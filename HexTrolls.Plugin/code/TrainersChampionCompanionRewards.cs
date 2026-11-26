// Trainers champion paths -> one permanent Level 5 PocketMon per chosen Level I.
// VERSION 2: resolve registered runtime GUIDs instead of comparing them to JSON names.
// Replace the older TrainersChampionCompanionRewards.cs; do not keep both versions.
// Reuses the EXISTING TrainersPocketMonCapture.TryAddPermanentCard helper.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TrainworksReloaded.Base;
using TrainworksReloaded.Core;
using TrainworksReloaded.Core.Interfaces;

namespace HexTrolls.Plugin.code
{
    [HarmonyPatch]
    internal static class TrainersChampionCompanionRewards
    {
        private sealed class Reward
        {
            internal readonly string UpgradeKey;
            internal readonly string ChampionKey;
            internal readonly string CompanionKey;
            internal readonly string Name;

            internal Reward(string upgradeKey, string championKey, string companionKey, string name)
            {
                UpgradeKey = upgradeKey;
                ChampionKey = championKey;
                CompanionKey = companionKey;
                Name = name;
            }
        }

        private static readonly Reward[] Rewards =
        {
            new Reward("TrainersRedPathI", "TrainersRedChampionCard", "TrainersPocketMonGarhoundForm1Card", "Garhound"),
            new Reward("TrainersRedTeslaPathI", "TrainersRedChampionCard", "TrainersPocketMonTeslasaurusForm1Card", "Teslasaurus"),
            new Reward("TrainersRedFrostPathI", "TrainersRedChampionCard", "TrainersPocketMonFrossetForm1Card", "Frosset"),
            new Reward("TrainersBluePathI", "TrainersBlueChampionCard", "TrainersPocketMonSnapsaurusForm1Card", "Snapsaurus"),
            // The source JSON really spells the species Cuttlfish, not Cuttlefish.
            new Reward("TrainersBlueCuttlefishPathI", "TrainersBlueChampionCard", "TrainersPocketMonCuttlfishForm1Card", "Cuttlefish"),
            new Reward("TrainersBlueManshrimpPathI", "TrainersBlueChampionCard", "TrainersPocketMonManshrimpForm1Card", "Manshrimp")
        };

