using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TrainworksReloaded.Base;
using TrainworksReloaded.Core;
using TrainworksReloaded.Core.Interfaces;

namespace HexTrolls.Plugin.code
{
    /// <summary>
    /// Permanent progression reconciliation.
    ///
    /// Source of truth: saved deck card upgrades, never temporary unit stacks.
    /// Level = 1 + number of permanent Trainers level upgrades.
    /// EXP = sum of binary TrainersEXPBit upgrades (1, 2, 4 ...).
    ///
    /// param_int=0: Siphon/kill award + reconcile.
    /// param_int=1: reconcile only (Summon, Celebrate).
    ///
    /// On a level-up, spend 10 * current Level, keep leftovers, and support
    /// as many consecutive level-ups as the EXP permits.
    /// </summary>
    public sealed class CardEffectTrainersGainExp : CardEffectBase
    {
        private const int ExpPerLevel = 10;
        private const int ExpBitCount = 20;
        private const int MaxLevel = 100000;
        private const string LevelId = "hextrolls.plugin_level";
        private const string ExpId = "hextrolls.plugin_exp";

        public override PropDescriptions CreateEditorInspectorDescriptions()
        {
            return new PropDescriptions();
        }

        public override bool TestEffect(CardEffectState effect,
            CardEffectParams parameters, ICoreGameManagers managers)
        {
            return parameters != null && parameters.selfTarget != null &&
                !parameters.selfTarget.IsDestroyed && managers != null &&
                managers.GetSaveManager() != null &&
                !managers.GetSaveManager().PreviewMode;
        }

