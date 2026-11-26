// Red's three Trainers champion path rewards — enemy, capture, and EXP code unchanged.
// Path 1 -> Garhound L5; Path 2 -> Teslasaurus L5; Path 3 -> Frosset L5.
// Native champion upgrade selection calls CardState.ApplyPermanentUpgrade.
// We listen ONLY for the first-tier upgrade of a path on Red's actual champion card.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TrainworksReloaded.Core;
using TrainworksReloaded.Core.Interfaces;

namespace HexTrolls.Plugin.code
{
    [HarmonyPatch]
    internal static class TrainersRedChampionPathRewards
    {
        private const string RedCardKey = "TrainersRedChampionCard";
        private const string LevelBaseKey = "TrainersLevelBaseMarker";
        private sealed class Reward
        {
            internal string PathFirstUpgrade;
            internal string NewCard;
            internal string FormLevelBonus;
            internal string Display;
        }

        private static readonly Reward[] Rewards =
        {
            new Reward { PathFirstUpgrade = "TrainersRedPathI",
                NewCard = "TrainersPocketMonGarhoundForm1Card",
                FormLevelBonus = "TrainersPocketMonGarhoundForm1LevelStatBonus",
                Display = "Garhound" },
            new Reward { PathFirstUpgrade = "TrainersRedTeslaPathI",
                NewCard = "TrainersPocketMonTeslasaurusForm1Card",
                FormLevelBonus = "TrainersPocketMonTeslasaurusForm1LevelStatBonus",
                Display = "Teslasaurus" },
            new Reward { PathFirstUpgrade = "TrainersRedFrostPathI",
                NewCard = "TrainersPocketMonFrossetForm1Card",
                FormLevelBonus = "TrainersPocketMonFrossetForm1LevelStatBonus",
                Display = "Frosset" }
        };
        private static readonly ConditionalWeakTable<CardState, HashSet<string>> Awarded =
            new ConditionalWeakTable<CardState, HashSet<string>>();
        private static readonly object Sync = new object();

        private static void Info(string msg) =>
            HexTrolls.Plugin.Plugin.Logger.LogInfo("[TrainersRedPathRewards] " + msg);
        private static void Warn(string msg) =>
            HexTrolls.Plugin.Plugin.Logger.LogWarning("[TrainersRedPathRewards] " + msg);

        // Locate the exact known overload rather than guessing at the number of arguments.
        private static IEnumerable<MethodBase> TargetMethods()
        {
            return typeof(CardState)
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.Name == "ApplyPermanentUpgrade" &&
                    m.GetParameters().Any(p => p.ParameterType == typeof(CardUpgradeState)));
        }

