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
    /// Celebrate: when a surviving Trainer's Assistant has Level >= Evolve,
    /// replace the exact card that spawned it with Veteran Assistant.
    /// Copies permanent card upgrades from the saved deck card.
    /// This changes the run deck at the end of the fight, not the in-battle unit.
    /// </summary>
    public sealed class CardEffectTrainersEvolveOnCelebrate : CardEffectBase
    {
        private const string LevelId = "hextrolls.plugin_level";
        private const string EvolveId = "hextrolls.plugin_evolve";
        private const string SourceCardName = "TrainersStarterCard";
        private const string TargetCardName = "TrainersVeteranAssistantCard";

        public override PropDescriptions CreateEditorInspectorDescriptions()
        {
            return new PropDescriptions();
        }

        public override bool TestEffect(CardEffectState effect, CardEffectParams parameters,
            ICoreGameManagers managers)
        {
            CharacterState unit = parameters != null ? parameters.selfTarget : null;
            return unit != null && managers != null &&
                managers.GetSaveManager() != null &&
                !managers.GetSaveManager().PreviewMode;
        }

        public override IEnumerator ApplyEffect(CardEffectState effect, CardEffectParams parameters,
            ICoreGameManagers managers, ISystemManagers systems)
        {
            CharacterState unit = parameters != null ? parameters.selfTarget : null;
            if (unit == null)
                yield break;

            var save = managers != null ? managers.GetSaveManager() : null;
            var gameData = managers != null ? managers.GetAllGameData() : null;
            if (save == null || save.PreviewMode || gameData == null)
                yield break;

            // The destination is registered by TrainersClan.json. Resolve by
            // its Trainworks asset name, with a short-name fallback.
            CardData evolvedData = FindRegisteredCard(gameData, TargetCardName);
            CardData sourceData = FindRegisteredCard(gameData, SourceCardName);
            if (evolvedData == null || sourceData == null)
            {
                Warn("Destination or source card wasn't registered. Verify TrainersClan.json is loaded.");
                yield break;
            }

            CardState spawner = unit.GetSpawnerCard();
            if (spawner == null)
            {
                Warn("The unit has no spawner card. Not replacing a random deck copy.");
                yield break;
            }
            if (!string.Equals(spawner.GetCardDataID(), sourceData.GetID(), StringComparison.Ordinal))
            {
                Warn("The triggering unit wasn't summoned from the expected source card.");
                yield break;
            }

            var deck = save.GetDeckState();
            if (deck == null)
                yield break;

            // Match exact identity first. When the game uses a separate combat
            // CardState, only accept a type-based fallback if ONE copy exists.
            CardState oldCard = deck.FirstOrDefault(c => c != null &&
                (object.ReferenceEquals(c, spawner) || object.Equals(c.GetID(), spawner.GetID())));
            if (oldCard == null)
            {
                var matches = deck.Where(c => c != null &&
                    string.Equals(c.GetCardDataID(), sourceData.GetID(), StringComparison.Ordinal)).ToList();
                if (matches.Count == 1)
                    oldCard = matches[0];
                else
                {
                    Warn("Couldn't identify one unique saved card copy (matches=" + matches.Count +
                         "). Evolution skipped to avoid changing another copy.");
                    yield break;
                }
            }
            if (!string.Equals(oldCard.GetCardDataID(), sourceData.GetID(), StringComparison.Ordinal))
            {
                Warn("Saved card did not match Trainer's Assistant. Evolution cancelled.");
                yield break;
            }

            int requirement = unit.GetStatusEffectStacks(EvolveId);
            if (requirement <= 0)
            {
                // Trainer's Assistant is the only supported source in this first
                // implementation, and its configured Evolve requirement is 16.
                requirement = 16;
                Warn("Evolve status was unavailable during Celebrate; using the configured Evolve 16 requirement.");
            }

            int savedLevel;
            if (!TryReadPermanentLevel(oldCard, out savedLevel))
            {
                Warn("Could not read saved Level for " + oldCard.GetID() +
                     "; evolution cancelled instead of using the stale spawned unit status.");
                yield break;
            }
            Info("Celebrate: card=" + oldCard.GetID() +
                 ", saved Level=" + savedLevel +
                 ", live Level=" + unit.GetStatusEffectStacks(LevelId) +
                 ", Evolve requirement=" + requirement);
            if (savedLevel < requirement)
                yield break;

            // A copied upgrade state retains its values (including shop upgrades).
            // Copy only the saved card's permanent upgrades, not temporary combat buffs.
            var carriedModifiers = new CardStateModifiers();
            var sourceModifiers = oldCard.GetCardStateModifiers();
            if (sourceModifiers != null)
            {
                var upgrades = sourceModifiers.GetCardUpgrades();
                if (upgrades != null)
                {
                    foreach (CardUpgradeState upgrade in upgrades)
                        if (upgrade != null)
                            carriedModifiers.AddUpgrade(upgrade);
                }
            }

            CardState newCard = null;
            try
            {
                // Create first, then remove the original. If addition fails,
                // the existing card remains safely in the deck.
                newCard = save.AddCardToDeck(
                    evolvedData, carriedModifiers,
                    false, 0, false, false, true, false, true);
                if (newCard == null)
                {
                    Warn("AddCardToDeck returned null, original card preserved.");
                }
                else
                {
                    save.RemoveCardFromDeck(oldCard);
                    newCard.UpdateCardBodyText();
                    global::HexTrolls.Plugin.Plugin.Logger.LogInfo(
                        "[TrainersEvolve] Trainer's Assistant evolved to Veteran Assistant on Celebrate, " +
                        "with its saved card upgrades carried over.");
                }
            }
            catch (Exception ex)
            {
                Warn("Evolution failed: " + ex);
                // Do not attempt a rollback via the game API here; log the failure.
            }
        }

        // Same progression definition as CardEffectTrainersGainExp:
        // Level = 1 + count of persistent stat bonus upgrades. The base marker
        // contributes Level 1 to the CARD DISPLAY, not another level-up.
        private static bool TryReadPermanentLevel(CardState card, out int level)
        {
            level = 1;
            if (card == null || card.GetCardStateModifiers() == null)
                return false;
            var upgrades = card.GetCardStateModifiers().GetCardUpgrades();
            if (upgrades == null)
                return false;

            var registry = Railend.GetContainer().GetInstance<IRegister<CardUpgradeData>>();
            if (registry == null)
                return false;

            var levelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] names = {
                "TrainersStarterLevelStatBonus",
                "TrainersBannerLevelStatBonus",
                "TrainersVeteranLevelStatBonus"
            };
            foreach (string name in names)
            {
                CardUpgradeData definition;
                if (!registry.TryGetValue(MyPluginInfo.PLUGIN_GUID + "-Upgrade-" + name,
                    out definition) || definition == null)
                    return false;
                var probe = new CardUpgradeState();
                probe.Setup(definition, false, false);
                string id = probe.GetCardUpgradeDataId();
                if (string.IsNullOrEmpty(id))
                    return false;
                levelIds.Add(id);
            }
            foreach (CardUpgradeState saved in upgrades)
            {
                if (saved != null && levelIds.Contains(saved.GetCardUpgradeDataId()))
                    level++;
            }
            return true;
        }

        private static CardData FindRegisteredCard(AllGameData data, string shortName)
        {
            CardData card = data.FindCardDataByName("HexTrolls.Plugin-Card-" + shortName);
            if (card != null) return card;
            card = data.FindCardDataByName(shortName);
            if (card != null) return card;
            // Some Trainworks versions prefix assets slightly differently.
            return data.GetAllCardData().FirstOrDefault(c => c != null &&
                c.name != null &&
                (c.name == shortName || c.name.EndsWith("-Card-" + shortName,
                    StringComparison.OrdinalIgnoreCase)));
        }

        private static void Info(string message)
        {
            global::HexTrolls.Plugin.Plugin.Logger.LogInfo("[TrainersEvolve] " + message);
        }

        private static void Warn(string message)
        {
            global::HexTrolls.Plugin.Plugin.Logger.LogWarning("[TrainersEvolve] " + message);
        }
    }
}