        public override IEnumerator ApplyEffect(CardEffectState effect,
            CardEffectParams parameters, ICoreGameManagers managers, ISystemManagers systems)
        {
            if (!TestEffect(effect, parameters, managers))
                yield break;

            CharacterState unit = parameters.selfTarget;
            SaveManager save = managers.GetSaveManager();
            CardState sourceCard = FindSavedSourceCard(unit, save);
            if (sourceCard == null)
            {
                Warn("Cannot reconcile: no uniquely identified permanent deck card " +
                     "for the triggering unit. Nothing changed.");
                yield break;
            }

            // Resolve the ACTUAL serialized upgrade IDs from registered templates.
            // Railhead's registration key is not guaranteed to be equal to
            // CardUpgradeState.GetCardUpgradeDataId() on a saved deck card.
            UpgradeIdCatalog catalog;
            if (!TryBuildUpgradeCatalog(out catalog))
            {
                Warn("Cannot reconcile: upgrade identity catalog is incomplete.");
                yield break;
            }

            CardStateModifiers modifiers = sourceCard.GetCardStateModifiers();
            if (modifiers == null)
            {
                Warn("Cannot reconcile: source deck card has no modifiers.");
                yield break;
            }
            IList<CardUpgradeState> savedUpgrades =
                modifiers.GetCardUpgrades() as IList<CardUpgradeState>;
            if (savedUpgrades == null)
            {
                Warn("Cannot reconcile: permanent upgrade list is not writable.");
                yield break;
            }

            // Migrate existing run cards (created before TrainersLevelBaseMarker
            // was added) and verify all new cards have their one base Level stack.
            // The marker has no stats: 1 Level comes from this marker, and
            // subsequent Levels come from the permanent stat upgrades.
            string baseLevelId = catalog.RuntimeIds["TrainersLevelBaseMarker"];
            if (CountUpgrade(savedUpgrades, baseLevelId) == 0)
            {
                var baseMarker = new CardUpgradeState();
                baseMarker.Setup(catalog.Templates["TrainersLevelBaseMarker"], false, false);
                modifiers.AddUpgrade(baseMarker);
                savedUpgrades = GetSavedUpgrades(modifiers);
                if (CountUpgrade(savedUpgrades, baseLevelId) != 1)
                {
                    Warn("Could not persist base Level 1 marker. Cancelling reconciliation.");
                    yield break;
                }
                Info("Migrated a saved Trainers card to the single permanent Level status.");
            }
            else if (CountUpgrade(savedUpgrades, baseLevelId) > 1)
            {
                Warn("Multiple base Level markers on one card. This run may have duplicate Level status displays.");
            }

            // CARD is authoritative. A character's initial Level 1 and its
            // permanent Level bonuses may not be combined in the UI; do not
            // make leveling decisions from those visible status stacks.
            int oldLevel = Math.Max(1, 1 + CountLevelUpgrades(savedUpgrades, catalog));
            long savedExp = ReadStoredExp(savedUpgrades, catalog);
            if (oldLevel == 1 && savedExp == 0 && savedUpgrades.Count > 0)
                Info("Saved card has " + savedUpgrades.Count +
                     " upgrades, but no recognized progression. IDs: " +
                     DescribeUpgrades(savedUpgrades));

            bool awardKill = effect != null && effect.GetParamInt() == 0;
            int earned = awardKill ? CalculateKillExp(unit, parameters.dyingCharacter) : 0;
            if (awardKill && parameters.dyingCharacter == null)
                Warn("Siphon ran without a dyingCharacter; reconciling existing EXP only.");

            long total = savedExp + (long)earned;
            int newLevel = oldLevel;
            while (newLevel < MaxLevel && total >= (long)newLevel * ExpPerLevel)
            {
                total -= (long)newLevel * ExpPerLevel;
                newLevel++;
            }
            int levelsGained = newLevel - oldLevel;
            if (total < 0 || total >= (1L << ExpBitCount))
            {
                Warn("Cannot encode " + total + " remaining EXP. Skipping change.");
                yield break;
            }

            CardUpgradeData levelBonus = effect != null ? effect.GetParamCardUpgradeData() : null;
            if (levelsGained > 0 && levelBonus == null)
            {
                Warn("Level-up needs a registered param_upgrade. No EXP spent.");
                yield break;
            }

            // Resolve templates and plan the new EXP markers BEFORE mutating
            // the card. On failure leave the original upgrade list intact.
            List<CardUpgradeData> newExpTemplates;
            if (!TryResolveExpTemplates((int)total, catalog, out newExpTemplates))
            {
                Warn("EXP marker registration incomplete. No EXP spent.");
                yield break;
            }
            if (!ReplaceExpMarkers(modifiers, savedUpgrades, newExpTemplates, catalog))
            {
                Warn("Failed to replace EXP markers. No levels added.");
                yield break;
            }
            long committedExpBeforeLevels = ReadStoredExp(
                GetSavedUpgrades(modifiers), catalog);
            if (committedExpBeforeLevels != total)
            {
                Warn("EXP failed to persist before level-up: expected " +
                     total + ", saved " + committedExpBeforeLevels +
                     ". Aborting stat changes. Card upgrades=" +
                     DescribeUpgrades(GetSavedUpgrades(modifiers)));
                yield break;
            }

            // Apply each level's combat stat bonus once. Some game versions
            // also update the source card in ApplyCardUpgrade; check the number
            // of matching upgrades before/after to prevent double-persisting.
            if (levelsGained > 0)
            {
                for (int i = 0; i < levelsGained; i++)
                {
                    if (unit.IsDestroyed)
                    {
                        Warn("Unit destroyed while processing level-up " + (i + 1));
                        break;
                    }
                    var upgrade = new CardUpgradeState();
                    upgrade.Setup(levelBonus, false, false);
                    string upgradeId = upgrade.GetCardUpgradeDataId();
                    int countBefore = CountUpgrade(GetSavedUpgrades(modifiers), upgradeId);
                    yield return unit.ApplyCardUpgrade(
                        upgrade, false, upgradeId, true,
                        false, null, null, null, false);
                    if (CountUpgrade(GetSavedUpgrades(modifiers), upgradeId) == countBefore)
                        modifiers.AddUpgrade(upgrade);
                    int after = CountUpgrade(GetSavedUpgrades(modifiers), upgradeId);
                    if (after != countBefore + 1)
                    {
                        Warn("Level bonus persistence failed: upgrade=" + upgradeId +
                             ", before=" + countBefore + ", after=" + after +
                             ". Check CardStateModifiers and ApplyCardUpgrade lifetime.");
                        break;
                    }
                }
            }

            // Calculate the Level actually stored after the upgrades have
            // landed, not the theoretical Level if an upgrade was interrupted.
            // Re-read permanent modifiers; never trust the old list reference
            // after ApplyCardUpgrade/Modifiers.AddUpgrade mutates it.
            IList<CardUpgradeState> committed = GetSavedUpgrades(modifiers);
            int finalLevel = Math.Max(1, 1 + CountLevelUpgrades(committed, catalog));
            int finalExp = (int)ReadStoredExp(committed, catalog);
            if (finalExp != (int)total || finalLevel != newLevel)
            {
                Warn("Progression did not commit as requested: wanted Level " +
                     newLevel + ", EXP " + total + "; stored Level " +
                     finalLevel + ", EXP " + finalExp +
                     ". Upgrade IDs=" + DescribeUpgrades(committed));
            }
            SyncLiveStatus(unit, FindStatusId(unit, LevelId, "level"), finalLevel);
            SyncLiveStatus(unit, FindStatusId(unit, ExpId, "exp"), finalExp);
            sourceCard.UpdateCardBodyText();

            Info((awardKill ? "Siphon" : "Reconcile") +
                 ": card=" + sourceCard.GetID() +
                 ", Level " + oldLevel + " -> " + finalLevel +
                 ", saved EXP " + savedExp + " + " + earned +
                 " earned -> " + finalExp +
                 ", level-ups " + (finalLevel - oldLevel));
        }