        // Prevent duplicate award if several Harmony patches fire for one selection.
        // CardState is unique to a run; new runs receive new champion CardStates.
        private static readonly Dictionary<CardState, HashSet<string>> Awarded =
            new Dictionary<CardState, HashSet<string>>();

        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            int count = 0;
            foreach (MethodInfo method in typeof(CardState).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name != "ApplyPermanentUpgrade") continue;
                var parameters = method.GetParameters();
                if (parameters.Length < 2 ||
                    parameters[0].ParameterType != typeof(CardUpgradeState) ||
                    parameters[1].ParameterType != typeof(SaveManager)) continue;
                ++count;
                Info("Hooking CardState." + method.Name + "(" +
                     String.Join(", ", parameters.Select(p => p.ParameterType.Name).ToArray()) + ")");
                yield return method;
            }
            if (count == 0)
                Warn("NO compatible CardState.ApplyPermanentUpgrade overload found. The reward hook is NOT installed.");
        }

        [HarmonyPostfix]
        private static void Postfix(CardState __instance, CardUpgradeState __0, SaveManager __1)
        {
            try { GrantForUpgrade(__instance, __0, __1); }
            catch (Exception ex) { Warn("Reward handler exception: " + ex); }
        }

        private static void GrantForUpgrade(CardState champion, CardUpgradeState upgrade, SaveManager save)
        {
            if (champion == null || upgrade == null || save == null || save.PreviewMode)
                return;
            if (!champion.IsChampionCard()) return;

            string appliedId = upgrade.GetCardUpgradeDataId();
            string championId = champion.GetCardDataID();
            if (String.IsNullOrEmpty(appliedId) || String.IsNullOrEmpty(championId)) return;

            // This log is important: v1 had no logging when a runtime GUID failed
            // to match a JSON name, making it impossible to diagnose the issue.
            Info("Champion upgrade observed: upgradeRuntimeId=" + appliedId +
                 "; championRuntimeId=" + championId);

            var container = Railend.GetContainer();
            if (container == null) { Warn("Railend container unavailable at path selection."); return; }
            var cardRegistry = container.GetInstance<IRegister<CardData>>();
            var upgradeRegistry = container.GetInstance<IRegister<CardUpgradeData>>();
            if (cardRegistry == null || upgradeRegistry == null)
            {
                Warn("Card or upgrade register unavailable at path selection.");
                return;
            }

            foreach (Reward reward in Rewards)
            {
                CardUpgradeData registeredUpgrade = FindUpgrade(upgradeRegistry, reward.UpgradeKey);
                if (registeredUpgrade == null)
                {
                    Warn("Missing registered path upgrade: " + reward.UpgradeKey);
                    continue;
                }

                // IDs on CardUpgradeState are GUIDs. Compare against the GUID
                // on the REGISTERED CardUpgradeData, not its JSON 'id' string.
                if (!SameRuntimeId(appliedId, registeredUpgrade.GetID(), reward.UpgradeKey))
                    continue;

                CardData registeredChampion = FindCard(cardRegistry, reward.ChampionKey);
                if (registeredChampion == null ||
                    !SameRuntimeId(championId, registeredChampion.GetID(), reward.ChampionKey))
                {
                    Warn("Matched path " + reward.UpgradeKey + " but champion card ID did not match " +
                         reward.ChampionKey + "; cardRuntimeId=" + championId);
                    return;
                }

                // Verify that a real run deck exists. We do NOT require the
                // CardState reference to be in the list at this exact moment:
                // initial champion selection can occur before deck setup settles.
                List<CardState> deck = save.GetDeckState();
                if (deck == null)
                {
                    Warn("Path " + reward.UpgradeKey + " matched, but permanent deck is unavailable.");
                    return;
                }

                HashSet<string> grantedPaths;
                if (!Awarded.TryGetValue(champion, out grantedPaths))
                {
                    grantedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Awarded.Add(champion, grantedPaths);
                }
                if (grantedPaths.Contains(reward.UpgradeKey))
                {
                    Info("Already granted this Level I selection: " + reward.UpgradeKey);
                    return;
                }

                CardData companion = FindCard(cardRegistry, reward.CompanionKey);
                if (companion == null)
                {
                    Warn("Matched path, but companion card missing: " + reward.CompanionKey);
                    return;
                }

                Info("Matched " + reward.UpgradeKey + "; adding permanent Level 5 " +
                     reward.Name + " (deck before=" + deck.Count + ").");
                CardState added;
                if (!TrainersPocketMonCapture.TryAddPermanentCard(save, companion, out added) || added == null)
                {
                    Warn("AddCardToDeck failed for " + reward.CompanionKey);
                    return;
                }

                grantedPaths.Add(reward.UpgradeKey);
                var modifiers = added.GetCardStateModifiers();
                var initialUpgrades = modifiers == null ? null : modifiers.GetCardUpgrades();
                int upgradeCount = initialUpgrades == null ? -1 : initialUpgrades.Count();
                Info("GRANTED " + reward.Name + " via " + reward.UpgradeKey +
                     "; cardRuntimeId=" + added.GetCardDataID() +
                     "; permanentDeckCount=" + save.GetDeckState().Count +
                     "; initialCardUpgrades=" + upgradeCount +
                     ". Check the card displays Level 5.");
                return;
            }

            Info("Champion upgrade was not a Trainers Level I companion path (normal for Levels II/III or other champions).");
        }

        private static bool SameRuntimeId(string actual, string registered, string localKey)
        {
            if (String.Equals(actual, registered, StringComparison.OrdinalIgnoreCase)) return true;
            // Some builds retain the namespaced key rather than a GUID.
            return actual != null &&
                (String.Equals(actual, localKey, StringComparison.OrdinalIgnoreCase) ||
                 actual.EndsWith("-" + localKey, StringComparison.OrdinalIgnoreCase));
        }

        private static CardUpgradeData FindUpgrade(IRegister<CardUpgradeData> registry, string key)
        {
            foreach (string candidate in Keys("Upgrade", key))
            {
                CardUpgradeData result;
                if (registry.TryGetValue(candidate, out result) && result != null) return result;
            }
            return null;
        }

        private static CardData FindCard(IRegister<CardData> registry, string key)
        {
            foreach (string candidate in Keys("Card", key))
            {
                CardData result;
                if (registry.TryGetValue(candidate, out result) && result != null) return result;
            }
            return null;
        }

        private static IEnumerable<string> Keys(string type, string key)
        {
            yield return MyPluginInfo.PLUGIN_GUID + "-" + type + "-" + key;
            yield return MyPluginInfo.PLUGIN_GUID + "-" + type + "Data-" + key;
            yield return MyPluginInfo.PLUGIN_GUID + "-" + key;
            yield return key;
        }

        private static void Info(string message)
        {
            if (global::HexTrolls.Plugin.Plugin.Logger != null)
                global::HexTrolls.Plugin.Plugin.Logger.LogInfo("[TrainersChampionRewards v2] " + message);
        }

        private static void Warn(string message)
        {
            if (global::HexTrolls.Plugin.Plugin.Logger != null)
                global::HexTrolls.Plugin.Plugin.Logger.LogWarning("[TrainersChampionRewards v2] " + message);
        }
    }
}
