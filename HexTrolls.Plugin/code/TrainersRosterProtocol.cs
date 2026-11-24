// Trainer's Roster Protocol v3 — registered relic ownership + silent battle-only exhaust
// Requires your existing TrainersNativeRosterController.cs and Monster Train 2 assemblies.
// All collection edits bypass CardManager's regular Consume / Exhaust actions.
// This file deliberately DOES NOT call SaveManager.RemoveCardFromDeck or Sacrifice.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TrainworksReloaded.Core;
using TrainworksReloaded.Core.Interfaces;
using UnityEngine;

namespace HexTrolls.Plugin.code
{
    // Earliest known combat-start hook (also used by the existing project).
    // The MonoBehaviour below retries when StartCombat has not finished
    // building the combat piles yet.
    [HarmonyPatch(typeof(CombatManager), "StartCombat")]
    internal static class TrainersRosterProtocolCombatStartPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            try { TrainersRosterProtocol.TryRun(); }
            catch (Exception ex)
            {
                HexTrolls.Plugin.Plugin.Logger.LogWarning(
                    "[TrainersRosterProtocol] StartCombat probe: " + ex.Message);
            }
        }
    }

    // Add this behaviour to the *existing* Plugin.Awake() GameObject.
    public sealed class TrainersRosterProtocolBehaviour : MonoBehaviour
    {
        private bool wasInCombat;
        private bool completedThisCombat;
        private float nextAttempt;
        private float firstAttempt;
        private bool gaveUp;

        private void Awake()
        {
            HexTrolls.Plugin.Plugin.Logger.LogInfo(
                "[TrainersRosterProtocol v3] Behaviour attached. Combat scanning enabled.");
        }

        private void Update()
        {
            var game = AllGameManagers.Instance;
            var combat = game == null ? null : game.GetCombatManager();
            bool active = combat != null && combat.GetIsRunningCombat();
            if (!active)
            {
                wasInCombat = false;
                completedThisCombat = false;
                gaveUp = false;
                return;
            }
            if (!wasInCombat)
            {
                wasInCombat = true;
                completedThisCombat = false;
                gaveUp = false;
                nextAttempt = 0f;
                firstAttempt = Time.unscaledTime;
                HexTrolls.Plugin.Plugin.Logger.LogInfo(
                    "[TrainersRosterProtocol v3] Combat detected; checking roster + artifact.");
            }
            if (completedThisCombat || gaveUp || Time.unscaledTime < nextAttempt) return;
            nextAttempt = Time.unscaledTime + 0.20f;
            completedThisCombat = TrainersRosterProtocol.TryRun();
            if (!completedThisCombat && Time.unscaledTime - firstAttempt > 8f)
            {
                gaveUp = true;
                HexTrolls.Plugin.Plugin.Logger.LogWarning(
                    "[TrainersRosterProtocol v3] Gave up this battle after 8 seconds. " +
                    "Search earlier [TrainersRosterProtocol] warnings for the blocking API.");
            }
        }
    }

    internal static class TrainersRosterProtocol
    {
        private const string RelicId = "TrainersRosterProtocol";
        private static readonly string FullRelicId =
            MyPluginInfo.PLUGIN_GUID + "-Relic-" + RelicId;
        private const BindingFlags Flags = BindingFlags.Public |
                                           BindingFlags.NonPublic |
                                           BindingFlags.Instance;
        private static bool warnedMissingPiles;
        private static bool warnedNoRelicLookup;
        private static bool loggedOwnership;
        private static bool warnedRoster;
        private static bool loggedNoRelic;
        private static bool loggedResolvedRelic;
        private static bool loggedFailedOwnershipDetails;
        private static bool warnedNotify;

        private static void Log(string message)
        {
            HexTrolls.Plugin.Plugin.Logger.LogInfo("[TrainersRosterProtocol] " + message);
        }
        private static void Warn(string message)
        {
            HexTrolls.Plugin.Plugin.Logger.LogWarning("[TrainersRosterProtocol] " + message);
        }

        // TRUE means work is complete for this battle; FALSE means retry shortly.
        internal static bool TryRun()
        {
            var game = AllGameManagers.Instance;
            if (game == null) return false;
            var save = game.GetSaveManager();
            var combat = game.GetCombatManager();
            var cardManager = game.GetCardManager();
            if (save == null || save.PreviewMode || cardManager == null || combat == null ||
                !combat.GetIsRunningCombat()) return false;

            // Never run against uninitialized roster markers. That could falsely
            // label all six slotted cards as "unassigned".
            if (!RosterInitialized())
            {
                if (!warnedRoster)
                {
                    warnedRoster = true;
                    Warn("Six-slot marker registry not ready. Is TrainersNativeRosterController initialized?");
                }
                return false;
            }
            bool ownershipWasInspectable;
            bool hasArtifact = OwnsArtifact(save, out ownershipWasInspectable);
            if (!ownershipWasInspectable)
            {
                if (!warnedNoRelicLookup)
                {
                    warnedNoRelicLookup = true;
                    Warn("Cannot verify owned artifacts from SaveManager; no cards will be changed. " +
                         "Inspect your game's SaveManager relic API and adapt OwnsArtifact().");
                }
                return false;
            }
            if (!hasArtifact)
            {
                if (!loggedNoRelic)
                {
                    loggedNoRelic = true;
                    Warn("No owned relic state matched Trainer's Roster Protocol. " +
                         "If Cheat GUI shows the artifact green/owned, send the new 'Resolved relic' " +
                         "and 'Native ownership probe' lines; this indicates an API mismatch.");
                }
                return true; // never mutate a run that doesn't own this artifact
            }
            if (!loggedOwnership)
            {
                loggedOwnership = true;
                Log("Ownership confirmed. Locating live combat piles.");
            }

            // These are the *active battle piles*. NEVER touch save.GetDeckState().
            // API calls GetDrawPile(false) / GetDiscardPile(false) / GetHand(false)
            // are known in the existing TrainersNativeRosterController.
            var draw = LivePile(cardManager, "GetDrawPile");
            var discard = LivePile(cardManager, "GetDiscardPile");
            var hand = LivePile(cardManager, "GetHand");
            var exhausted = FindExhaustedPile(cardManager);

            if (draw == null || discard == null || hand == null || exhausted == null ||
                ReferenceEquals(draw, exhausted) ||
                ReferenceEquals(discard, exhausted) ||
                ReferenceEquals(hand, exhausted))
            {
                if (!warnedMissingPiles)
                {
                    warnedMissingPiles = true;
                    Warn("No mutable battle pile available (especially exhausted pile). " +
                         "No cards changed. CardManager pile methods: " + DescribePiles(cardManager));
                }
                return false;
            }
            if (draw.Count + discard.Count + hand.Count == 0)
                return false; // battle deck not populated yet

            // Collect exact CardState objects, not species IDs. Do NOT use
            // the permanent run-deck list as a mutation source.
            var todo = new List<Tuple<string, IList, CardState>>();
            foreach (var pile in new[] {
                Tuple.Create("GetDrawPile", draw),
                Tuple.Create("GetDiscardPile", discard),
                Tuple.Create("GetHand", hand) })
                foreach (object value in pile.Item2)
                {
                    var card = value as CardState;
                    if (card == null || card.GetSpawnCharacterData() == null) continue;
                    if (TrainersNativeRosterController.IsRosterAssigned(card)) continue;
                    todo.Add(Tuple.Create(pile.Item1, pile.Item2, card));
                }

            if (todo.Count > 0)
                Log("Found " + todo.Count + " unassigned unit card(s) across battle piles.");
            int moved = 0;
            foreach (var entry in todo)
            {
                string fromGetter = entry.Item1;
                IList origin = entry.Item2;
                CardState card = entry.Item3;
                if (!origin.Contains(card)) continue;
                int oldIndex = origin.IndexOf(card);
                try
                {
                    // Two direct list edits, no ExhaustCard / ConsumeCard call.
                    // Thus no OnCardExhausted, Etch, or relic Consume dispatch.
                    origin.RemoveAt(oldIndex);
                    // Prove we removed from a LIVE list, not a detached copy.
                    // If the public getter returns copies, fail closed.
                    var freshSource = LivePile(cardManager, fromGetter);
                    if (freshSource == null || freshSource.Contains(card))
                    {
                        origin.Insert(oldIndex, card);
                        Warn(fromGetter + " getter did not expose a live mutable pile; " +
                             "refusing to create duplicates in Exhausted pile.");
                        return false;
                    }
                    try
                    {
                        exhausted.Add(card);
                        var freshExhausted = FindExhaustedPile(cardManager);
                        if (freshExhausted == null || !freshExhausted.Contains(card))
                            throw new InvalidOperationException(
                                "Exhausted-pile getter returned a detached copy");
                        moved++;
                    }
                    catch
                    {
                        exhausted.Remove(card);
                        origin.Insert(oldIndex, card); // atomic rollback
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    Warn("Could not silently exhaust " + card.GetCardDataID() +
                         ": " + ex.GetType().Name + " " + ex.Message);
                    return false; // retry; already moved cards stay exhausted
                }
            }
            if (moved > 0)
                RefreshCardPileViews(cardManager);
            Log("Silently exhausted " + moved +
                " unassigned unit card(s); roster copies/spells untouched.");
            return true;
        }

        private static bool RosterInitialized()
        {
            var field = typeof(TrainersNativeRosterController).GetField("ready",
                BindingFlags.Static | BindingFlags.NonPublic);
            return field != null && field.GetValue(null) is bool &&
                   (bool)field.GetValue(null);
        }

        // The game exposes this signal for direct list modifications (also used
        // by other MT2 mods). It updates pile/hand views; it is not the normal
        // Consume/Exhaust command or Consume trigger dispatcher.
        private static void RefreshCardPileViews(CardManager cards)
        {
            try
            {
                var method = cards.GetType().GetMethod("DispatchCardPilesChangedSignal",
                    Flags, null, Type.EmptyTypes, null);
                if (method != null) method.Invoke(cards, null);
                else if (!warnedNotify)
                {
                    warnedNotify = true;
                    Warn("No DispatchCardPilesChangedSignal() found; the UI may not refresh until the next draw.");
                }
            }
            catch (Exception ex)
            {
                Warn("Pile-view refresh failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Getters may return a direct List<CardState>, or a pile object
        // wrapping a List<CardState>. Refuse read-only/copy-only containers.
        private static IList FindExhaustedPile(object manager)
        {
            return LivePile(manager, "GetExhaustedPile") ??
                   LivePile(manager, "GetExhaustPile") ??
                   LivePile(manager, "GetConsumePile") ??
                   LivePile(manager, "GetConsumedPile") ??
                   FieldPile(manager, "exhaust") ?? FieldPile(manager, "consum");
        }
        private static IList FieldPile(object manager, string token)
        {
            foreach (var f in manager.GetType().GetFields(Flags))
            {
                if (f.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    var pile = ExtractMutableCardList(f.GetValue(manager));
                    if (pile != null) return pile;
                }
                catch { }
            }
            return null;
        }
        private static IList LivePile(object manager, string getter)
        {
            var type = manager.GetType();
            MethodInfo method = type.GetMethod(getter, Flags, null,
                new[] { typeof(bool) }, null) ??
                type.GetMethod(getter, Flags, null, Type.EmptyTypes, null);
            if (method == null) return null;
            object result;
            try
            {
                result = method.Invoke(manager,
                    method.GetParameters().Length == 1 ? new object[] { false } : null);
            }
            catch { return null; }
            return ExtractMutableCardList(result);
        }

        private static IList ExtractMutableCardList(object obj)
        {
            if (obj == null) return null;
            var list = obj as IList;
            if (list != null && !list.IsFixedSize && !list.IsReadOnly &&
                IsCardList(list)) return list;
            var type = obj.GetType();
            foreach (var f in type.GetFields(Flags))
            {
                if (!f.Name.ToLowerInvariant().Contains("card")) continue;
                try
                {
                    var found = f.GetValue(obj) as IList;
                    if (found != null && !found.IsFixedSize && !found.IsReadOnly &&
                        IsCardList(found)) return found;
                }
                catch { }
            }
            return null;
        }

        private static bool IsCardList(IList list)
        {
            if (list.Count > 0) return list[0] is CardState;
            Type t = list.GetType();
            return t.GetInterfaces().Any(x => x.IsGenericType &&
                x.GetGenericTypeDefinition() == typeof(IList<>) &&
                x.GetGenericArguments()[0] == typeof(CardState));
        }

        // Resolve the REAL registered RelicData, never compare guessed GUIDs.
        // Trainworks translates readable IDs to runtime IDs. The v2 comparison
        // of RelicData.GetID() to a readable string could produce false negatives.
        private static RelicData ResolveOurRegisteredRelic(SaveManager save)
        {
            string[] keys = {
                FullRelicId,
                MyPluginInfo.PLUGIN_GUID + "-TrainersRosterProtocol",
                RelicId
            };
            try
            {
                var container = Railend.GetContainer();
                if (container != null)
                {
                    var register = container.GetInstance<IRegister<RelicData>>();
                    if (register != null)
                        foreach (string key in keys)
                        {
                            RelicData found;
                            if (register.TryGetValue(key, out found) && found != null)
                            {
                                LogResolved("Trainworks registry key=" + key, found);
                                return found;
                            }
                        }
                }
            }
            catch (Exception ex)
            {
                Warn("Trainworks relic registry lookup failed: " + ex.Message);
            }

            // Some builds expose the direct lookup on AllGameData rather
            // than the generic register. Use exact registered data if found.
            var all = save.GetAllGameData();
            if (all == null) return null;
            foreach (var method in all.GetType().GetMethods(Flags))
            {
                if (!method.Name.StartsWith("Find", StringComparison.OrdinalIgnoreCase) ||
                    method.Name.IndexOf("Relic", StringComparison.OrdinalIgnoreCase) < 0 ||
                    !typeof(RelicData).IsAssignableFrom(method.ReturnType)) continue;
                var args = method.GetParameters();
                if (args.Length != 1 || args[0].ParameterType != typeof(string)) continue;
                foreach (var key in keys)
                {
                    try
                    {
                        var found = method.Invoke(all, new object[] { key }) as RelicData;
                        if (found != null)
                        {
                            LogResolved(method.Name + "(" + key + ")", found);
                            return found;
                        }
                    }
                    catch { }
                }
            }
            // Last-resort scan of registered data, NOT of the run inventory.
            // This scan checks both readable names and ID variants.
            foreach (var member in all.GetType().GetMethods(Flags))
            {
                if (member.GetParameters().Length != 0 ||
                    (!member.Name.Contains("Relic") && !member.Name.Contains("Collectable")) ||
                    !typeof(IEnumerable).IsAssignableFrom(member.ReturnType)) continue;
                try
                {
                    var found = FindRelicIn(member.Invoke(all, null) as IEnumerable);
                    if (found != null)
                    {
                        LogResolved("AllGameData." + member.Name, found);
                        return found;
                    }
                }
                catch { }
            }
            foreach (var f in all.GetType().GetFields(Flags))
            {
                if (f.Name.IndexOf("relic", StringComparison.OrdinalIgnoreCase) < 0 &&
                    f.Name.IndexOf("collectable", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                try
                {
                    var found = FindRelicIn(f.GetValue(all) as IEnumerable);
                    if (found != null)
                    {
                        LogResolved("AllGameData." + f.Name, found);
                        return found;
                    }
                }
                catch { }
            }
            return null;
        }

        private static void LogResolved(string lookup, RelicData data)
        {
            if (loggedResolvedRelic) return;
            loggedResolvedRelic = true;
            Log("Resolved relic via " + lookup + "; runtime GetID=" +
                data.GetID() + "; display=" + GetDisplayName(data));
        }

        private static string GetDisplayName(object data)
        {
            if (data == null) return "<null>";
            foreach (var methodName in new[] { "GetName", "Cheat_GetNameEnglish", "GetNameEnglish" })
            {
                var method = data.GetType().GetMethod(methodName, Flags,
                    null, Type.EmptyTypes, null);
                if (method == null) continue;
                try
                {
                    var result = method.Invoke(data, null) as string;
                    if (!string.IsNullOrEmpty(result)) return result;
                }
                catch { }
            }
            return "<unavailable>";
        }

        private static bool OwnsArtifact(SaveManager save, out bool inspected)
        {
            inspected = false;
            var relic = ResolveOurRegisteredRelic(save);
            string runtimeId = relic == null ? null : relic.GetID();
            if (relic != null)
            {
                // Try exact RelicData/CollectableRelicData overload, and string
                // ID overloads if they exist. Only positive ownership is trusted.
                foreach (var method in save.GetType().GetMethods(Flags))
                {
                    if (method.Name != "GetHasRelic" && method.Name != "HasRelic" &&
                        method.Name != "GetHasArtifact") continue;
                    if (method.ReturnType != typeof(bool)) continue;
                    var ps = method.GetParameters();
                    if (ps.Length != 1) continue;
                    var args = new List<object>();
                    if (ps[0].ParameterType.IsInstanceOfType(relic)) args.Add(relic);
                    else if (ps[0].ParameterType == typeof(string))
                    {
                        foreach (var key in new[] { runtimeId, FullRelicId, RelicId })
                            if (!string.IsNullOrEmpty(key)) args.Add(key);
                    }
                    foreach (var arg in args)
                    {
                        try
                        {
                            inspected = true;
                            bool answer = (bool)method.Invoke(save, new[] { arg });
                            if (!loggedFailedOwnershipDetails)
                            {
                                loggedFailedOwnershipDetails = true;
                                Log("Native ownership probe " + method.Name + "(" +
                                    ps[0].ParameterType.Name + ") with " +
                                    (arg is RelicData ? "registered RelicData" : arg.ToString()) +
                                    " => " + answer);
                            }
                            if (answer) return true;
                        }
                        catch (Exception ex)
                        {
                            Warn(method.Name + " ownership check error: " + ex.Message);
                        }
                    }
                }
            }
            else if (!loggedResolvedRelic)
            {
                loggedResolvedRelic = true;
                Warn("Could not resolve Trainer's Roster Protocol in the Trainworks relic registry. " +
                     "Expected key=" + FullRelicId);
            }

            // The cheat GUI adds relics using CheatManager.Command_AddArtifact.
            // The presence of a relic in AllGameData is NOT proof it is owned.
            // Look for a matching *RelicState* in the active run inventory.
            foreach (string name in new[]
            {
                "GetRelicStates", "GetRelics", "GetCollectedRelics", "GetOwnedRelics",
                "GetArtifacts", "GetRelicList", "GetCollectableRelics",
                "GetAllRelicStates", "GetCollectedRelicStates",
                "GetActiveRelics", "GetActiveRelicStates"
            })
            {
                var getter = save.GetType().GetMethod(name, Flags,
                    null, Type.EmptyTypes, null);
                if (getter == null) continue;
                try
                {
                    var records = getter.Invoke(save, null) as IEnumerable;
                    if (records == null || records is string) continue;
                    inspected = true;
                    foreach (object state in records)
                        if (IsThisRelic(state, relic))
                        {
                            Log("Owned relic matched in SaveManager." + name);
                            return true;
                        }
                }
                catch (Exception ex) { Warn("Owned relic read " + name + ": " + ex.Message); }
            }
            foreach (var f in save.GetType().GetFields(Flags))
            {
                var name = f.Name.ToLowerInvariant();
                if (!name.Contains("relic") && !name.Contains("artifact")) continue;
                if (name.Contains("registry") || name.Contains("catalog")) continue;
                try
                {
                    var records = f.GetValue(save) as IEnumerable;
                    if (records == null || records is string) continue;
                    inspected = true;
                    foreach (object state in records)
                        if (IsThisRelic(state, relic))
                        {
                            Log("Owned relic matched in SaveManager field " + f.Name);
                            return true;
                        }
                }
                catch { }
            }
            if (!inspected)
                Warn("Ownership unavailable: no working owned-relic state API found. " +
                     "SaveManager methods containing 'Relic': " +
                     String.Join(", ", save.GetType().GetMethods(Flags)
                         .Where(m => m.Name.IndexOf("Relic", StringComparison.OrdinalIgnoreCase) >= 0)
                         .Select(m => m.Name + "(" + String.Join(",",
                            m.GetParameters().Select(p => p.ParameterType.Name).ToArray()) + ")")
                         .Distinct().Take(40).ToArray()));
            return false;
        }

        private static RelicData FindRelicIn(IEnumerable records)
        {
            if (records == null) return null;
            foreach (object record in records)
            {
                var relic = record as RelicData;
                if (relic == null) continue;
                if (MatchesRelicId(relic.GetID()) ||
                    String.Equals(GetDisplayName(relic), "Trainer's Roster Protocol",
                        StringComparison.OrdinalIgnoreCase)) return relic;
            }
            return null;
        }
        private static bool IsThisRelic(object state, RelicData resolved)
        {
            if (state == null) return false;
            if (state is RelicData r) return SameRelic(r, resolved);
            if (SameId(ReadId(state), resolved)) return true;
            foreach (var getter in new[] { "GetRelicData", "GetData", "GetRelic" })
            {
                var m = state.GetType().GetMethod(getter, Flags,
                    null, Type.EmptyTypes, null);
                if (m == null) continue;
                try
                {
                    var data = m.Invoke(state, null);
                    if (SameRelic(data as RelicData, resolved) ||
                        SameId(ReadId(data), resolved)) return true;
                }
                catch { }
            }
            return false;
        }
        private static bool SameRelic(RelicData value, RelicData resolved)
        {
            if (value == null) return false;
            return (resolved != null && (object.ReferenceEquals(value, resolved) ||
                    String.Equals(value.GetID(), resolved.GetID(),
                        StringComparison.OrdinalIgnoreCase))) ||
                   MatchesRelicId(value.GetID()) ||
                   String.Equals(GetDisplayName(value), "Trainer's Roster Protocol",
                       StringComparison.OrdinalIgnoreCase);
        }
        private static bool SameId(string id, RelicData resolved)
        {
            return MatchesRelicId(id) ||
                   (!String.IsNullOrEmpty(id) && resolved != null &&
                     String.Equals(id, resolved.GetID(), StringComparison.OrdinalIgnoreCase));
        }
        private static string ReadId(object record)
        {
            if (record == null) return null;
            if (record is string s) return s;
            foreach (string name in new[] { "GetRelicDataId", "GetRelicDataID", "GetID" })
            {
                var method = record.GetType().GetMethod(name, Flags,
                    null, Type.EmptyTypes, null);
                if (method == null) continue;
                try { return method.Invoke(record, null)?.ToString(); }
                catch { }
            }
            return null;
        }
        private static bool MatchesRelicId(string id) =>
            !string.IsNullOrEmpty(id) &&
            (id.Equals(FullRelicId, StringComparison.OrdinalIgnoreCase) ||
             id.Equals(RelicId, StringComparison.OrdinalIgnoreCase));

        private static string DescribePiles(object obj)
        {
            return string.Join(", ", obj.GetType().GetMethods(Flags)
                .Where(m => m.Name.IndexOf("Pile", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(m => m.Name + "(" +
                    string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")")
                .Distinct().Take(40).ToArray());
        }
    }
}
