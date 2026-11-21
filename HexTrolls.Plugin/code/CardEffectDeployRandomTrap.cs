using System;
using System.Collections;
using TrainworksReloaded.Base;
using TrainworksReloaded.Base.Extensions;
using TrainworksReloaded.Core;
using TrainworksReloaded.Core.Interfaces;
using UnityEngine;

namespace HexTrolls.Plugin.code
{
    /// <summary>
    /// Deploys one of the three existing Bounty Clan trap units without
    /// inheriting upgrades/equipment from the unit using Deploy Trap.
    ///
    /// We deliberately do NOT replay the trap cards' own spawn effects:
    /// those effects have param_bool=false by default, which lets the native
    /// spawner copy the source's upgrades (including Trapping Gear).
    /// Instead, use dedicated native CardEffectSpawnMonster effects with
    /// param_bool=true from the hidden TrappingGearTrapSpawnEffects card.
    /// </summary>
    public sealed class CardEffectDeployRandomTrap : CardEffectBase
    {
        private const string SpawnEffectsCardId = "TrappingGearTrapSpawnEffects";

        // Must match the order in TrappingGearTrapSpawnEffects.effects.
        private static readonly string[] TrapNames =
        {
            "Concussive Trap",
            "Blast Trap",
            "Ambush Trap"
        };

        public override bool CanPlayAfterBossDead => false;
        public override bool CanApplyInPreviewMode => false;

        public override PropDescriptions CreateEditorInspectorDescriptions()
        {
            return new PropDescriptions { };
        }

        public override IEnumerator ApplyEffect(
            CardEffectState cardEffectState,
            CardEffectParams cardEffectParams,
            ICoreGameManagers coreGameManagers,
            ISystemManagers systemManagers)
        {
            var saveManager = coreGameManagers?.GetSaveManager();
            if (saveManager == null || saveManager.PreviewMode || cardEffectParams == null)
                yield break;

            var cardRegistry = Railend.GetContainer().GetInstance<IRegister<CardData>>();
            if (cardRegistry == null)
            {
                Debug.LogWarning("[TrappingGear] Card registry unavailable; no trap spawned.");
                yield break;
            }

            string fullId = MyPluginInfo.PLUGIN_GUID.GetId(
                TemplateConstants.Card, SpawnEffectsCardId);

            if (!cardRegistry.TryGetValue(fullId, out CardData helperCard) || helperCard == null)
            {
                Debug.LogWarning($"[TrappingGear] Internal spawn card '{fullId}' not found. " +
                    "Make sure the updated TrappingGear.json is registered.");
                yield break;
            }

            var spawnEffects = helperCard.GetEffects();
            if (spawnEffects == null || spawnEffects.Count != TrapNames.Length)
            {
                Debug.LogWarning($"[TrappingGear] Internal spawn card must have exactly " +
                    $"{TrapNames.Length} effects; found {spawnEffects?.Count ?? 0}.");
                yield break;
            }

            int roll = UnityEngine.Random.Range(0, TrapNames.Length);
            var chosenSpawnData = spawnEffects[roll];
            if (chosenSpawnData == null)
            {
                Debug.LogWarning($"[TrappingGear] Missing native spawn effect at index {roll}.");
                yield break;
            }

            Debug.Log($"[TrappingGear] Deploy Trap rolled '{TrapNames[roll]}' " +
                $"in room {cardEffectParams.selectedRoom} (copy upgrades: OFF).");

            // Keep room/ability context so the native effect can spawn normally.
            // The selected native effect has param_bool=true, which disables
            // copying Trapping Gear from the unit activating the ability.
            var spawnState = new CardEffectState();
            spawnState.Setup(chosenSpawnData, null);
            var spawnEffect = spawnState.GetCardEffect();

            if (spawnEffect == null ||
                !spawnEffect.TestEffect(spawnState, cardEffectParams, coreGameManagers))
            {
                Debug.LogWarning($"[TrappingGear] Native spawn check failed for '{TrapNames[roll]}'.");
                yield break;
            }

            yield return spawnEffect.ApplyEffect(
                spawnState, cardEffectParams, coreGameManagers, systemManagers);
        }
    }
}