        private static int CalculateKillExp(CharacterState unit, CharacterState slain)
        {
            if (slain == null)
                return 0;
            RoomState floor = unit.GetCurrentRoom();
            if (floor == null)
            {
                Warn("Siphon recipient is no longer on a floor. No kill reward.");
                return 0;
            }

            var allies = new List<CharacterState>();
            floor.AddCharactersToList(allies, Team.Type.Monsters);
            int living = allies.Count(c => c != null && c.IsAlive && !c.IsDestroyed);
            if (living < 1)
                return 0;

            // Keep existing rule: enemy Max HP + Attack, divided equally among
            // surviving allies on the floor, including allies without Level.
            long value = (long)Math.Max(0, slain.GetMaxHP()) +
                         Math.Max(0, slain.GetAttackDamage());
            return (int)Math.Min(int.MaxValue, value / living);
        }

        // A Trainworks registration key (used by IRegister.TryGetValue) is
        // NOT the authoritative CardUpgradeState ID saved in a run.
        // Build a probe CardUpgradeState from the registered CardUpgradeData
        // and compare its GetCardUpgradeDataId() against the saved cards.
        private sealed class UpgradeIdCatalog
        {
            public readonly Dictionary<string, string> RuntimeIds =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, CardUpgradeData> Templates =
                new Dictionary<string, CardUpgradeData>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> ExpBits =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> LevelIds =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private static bool TryBuildUpgradeCatalog(out UpgradeIdCatalog catalog)
        {
            catalog = new UpgradeIdCatalog();
            var registry = Railend.GetContainer().GetInstance<IRegister<CardUpgradeData>>();
            if (registry == null)
            {
                Warn("CardUpgradeData registry is null.");
                return false;
            }

            if (!RegisterRuntimeId(registry, catalog, "TrainersLevelBaseMarker"))
                return false;
            string[] levelNames = {
                "TrainersStarterLevelStatBonus",
                "TrainersBannerLevelStatBonus",
                "TrainersVeteranLevelStatBonus"
            };
            for (int i = 0; i < levelNames.Length; i++)
            {
                if (!RegisterRuntimeId(registry, catalog, levelNames[i]))
                    return false;
                catalog.LevelIds.Add(catalog.RuntimeIds[levelNames[i]]);
            }
            for (int bit = 0; bit < ExpBitCount; bit++)
            {
                string name = "TrainersEXPBit" + bit.ToString("D2");
                if (!RegisterRuntimeId(registry, catalog, name))
                    return false;
                string runtimeId = catalog.RuntimeIds[name];
                if (catalog.ExpBits.ContainsKey(runtimeId))
                {
                    Warn("Duplicate runtime EXP marker ID: " + runtimeId);
                    return false;
                }
                catalog.ExpBits.Add(runtimeId, bit);
            }
            return true;
        }