        [HarmonyPrefix]
        private static void Prefix(CardState __instance, object[] __args, out bool __state)
        {
            // Was this *exact* first-tier path upgrade already on the champion?
            // If so, any reapplication or retrigger must NOT award a card again.
            __state = false;
            try
            {
                CardUpgradeState chosen = __args == null ? null :
                    __args.OfType<CardUpgradeState>().FirstOrDefault();
                if (chosen == null || __instance == null) return;
                var list = __instance.GetCardStateModifiers()?.GetCardUpgrades();
                if (list == null) return;
                string id = chosen.GetCardUpgradeDataId();
                __state = list.Any(item => item != null &&
                    string.Equals(item.GetCardUpgradeDataId(), id,
                        StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) { Warn("Upgrade precheck failed: " + ex.Message); }
        }

        [HarmonyPostfix]
        private static void Postfix(CardState __instance, object[] __args, bool __state)
        {
            if (__state || __instance == null || __args == null) return;
            try
            {
                var selected = __args.OfType<CardUpgradeState>().FirstOrDefault();
                var save = __args.OfType<SaveManager>().FirstOrDefault();
                if (selected == null || save == null || save.PreviewMode) return;
                var deck = save.GetDeckState();
                if (deck == null || !deck.Contains(__instance)) return;

                var container = Railend.GetContainer();
                if (container == null) return;
                var cards = container.GetInstance<IRegister<CardData>>();
                var upgrades = container.GetInstance<IRegister<CardUpgradeData>>();
                if (cards == null || upgrades == null) return;

                CardData red;
                if (!FindCard(cards, RedCardKey, out red) || red == null ||
                    !string.Equals(__instance.GetCardDataID(), red.GetID(),
                        StringComparison.OrdinalIgnoreCase)) return;

                string selectedId = selected.GetCardUpgradeDataId();
                foreach (Reward reward in Rewards)
                {
                    CardUpgradeData firstUpgrade;
                    if (!FindUpgrade(upgrades, reward.PathFirstUpgrade, out firstUpgrade))
                        continue;
                    string firstId = UpgradeId(firstUpgrade);
                    if (!string.Equals(firstId, selectedId,
                        StringComparison.OrdinalIgnoreCase)) continue;

                    // Only the successful FIRST selection grants the reward.
                    // The later tier-II/III upgrade IDs don't match, and the
                    // native champion-card record must actually contain tier I.
                    if (!__instance.GetCardStateModifiers().GetCardUpgrades().Any(x =>
                        x != null && string.Equals(x.GetCardUpgradeDataId(), firstId,
                            StringComparison.OrdinalIgnoreCase))) return;

                    lock (Sync)
                    {
                        var seen = Awarded.GetOrCreateValue(__instance);
                        if (seen.Contains(firstId)) return;
                        if (TryGrantLevelFive(save, cards, upgrades, reward))
                            seen.Add(firstId);
                    }
                    return;
                }
            }
            catch (Exception ex) { Warn("Could not award champion path card: " + ex); }
        }

        private static bool FindCard(IRegister<CardData> registry, string id, out CardData result)
        {
            foreach (string key in new [] {
                MyPluginInfo.PLUGIN_GUID + "-Card-" + id,
                MyPluginInfo.PLUGIN_GUID + "-CardData-" + id,
                MyPluginInfo.PLUGIN_GUID + "-" + id,
                id })
                if (registry.TryGetValue(key, out result) && result != null) return true;
            result = null;
            return false;
        }

        private static bool FindUpgrade(IRegister<CardUpgradeData> registry,
            string id, out CardUpgradeData result)
        {
            foreach (string key in new [] {
                MyPluginInfo.PLUGIN_GUID + "-Upgrade-" + id,
                MyPluginInfo.PLUGIN_GUID + "-" + id,
                id })
                if (registry.TryGetValue(key, out result) && result != null) return true;
            result = null;
            return false;
        }

        private static string UpgradeId(CardUpgradeData data)
        {
            var probe = new CardUpgradeState();
            probe.Setup(data, false, false);
            return probe.GetCardUpgradeDataId();
        }

        private static bool TryGrantLevelFive(SaveManager save, IRegister<CardData> cards,
            IRegister<CardUpgradeData> upgrades, Reward reward)
        {
            CardData template;
            CardUpgradeData baseLevel, formBonus;
            if (!FindCard(cards, reward.NewCard, out template) || template == null ||
                !FindUpgrade(upgrades, LevelBaseKey, out baseLevel) ||
                !FindUpgrade(upgrades, reward.FormLevelBonus, out formBonus))
            {
                Warn("Path reward not registered: " + reward.Display +
                     ". No card was added.");
                return false;
            }
            var before = new HashSet<CardState>(save.GetDeckState());
            string wantedId = template.GetID();
            try
            {
                save.AddCardToDeck(template, (CardStateModifiers)null,
                    false, 0, false, false, true, false, true);
            }
            catch (Exception ex)
            {
                Warn("AddCardToDeck failed for " + reward.Display + ": " + ex);
                return false;
            }
            var deck = save.GetDeckState();
            CardState added = deck.FirstOrDefault(c => c != null && !before.Contains(c) &&
                string.Equals(c.GetCardDataID(), wantedId,
                    StringComparison.OrdinalIgnoreCase));
            if (added == null)
            {
                Warn("No new permanent deck card found after awarding " + reward.Display);
                return false;
            }

            try
            {
                var modifiers = added.GetCardStateModifiers();
                var list = modifiers == null ? null :
                    modifiers.GetCardUpgrades() as IList<CardUpgradeState>;
                if (list == null) throw new InvalidOperationException("Upgrade list unavailable");
                string baseId = UpgradeId(baseLevel);
                string bonusId = UpgradeId(formBonus);
                if (string.IsNullOrEmpty(baseId) || string.IsNullOrEmpty(bonusId))
                    throw new InvalidOperationException("Upgrade ID unavailable");

                // Level 5 == exactly one Level 1 base marker + four of this
                // form's permanent Level+stat upgrades. Direct modifiers, just
                // like the working Capture Capsule; NO spawn-time recalculation.
                int bases = list.Count(u => u != null && u.GetCardUpgradeDataId() == baseId);
                if (bases == 0)
                {
                    var state = new CardUpgradeState();
                    state.Setup(baseLevel, false, false);
                    modifiers.AddUpgrade(state);
                }
                int bonuses = list.Count(u => u != null && u.GetCardUpgradeDataId() == bonusId);
                for (int n = bonuses; n < 4; n++)
                {
                    var state = new CardUpgradeState();
                    state.Setup(formBonus, false, false);
                    modifiers.AddUpgrade(state);
                }
                list = modifiers.GetCardUpgrades() as IList<CardUpgradeState>;
                if (list == null || list.Count(u => u != null &&
                        u.GetCardUpgradeDataId() == baseId) != 1 ||
                    list.Count(u => u != null && u.GetCardUpgradeDataId() == bonusId) != 4)
                    throw new InvalidOperationException("Level 5 upgrade count could not be verified");
                added.UpdateCardBodyText();
                Info("First selection of " + reward.PathFirstUpgrade +
                     " awarded permanent " + reward.Display + " Level 5 card. " +
                     "Total deck cards=" + deck.Count);
                return true;
            }
            catch (Exception ex)
            {
                // Don't leave a partial malformed reward in the permanent deck.
                deck.Remove(added);
                Warn("Rolled back partial " + reward.Display + " path reward: " + ex);
                return false;
            }
        }
    }
}
