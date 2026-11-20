using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TrainworksReloaded.Base;
using TrainworksReloaded.Core;
using TrainworksReloaded.Core.Interfaces;
using UnityEngine;

namespace HexTrolls.Plugin.code
{
    /// <summary>
    /// Grants Commander bonuses while respecting the same current upgrade-slot
    /// limit that the game's deck upgrade screen uses. Applies only to Mercenaries.
    /// Shop-lock filtering is intentionally NOT used for these automatic bonuses.
    /// </summary>
    public sealed class GrantRandomMercenaryUpgradeEffect : CardEffectBase
    {
        private const string MercenaryStatusId = "hextrolls.plugin_mercenary";

        public override bool CanPlayAfterBossDead => false;
        public override bool CanApplyInPreviewMode => false;

        public override PropDescriptions CreateEditorInspectorDescriptions()
        {
            return new PropDescriptions { };
        }

        public override bool TestEffect(
            CardEffectState cardEffectState,
            CardEffectParams cardEffectParams,
            ICoreGameManagers coreGameManagers)
        {
            return cardEffectParams?.targets != null &&
                   cardEffectParams.targets.Any(t => t != null && t.IsAlive &&
                       t.HasStatusEffect(MercenaryStatusId));
        }

        public override IEnumerator ApplyEffect(
            CardEffectState cardEffectState,
            CardEffectParams cardEffectParams,
            ICoreGameManagers coreGameManagers,
            ISystemManagers sysManagers)
        {
            var saveManager = coreGameManagers?.GetSaveManager();
            if (saveManager == null || saveManager.PreviewMode)
                yield break;

            CharacterState target = cardEffectParams?.targets?.FirstOrDefault(t =>
                t != null && t.IsAlive && t.HasStatusEffect(MercenaryStatusId));
            if (target == null)
                yield break;

            int tier = cardEffectState != null ? cardEffectState.GetParamInt() : 1;
            yield return ApplyUpgradeIfSpace(target, tier, saveManager);
        }

        // The HUD directly spawns characters (it doesn't play unit cards).
        // Keep this path equivalent to ApplyEffect's trigger path.
        public static IEnumerator ApplyRandomUpgradeToUnit(CharacterState target, int tier)
        {
            var saveManager = AllGameManagers.Instance?.GetSaveManager();
            if (saveManager == null || saveManager.PreviewMode)
                yield break;
            yield return ApplyUpgradeIfSpace(target, tier, saveManager);
        }

        private static RelicManager GetCurrentRelicManager()
        {
            // Avoid binding to a potentially version-specific manager accessor.
            object allManagers = AllGameManagers.Instance;
            if (allManagers == null)
                return null;

            object[] owners = { allManagers, AllGameManagers.Instance.GetCoreManagers() };
            foreach (var owner in owners)
            {
                if (owner == null)
                    continue;
                try
                {
                    MethodInfo method = owner.GetType().GetMethods(
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .FirstOrDefault(m => m.Name == "GetRelicManager" &&
                                             m.GetParameters().Length == 0);
                    if (method != null && method.Invoke(owner, null) is RelicManager result)
                        return result;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[MercSlots] Could not obtain RelicManager: " + ex.Message);
                }
            }
            return null;
        }

        /// <summary>
        /// These are the exact two checks used by Monster Train 2's deck upgrade
        /// screen for a unit upgrade: visible upgrades used vs. current allowed
        /// slots. GetUpgradeSlots reads the card's slot-expanding modifiers; if
        /// available we also pass active relic effects that modify slot counts.
        /// Fail closed rather than accidentally adding upgrades beyond capacity.
        /// </summary>
        private static bool HasFreeUpgradeSlot(CharacterState target, SaveManager saveManager)
        {
            CardState card = target?.GetSpawnerCard();
            if (card == null)
            {
                Debug.LogWarning("[MercSlots] Missing spawner card; skipping upgrade to avoid exceeding slots.");
                return false;
            }

            try
            {
                RelicManager relicManager = GetCurrentRelicManager();
                var slotRelicEffects = relicManager != null
                    ? relicManager.GetRelicEffects<IModifyCardUpgradeSlotCountRelicEffect>(
                        new List<IModifyCardUpgradeSlotCountRelicEffect>())
                    : null;

                int used = card.GetVisibleUpgradeCount(true, false);
                int maximum = saveManager.GetBalanceData().GetUpgradeSlots(
                    card, slotRelicEffects, relicManager);

                Debug.Log($"[MercSlots] unit='{target.GetSourceCharacterData()?.name}' " +
                          $"used={used} maximum={maximum} available={Mathf.Max(0, maximum - used)} " +
                          $"relicManagerFound={relicManager != null}");
                if (maximum <= 0 || used >= maximum)
                {
                    Debug.Log($"[MercSlots] No free slot; skipping Commander upgrade for '{target.GetName()}'.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError("[MercSlots] Could not determine live upgrade-slot capacity. " +
                               "Skipping bonus to prevent overflow: " + ex);
                return false;
            }
        }

        private static IEnumerator ApplyUpgradeIfSpace(CharacterState target, int tier,
            SaveManager saveManager)
        {
            if (target == null || !target.IsAlive || !target.HasStatusEffect(MercenaryStatusId))
                yield break;

            // Check BEFORE EACH upgrade. Commander II/III invoke this coroutine
            // multiple times; the next invocation sees the previous new upgrade.
            if (!HasFreeUpgradeSlot(target, saveManager))
                yield break;

            string poolName = tier >= 2 ? "UnitUpgradePoolRare" : "UnitUpgradePoolCommon";
            var register = Railend.GetContainer().GetInstance<IRegister<EnhancerPool>>();
            if (register == null || !register.TryGetValue(poolName, out EnhancerPool pool) || pool == null)
            {
                Debug.LogWarning($"[MercSlots] Enhancer pool '{poolName}' unavailable.");
                yield break;
            }

            var choices = new List<EnhancerData>();
            pool.GetAllChoices(choices);
            var validUpgrades = new List<CardUpgradeData>();
            foreach (var enhancer in choices)
            {
                CardUpgradeData upgrade = enhancer?.GetEffects()?.FirstOrDefault()
                    ?.GetParamCardUpgradeData();
                if (upgrade != null)
                    validUpgrades.Add(upgrade);
            }
            if (validUpgrades.Count == 0)
            {
                Debug.LogWarning($"[MercSlots] No valid upgrades in '{poolName}'.");
                yield break;
            }

            CardUpgradeData selected = validUpgrades[UnityEngine.Random.Range(0, validUpgrades.Count)];
            var upgradeState = new CardUpgradeState();
            upgradeState.Setup(selected);

            yield return target.ApplyCardUpgrade(upgradeState);

            // A cardless HUD spawn can still expose a spawner CardState; carry its
            // bonus there so Recruit can copy it into the permanent deck card.
            CardState spawnerCard = target.GetSpawnerCard();
            var modifiers = spawnerCard?.GetCardStateModifiers();
            if (modifiers != null)
            {
                var upgrades = modifiers.GetCardUpgrades();
                if (upgrades == null || !upgrades.Contains(upgradeState))
                    modifiers.AddUpgrade(upgradeState);
                spawnerCard.UpdateCardBodyText(saveManager);
            }

            Debug.Log($"[MercSlots] Applied '{selected.name}' to '{target.GetName()}' " +
                      $"from '{poolName}'.");
        }
    }
}