        private static bool RegisterRuntimeId(IRegister<CardUpgradeData> registry,
            UpgradeIdCatalog catalog, string shortName)
        {
            string registrationKey = MyPluginInfo.PLUGIN_GUID + "-Upgrade-" + shortName;
            CardUpgradeData template;
            if (!registry.TryGetValue(registrationKey, out template) || template == null)
            {
                Warn("Missing registered upgrade " + registrationKey);
                return false;
            }
            var sample = new CardUpgradeState();
            sample.Setup(template, false, false);
            string runtimeId = sample.GetCardUpgradeDataId();
            if (string.IsNullOrEmpty(runtimeId))
            {
                Warn("Registered upgrade has no serialized ID: " + registrationKey);
                return false;
            }
            catalog.Templates.Add(shortName, template);
            catalog.RuntimeIds.Add(shortName, runtimeId);
            return true;
        }

        private static IList<CardUpgradeState> GetSavedUpgrades(CardStateModifiers modifiers)
        {
            return modifiers != null
                ? modifiers.GetCardUpgrades() as IList<CardUpgradeState> : null;
        }

        private static int CountLevelUpgrades(IList<CardUpgradeState> upgrades,
            UpgradeIdCatalog catalog)
        {
            if (upgrades == null) return 0;
            int count = 0;
            foreach (CardUpgradeState upgrade in upgrades)
            {
                string id = upgrade != null ? upgrade.GetCardUpgradeDataId() : null;
                if (id != null && catalog.LevelIds.Contains(id))
                    count++;
            }
            return count;
        }

        private static int CountUpgrade(IList<CardUpgradeState> upgrades, string id)
        {
            if (upgrades == null || string.IsNullOrEmpty(id)) return 0;
            return upgrades.Count(u => u != null && string.Equals(
                u.GetCardUpgradeDataId(), id, StringComparison.OrdinalIgnoreCase));
        }

        private static long ReadStoredExp(IList<CardUpgradeState> upgrades,
            UpgradeIdCatalog catalog)
        {
            if (upgrades == null) return 0;
            long total = 0;
            foreach (CardUpgradeState upgrade in upgrades)
            {
                int bit;
                string id = upgrade != null ? upgrade.GetCardUpgradeDataId() : null;
                if (id != null && catalog.ExpBits.TryGetValue(id, out bit))
                    total += 1L << bit;
            }
            return total;
        }

        private static bool TryResolveExpTemplates(int exp,
            UpgradeIdCatalog catalog, out List<CardUpgradeData> templates)
        {
            templates = new List<CardUpgradeData>();
            for (int bit = 0; bit < ExpBitCount; bit++)
            {
                if ((exp & (1 << bit)) == 0)
                    continue;
                CardUpgradeData template;
                if (!catalog.Templates.TryGetValue(
                        "TrainersEXPBit" + bit.ToString("D2"), out template))
                    return false;
                templates.Add(template);
            }
            return true;
        }

        private static bool ReplaceExpMarkers(CardStateModifiers modifiers,
            IList<CardUpgradeState> upgrades, List<CardUpgradeData> templates,
            UpgradeIdCatalog catalog)
        {
            var before = new List<CardUpgradeState>(upgrades);
            try
            {
                for (int i = upgrades.Count - 1; i >= 0; --i)
                {
                    string id = upgrades[i] != null
                        ? upgrades[i].GetCardUpgradeDataId() : null;
                    if (id != null && catalog.ExpBits.ContainsKey(id))
                        upgrades.RemoveAt(i);
                }
                foreach (CardUpgradeData template in templates)
                {
                    var marker = new CardUpgradeState();
                    marker.Setup(template, false, false);
                    modifiers.AddUpgrade(marker);
                }
                return true;
            }
            catch (Exception ex)
            {
                Warn("EXP marker update failed: " + ex);
                try
                {
                    upgrades.Clear();
                    foreach (CardUpgradeState old in before)
                        upgrades.Add(old);
                }
                catch (Exception rollback)
                {
                    Warn("EXP rollback also failed: " + rollback);
                }
                return false;
            }
        }

        private static string DescribeUpgrades(IList<CardUpgradeState> upgrades)
        {
            if (upgrades == null) return "(null)";
            return string.Join(", ", upgrades.Where(u => u != null)
                .Take(16).Select(u => u.GetCardUpgradeDataId()).ToArray());
        }

        private static void SyncLiveStatus(CharacterState unit, string id, int desired)
        {
            int current = unit.GetStatusEffectStacks(id);
            if (current == desired)
                return;
            if (current > 0)
                unit.RemoveStatusEffect(id, current, false);
            if (desired > 0)
                unit.AddStatusEffect(id, desired, (CharacterState)null, false, false);
        }

        private static string FindStatusId(CharacterState unit, string canonicalId,
            string shortName)
        {
            if (unit.GetStatusEffectStacks(canonicalId) > 0)
                return canonicalId;
            var statuses = new List<CharacterState.StatusEffectStack>();
            unit.GetStatusEffects(ref statuses);
            foreach (CharacterState.StatusEffectStack entry in statuses)
            {
                if (entry.State == null)
                    continue;
                string id = entry.State.GetStatusId();
                if (id != null &&
                    id.StartsWith("hextrolls.plugin", StringComparison.OrdinalIgnoreCase) &&
                    (id.EndsWith("_" + shortName, StringComparison.OrdinalIgnoreCase) ||
                     id.EndsWith("-" + shortName, StringComparison.OrdinalIgnoreCase)))
                    return id;
            }
            return canonicalId;
        }

        private static CardState FindSavedSourceCard(CharacterState unit, SaveManager save)
        {
            if (save == null)
            {
                Warn("Card lookup failed: SaveManager is null.");
                return null;
            }
            if (unit == null)
            {
                Warn("Card lookup failed: triggering unit is null.");
                return null;
            }

            var deck = save.GetDeckState();
            if (deck == null)
            {
                Warn("Card lookup failed: GetDeckState() returned null.");
                return null;
            }

            CardState spawner = unit.GetSpawnerCard();
            if (spawner == null)
            {
                Warn("Card lookup failed: GetSpawnerCard() is null; deckCount=" + deck.Count +
                     ". Was the unit summoned without playing a permanent deck card?");
                return null;
            }

            CardState direct = deck.FirstOrDefault(c =>
                c != null && object.ReferenceEquals(c, spawner));
            if (direct != null)
                return direct;

            var matchingId = deck.Where(c => c != null &&
                object.Equals(c.GetID(), spawner.GetID())).Take(2).ToList();
            if (matchingId.Count == 1)
                return matchingId[0];
            if (matchingId.Count > 1)
            {
                Warn("Card lookup ambiguous: multiple deck cards have spawner ID=" +
                     spawner.GetID() + "; spawnerCardData=" + spawner.GetCardDataID() +
                     "; deckCount=" + deck.Count + ". Leaving progress unchanged.");
                return null;
            }

            var sameType = deck.Where(c => c != null &&
                string.Equals(c.GetCardDataID(), spawner.GetCardDataID(),
                    StringComparison.Ordinal)).Take(3).ToList();
            if (sameType.Count == 1)
                return sameType[0];

            Warn("Card lookup failed: spawnerID=" + spawner.GetID() +
                 "; spawnerCardData=" + spawner.GetCardDataID() +
                 "; deckCount=" + deck.Count +
                 "; exactIDMatches=0; sameCardDataMatches=" +
                 (sameType.Count == 3 ? "3 or more" : sameType.Count.ToString()) +
                 ". " + (sameType.Count == 0
                     ? "The original card may have left the run deck (including via Cheat GUI)."
                     : "Multiple card copies are present; cannot safely identify which to update."));
            return null;
        }

        private static void Info(string message)
        {
            global::HexTrolls.Plugin.Plugin.Logger.LogInfo("[TrainersEXP] " + message);
        }
        private static void Warn(string message)
        {
            global::HexTrolls.Plugin.Plugin.Logger.LogWarning("[TrainersEXP] " + message);
        }
    }
}
