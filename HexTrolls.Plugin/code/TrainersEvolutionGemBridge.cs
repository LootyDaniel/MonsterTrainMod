// Trainers Evolution Gem v7 - experimental in-battle replacement.
// Uses MonsterManager.CreateMonsterState with destination CharacterData and
// new permanent CardState as its explicit source (NOT CloneMonsterState, which
// generated another Form 1 in the v5 test). Abort/cleanup on bad postconditions.
// WARNING: not verified in-game; test only on disposable fresh runs.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TrainworksReloaded.Base;
using TrainworksReloaded.Core;
using TrainworksReloaded.Core.Interfaces;

namespace HexTrolls.Plugin.code
{
    public static class TrainersEvolutionGemBridge
    {
        // Set true to disable mutations and emit safe diagnostic messages.
        private static bool SafeProbeOnly = false;
        const string Prefix = "HexTrolls.Plugin-Card-";
        const string CharacterPrefix = "HexTrolls.Plugin-Character-";
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly HashSet<string> Warnings = new HashSet<string>();
        private static bool busy;

        private static void Info(string s) => Plugin.Logger.LogInfo("[EvolutionGem v7 CREATE] " + s);
        private static void Warn(string s) => Plugin.Logger.LogWarning("[EvolutionGem v7 CREATE] " + s);

        private static void WarnOnce(string s)
        {
            if (Warnings.Add(s)) Warn(s);
        }

        private sealed class Plan
        {
            public CharacterState OldUnit;
            public CardState OldDeckCard;
            public CardData NewCardData;
            public CharacterData NewCharacterData;
            public SaveManager Save;
            public RoomState Room;
            public int RoomIndex;
            public int UnitIndex;
            public MethodInfo CreateMethod;
            public object MonsterManager;
            public CardEffectBase SilentDespawn;
            public int Level;
            public int Exp;
            public TrainersElement NewType;
            public string OldName;
            public string NewName;
        }

        // Plugged directly into the Gem's CardEffect. No Plugin.cs replacement.
        public static IEnumerator Apply(CharacterState unit, string rawGem, CardEffectState effect,
            CardEffectParams parameters, ICoreGameManagers core, ISystemManagers systems)
        {
            if (busy)
            {
                Warn("A second evolution was requested while one was being processed; cancelled.");
                yield break;
            }

            Plan plan;
            if (!TryPlan(unit, rawGem, core, out plan)) yield break;
            // Optional read-only diagnostics. Set SafeProbeOnly=true to disable mutations.
            if (SafeProbeOnly)
            {
                Info("SAFE PROBE: source=" + plan.OldName +
                     ", destination=" + plan.NewName +
                     ", room=" + plan.RoomIndex +
                     ", level=" + plan.Level +
                     ", exp=" + plan.Exp +
                     ". No cards or units changed.");
                ProbeNativeMethods(plan, core);
                yield break;
            }
            busy = true;
            try
            {
            CardState addedCard = null;
            CharacterState spawned = null;
            bool oldRemoved = false;
            bool committed = false;
            var deferredRoster = new List<CardUpgradeState>();

            try
            {
                // Prepare the exact permanent replacement BEFORE touching the original unit.
                var modifiers = new CardStateModifiers();
                var oldModifiers = plan.OldDeckCard.GetCardStateModifiers();
                var upgrades = oldModifiers?.GetCardUpgrades();
                if (upgrades == null)
                {
                    Warn("The source deck card's upgrade collection is unavailable; cancelled.");
                }
                else
                {
                    // The destination's roster marker must not appear in the
                    // permanent deck until the old owner is removed. Otherwise the
                    // HUD can see two owners for the same slot during spawning.
                    foreach (CardUpgradeState upgrade in upgrades)
                    {
                        if (upgrade == null) continue;
                        string upgradeId = upgrade.GetCardUpgradeDataId();
                        if (!String.IsNullOrEmpty(upgradeId) &&
                            upgradeId.StartsWith("HexTrolls.Plugin-Upgrade-TrainersRosterSlot",
                                StringComparison.OrdinalIgnoreCase))
                            deferredRoster.Add(upgrade);
                        else modifiers.AddUpgrade(upgrade);
                    }

                    try
                    {
                        addedCard = plan.Save.AddCardToDeck(plan.NewCardData, modifiers,
                            false, 0, false, false, true, false, true);
                    }
                    catch (Exception ex)
                    {
                        Warn("AddCardToDeck failed; original unit/card untouched: " + ex);
                    }
                    if (addedCard == null || !plan.Save.GetDeckState().Contains(addedCard))
                    {
                        Warn("Replacement card could not be verified in permanent deck; original retained.");
                        addedCard = null;
                    }
                    else
                    {
                        Info("Prepared " + plan.OldName + " -> " + plan.NewName +
                             " with " + upgrades.Count + " persistent upgrades, level=" + plan.Level +
                             ", exp=" + plan.Exp + ". Starting native evolved-character creation.");
                    }
                }
            }
            catch (Exception ex)
            {
                Warn("Preparation error; original intact: " + ex);
            }

            if (addedCard != null)
            {
                // Native CreateMonsterState takes destination CharacterData as its
                // FIRST parameter and the replacement CardState as its SECOND.
                // This avoids CreateMonsterState always duplicating the old form.
                IEnumerator nativeCreate = null;
                try
                {
                    var args = new object[] {
                        plan.NewCharacterData, addedCard, plan.RoomIndex,
                        new Action<CharacterState>(candidate => spawned = candidate),
                        Enum.ToObject(plan.CreateMethod.GetParameters()[4].ParameterType, 3),
                        plan.OldUnit.GetSpawnPoint(false),
                        null, false, null, null, null, true, null, false, addedCard
                    };
                    nativeCreate = plan.CreateMethod.Invoke(plan.MonsterManager, args) as IEnumerator;
                }
                catch (Exception ex)
                {
                    Warn("Native create invocation failed: " + ex);
                }

                if (nativeCreate != null)
                {
                    yield return nativeCreate;

                    Info("CreateMonsterState completed: candidate=" + (spawned == null ? "<null>" :
                        spawned.GetSourceCharacterData()?.name) +
                        ", originalStillOnFloor=" + OnFloor(plan.Room, plan.OldUnit) +
                        ", replacementOnFloor=" + (spawned != null && OnFloor(plan.Room, spawned)));
                    if (IsCorrectCreated(plan, spawned, addedCard))
                    {
                        // The replacement exists with the correct CharacterData and CardState.
                        // Only NOW remove the original, using the same native silent despawn
                        // effect already tested by Capture Capsule (not Kill or Sacrifice).
                        IEnumerator silent = MakeDespawnCoroutine(plan.SilentDespawn, effect,
                            parameters, plan.OldUnit, core, systems);
                        if (silent != null)
                        {
                            yield return silent;
                            oldRemoved = !OnFloor(plan.Room, plan.OldUnit);
                            if (!oldRemoved)
                            {
                                // Native cleanup is asynchronous on some MT2 builds.
                                yield return null;
                                oldRemoved = !OnFloor(plan.Room, plan.OldUnit);
                            }
                            if (!oldRemoved)
                                Warn("Original still on floor after despawn; attempting to roll back new unit.");
                        }
                        else Warn("Unable to start silent despawn; original unit was not touched.");
                    }
                    else Warn("Created unit did not match destination CharacterData + replacement spawner card; rolling back.");
                }
                else Warn("Native create returned no coroutine; evolution cancelled.");
            }

            if (oldRemoved && spawned != null)
            {
                try
                {
                    // Move the evolved unit into the vacated index without killing anything.
                    var point = spawned.GetSpawnPoint(false);
                    if (point != null)
                    {
                        int newIndex = point.GetIndexInRoom();
                        if (newIndex != plan.UnitIndex)
                            plan.Room.RearrangeCharacter(Team.Type.Monsters, newIndex, plan.UnitIndex);
                    }

                    // Level / EXP upgrades were copied at card creation. The roster
                    // marker is deferred until the original deck owner is removed.
                    plan.Save.RemoveCardFromDeck(plan.OldDeckCard);
                    var deck = plan.Save.GetDeckState();
                    // Restore the same upgrade objects (including exact roster
                    // slot) only AFTER old card has left permanent deck.
                    foreach (var roster in deferredRoster)
                        addedCard.GetCardStateModifiers().AddUpgrade(roster);
                    committed = deck.Contains(addedCard) && !deck.Contains(plan.OldDeckCard) &&
                        deferredRoster.All(marker => addedCard.GetCardStateModifiers()
                            .GetCardUpgrades().Contains(marker));
                    if (committed)
                    {
                        addedCard.UpdateCardBodyText();
                        Info("EVOLVED " + plan.OldName + " -> " + plan.NewName +
                             " on floor " + plan.RoomIndex + "; exact saved deck copy replaced.");
                    }
                    else Warn("DECK COMMIT NOT VERIFIED. Inspect game state; do not save this run until resolved.");
                }
                catch (Exception ex)
                {
                    Warn("DECK COMMIT FAILED AFTER UNIT CHANGED: " + ex +
                         ". Keep this log; do not save this run until resolved.");
                }
            }

            // If the original is intact, clean up the speculative replacement and deck copy.
            if (!oldRemoved && !committed)
            {
                if (spawned != null && OnFloor(plan.Room, spawned))
                {
                    IEnumerator cleanup = MakeDespawnCoroutine(plan.SilentDespawn,
                        effect, parameters, spawned, core, systems);
                    if (cleanup != null)
                    {
                        yield return cleanup;
                        if (OnFloor(plan.Room, spawned)) yield return null;
                    }
                    else Warn("Could not silently clean speculative replacement; check floor for duplicate.");
                }
                bool replacementGone = spawned == null || !OnFloor(plan.Room, spawned);
                if (replacementGone && addedCard != null)
                {
                    try
                    {
                        if (plan.Save.GetDeckState().Contains(addedCard))
                            plan.Save.RemoveCardFromDeck(addedCard);
                    }
                    catch (Exception ex)
                    {
                        Warn("Cleanup of uncommitted replacement deck card failed: " + ex);
                    }
                }
                if (replacementGone)
                    Info("Evolution aborted; original unit and saved card preserved.");
                else
                    Warn("CRITICAL: tentative replacement remains on floor; saved card left present as well. " +
                         "Do not save this run; send this log.");
            }
            else if (oldRemoved && !committed)
            {
                Warn("CRITICAL: original unit was removed but saved-deck replacement was not confirmed. " +
                     "Do not continue this run; send this log before saving.");
            }
            }
            finally { busy = false; }
        }

        private static void ProbeNativeMethods(Plan plan, ICoreGameManagers core)
        {
            try
            {
                var deck = plan.Save.GetDeckState();
                string slotPrefix = "HexTrolls.Plugin-Upgrade-TrainersRosterSlot";
                string[] slotIds = new string[6];
                for (int i = 0; i < 6; ++i) slotIds[i] = slotPrefix + (i + 1).ToString("D2");
                if (deck != null)
                {
                    for (int i = 0; i < slotIds.Length; i++)
                    {
                        int n = deck.Count(c => c != null && c.GetCardStateModifiers()?.GetCardUpgrades()?.Any(
                            u => u != null && string.Equals(u.GetCardUpgradeDataId(), slotIds[i],
                                StringComparison.OrdinalIgnoreCase)) == true);
                        if (n > 0) Info("Roster Slot " + (i + 1) + " permanent owners=" + n +
                            (n > 1 ? " (DUPLICATE - use fresh run)" : ""));
                    }
                }
            }
            catch (Exception ex) { Warn("Roster inspection error: " + ex.Message); }
            DumpMethods("MonsterManager", plan.MonsterManager?.GetType(), 55);
            try { DumpMethods("RoomManager", core?.GetRoomManager()?.GetType(), 32); }
            catch (Exception ex) { Warn("RoomManager probe error: " + ex.Message); }
            DumpMethods("CharacterState", typeof(CharacterState), 40);
            try
            {
                var spawnType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("CardEffectSpawnMonster", false))
                    .FirstOrDefault(t => t != null);
                if (spawnType != null)
                    DumpMethods("CardEffectSpawnMonster", spawnType, 20);
                else Info("CardEffectSpawnMonster type not resolved in loaded assemblies.");
            }
            catch (Exception ex) { Warn("Spawn effect probe error: " + ex.Message); }
            Info("SAFE PROBE COMPLETE. The Gem has not changed a unit, deck card, or roster slot.");
        }

        private static void DumpMethods(string tag, Type type, int limit)
        {
            if (type == null) { Warn(tag + " type is null"); return; }
            try
            {
                var methods = type.GetMethods(Flags)
                    .Where(m => m.Name.IndexOf("Spawn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.Name.IndexOf("Create", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.Name.IndexOf("Clone", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.Name.IndexOf("Change", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.Name.IndexOf("SetSource", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.Name.IndexOf("Transform", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.Name.IndexOf("Despawn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.Name.IndexOf("RemoveMonster", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(m => m.ToString()).Distinct().OrderBy(s => s).Take(limit).ToArray();
                Info("PROBE " + tag + ": " + methods.Length + " method signature(s)");
                foreach (string method in methods) Info("PROBE " + tag + "." + method);
            }
            catch (Exception ex) { Warn("Could not probe " + tag + ": " + ex.Message); }
        }

        private static bool TryPlan(CharacterState unit, string rawGem, ICoreGameManagers core, out Plan plan)
        {
            plan = null;
            if (unit == null || core == null || !unit.IsAlive || unit.IsDestroyed ||
                unit.GetTeamType() != Team.Type.Monsters) return false;
            var save = core.GetSaveManager();
            if (save == null || save.PreviewMode) return false;
            if (!Enum.TryParse(rawGem, true, out TrainersGem gem))
            {
                Warn("Unknown Gem type: " + rawGem); return false;
            }

            var spawner = unit.GetSpawnerCard();
            var deck = save.GetDeckState();
            if (spawner == null || deck == null)
            {
                Warn("Deployed unit lacks spawner or permanent deck; refusing ambiguous evolution.");
                return false;
            }
            // IMPORTANT: CardState.GetCardDataID() is a RUNTIME DATA GUID, not
            // the Trainworks name such as TrainersPocketMonManshrimpForm1Card.
            // Also CardState.GetID() can be shared by multiple copies of the
            // same species; it does NOT establish which deck copy was deployed.
            // Use reference identity first, otherwise only a unique GUID-based
            // card-data match. Never silently choose a duplicate.
            var byReference = deck.Where(c => c != null &&
                object.ReferenceEquals(c, spawner)).ToList();
            CardState old;
            if (byReference.Count == 1)
            {
                old = byReference[0];
            }
            else
            {
                var matchingData = deck.Where(c => c != null &&
                    String.Equals(c.GetCardDataID(), spawner.GetCardDataID(),
                                  StringComparison.Ordinal)).ToList();
                if (matchingData.Count != 1)
                {
                    Warn("Cannot identify the exact permanent deck copy. " +
                         "ReferenceMatches=" + byReference.Count +
                         ", sameCardData=" + matchingData.Count +
                         ", runtimeCardDataId=" + spawner.GetCardDataID() +
                         ". Multiple identical copies require explicit copy identity.");
                    return false;
                }
                old = matchingData[0];
                Info("Unique saved CardData GUID matched; exact instance reference was " +
                     "not shared with combat spawner.");
            }
            if (!String.Equals(old.GetCardDataID(), spawner.GetCardDataID(),
                               StringComparison.Ordinal))
            {
                Warn("Live spawner and saved copy have different CardData IDs."); return false;
            }
            CardData sourceCardData;
            string oldName = ResolveRegisteredSourceName(core.GetAllGameData(),
                old.GetCardDataID(), out sourceCardData);
            if (oldName == null || !oldName.StartsWith("TrainersPocketMon",
                StringComparison.OrdinalIgnoreCase))
            {
                Warn("The runtime source card could not be resolved as a registered " +
                     "Trainers PocketMon. cardDataGuid=" + old.GetCardDataID() +
                     ", registeredAssetName=" + (sourceCardData == null ? "<not found>" : sourceCardData.name) +
                     ". Original unchanged.");
                return false;
            }
            Info("Resolved runtime cardDataGuid=" + old.GetCardDataID() +
                 " to registered source " + oldName + ".");

            var typeMatches = Enum.GetValues(typeof(TrainersElement)).Cast<TrainersElement>()
                .Where(t => unit.GetStatusEffectStacks(TrainersElementalRules.StatusId(t)) > 0).ToList();
            if (typeMatches.Count != 1)
            {
                Warn("Target must have exactly ONE Trainer Type; found " + typeMatches.Count +
                     " for " + oldName + ". No type guessing.");
                return false;
            }
            var moon = LunarmancerMoonCycle.IsFullMoon
                ? TrainersMoonPhase.FullMoon : TrainersMoonPhase.NewMoon;
            string destinationName = TrainersEvolutionMechanics.ChooseDestination(oldName,
                typeMatches[0], gem, moon);
            if (String.IsNullOrWhiteSpace(destinationName))
            {
                Warn("No eligible " + gem + " evolution for " + oldName +
                     " (type=" + typeMatches[0] + ", moon=" + moon + ").");
                return false;
            }
            CardData newCardData = core.GetAllGameData()?.FindCardDataByName(Prefix + destinationName);
            if (newCardData == null)
            {
                Warn("Destination CardData was not registered: " + destinationName); return false;
            }
            string newCharacterName = destinationName.Replace("Card", "Character");
            CharacterData newCharacterData = null;
            try
            {
                var registry = Railend.GetContainer().GetInstance<IRegister<CharacterData>>();
                registry.TryGetValue(CharacterPrefix + newCharacterName, out newCharacterData);
            }
            catch (Exception ex) { Warn("Character registry lookup failed: " + ex.Message); }
            if (newCharacterData == null)
            {
                Warn("Destination CharacterData not registered: " + newCharacterName); return false;
            }

            var room = unit.GetCurrentRoom();
            var spawn = unit.GetSpawnPoint(false);
            if (room == null || spawn == null || !OnFloor(room, unit))
            {
                Warn("Target is not present at a valid floor spawn point."); return false;
            }
            var manager = core.GetMonsterManager();
            var create = FindNativeCreate(manager, spawn);
            if (create == null)
            {
                Warn("No supported 15-argument CreateMonsterState signature. " +
                     "Build-specific API data is required before attempting a swap.");
                return false;
            }
            // A roster slot can have exactly one permanent owner. Never attempt a
            // transformation on already-corrupted decks with multiple owners.
            var sourceUpgrades = old.GetCardStateModifiers()?.GetCardUpgrades();
            var rosterMarkerIds = (sourceUpgrades == null ? Enumerable.Empty<string>() :
                sourceUpgrades.Where(u => u != null)
                    .Select(u => u.GetCardUpgradeDataId())
                    .Where(id => !String.IsNullOrEmpty(id) &&
                        id.StartsWith("HexTrolls.Plugin-Upgrade-TrainersRosterSlot",
                            StringComparison.OrdinalIgnoreCase))).Distinct().ToList();
            if (rosterMarkerIds.Count > 1)
            {
                Warn("Source card owns multiple roster slots. Do not evolve this deck.");
                return false;
            }
            foreach (string rosterId in rosterMarkerIds)
            {
                int owners = deck.Count(card => card != null &&
                    card.GetCardStateModifiers()?.GetCardUpgrades()?.Any(u => u != null &&
                        String.Equals(u.GetCardUpgradeDataId(), rosterId,
                                      StringComparison.OrdinalIgnoreCase)) == true);
                if (owners != 1)
                {
                    Warn("Roster marker " + rosterId + " has " + owners +
                         " saved owners; refusing evolution to avoid duplicate assignments.");
                    return false;
                }
            }
            var despawn = TrainersPocketMonCapture.MakeSilentDespawn();
            if (despawn == null)
            {
                Warn("Native silent despawn unavailable; evolution refused."); return false;
            }
            plan = new Plan {
                OldUnit=unit, OldDeckCard=old, NewCardData=newCardData,
                NewCharacterData=newCharacterData, Room=room, RoomIndex=room.GetRoomIndex(),
                UnitIndex=spawn.GetIndexInRoom(), Save=save,
                MonsterManager=manager, CreateMethod=create, SilentDespawn=despawn,
                OldName=oldName, NewName=destinationName,
                Level=unit.GetStatusEffectStacks("hextrolls.plugin_level"),
                Exp=unit.GetStatusEffectStacks("hextrolls.plugin_exp"),
                NewType=DestinationType(oldName, destinationName, typeMatches[0])
            };
            return true;
        }

        private static MethodInfo FindNativeCreate(object manager, object spawn)
        {
            if (manager == null || spawn == null) return null;
            foreach (var m in manager.GetType().GetMethods(Flags)
                .Where(m => m.Name == "CreateMonsterState"))
            {
                var p = m.GetParameters();
                if (p.Length != 15 || p[0].ParameterType != typeof(CharacterData) ||
                    p[1].ParameterType != typeof(CardState) ||
                    p[2].ParameterType != typeof(int) ||
                    p[3].ParameterType != typeof(Action<CharacterState>) ||
                    !p[4].ParameterType.IsEnum ||
                    !p[5].ParameterType.IsInstanceOfType(spawn) ||
                    p[8].ParameterType != typeof(CharacterData) ||
                    p[14].ParameterType != typeof(CardState) ||
                    !typeof(IEnumerator).IsAssignableFrom(m.ReturnType)) continue;
                Info("Native CreateMonsterState matched. Param names: " +
                     String.Join(", ", p.Select(x => x.Name)));
                return m;
            }
            WarnOnce("CreateMonsterState overloads: " + String.Join(" | ",
                manager.GetType().GetMethods(Flags).Where(m => m.Name == "CreateMonsterState")
                    .Select(m => m.ToString()).Take(6)));
            return null;
        }

        private static bool IsCorrectCreated(Plan plan, CharacterState candidate, CardState expectedCard)
        {
            if (candidate == null || candidate.IsDestroyed || !candidate.IsAlive)
                return false;
            if (candidate.GetTeamType() != Team.Type.Monsters ||
                candidate.GetCurrentRoom() != plan.Room ||
                candidate.GetSourceCharacterData()?.GetID() != plan.NewCharacterData.GetID())
            {
                Warn("Created unit wrong form or room: wanted " + plan.NewCharacterData.name +
                     " got " + candidate.GetSourceCharacterData()?.name);
                return false;
            }
            CardState spawnedFrom = candidate.GetSpawnerCard();
            if (spawnedFrom == null ||
                (!object.ReferenceEquals(spawnedFrom, expectedCard) &&
                 !object.Equals(spawnedFrom.GetID(), expectedCard.GetID())))
            {
                Warn("Created unit linked to unexpected spawner CardState.");
                return false;
            }
            if (!NormalizeEvolutionStatuses(plan, candidate)) return false;
            if (candidate.GetStatusEffectStacks("hextrolls.plugin_level") != plan.Level ||
                candidate.GetStatusEffectStacks("hextrolls.plugin_exp") != plan.Exp)
            {
                Warn("Created unit did not preserve Level/EXP (before=" + plan.Level + "/" + plan.Exp +
                     ", after=" + candidate.GetStatusEffectStacks("hextrolls.plugin_level") + "/" +
                     candidate.GetStatusEffectStacks("hextrolls.plugin_exp") + ").");
                return false;
            }
            return true;
        }

        private static TrainersElement DestinationType(string oldCard, string newCard,
            TrainersElement original)
        {
            if (String.Equals(oldCard, TrainersEvolutionMechanics.BaseWhelp,
                StringComparison.OrdinalIgnoreCase))
            {
                string name = newCard.Substring("TrainersPocketMonDragon".Length);
                if (name.EndsWith("Form1Card", StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - "Form1Card".Length);
                if (TrainersElementalRules.TryParse(name, out var elemental))
                    return elemental;
            }
            return original;
        }

        private static bool NormalizeEvolutionStatuses(Plan plan, CharacterState candidate)
        {
            // Re-created unit must have exactly the destination's Type and
            // Evolve threshold. Only normalize intrinsic statuses on the NEW unit.
            try
            {
                foreach (TrainersElement kind in Enum.GetValues(typeof(TrainersElement)))
                {
                    string id = TrainersElementalRules.StatusId(kind);
                    int stacks = candidate.GetStatusEffectStacks(id);
                    if (kind != plan.NewType && stacks > 0)
                        candidate.RemoveStatusEffect(id, stacks, false);
                }
                string desired = TrainersElementalRules.StatusId(plan.NewType);
                int count = candidate.GetStatusEffectStacks(desired);
                if (count <= 0) candidate.AddStatusEffect(desired, 1);
                else if (count > 1) candidate.RemoveStatusEffect(desired, count - 1, false);

                const string Evolve = "hextrolls.plugin_evolve";
                int oldThreshold = candidate.GetStatusEffectStacks(Evolve);
                if (oldThreshold > 0)
                    candidate.RemoveStatusEffect(Evolve, oldThreshold, false);
                // Three-form lines evolve first at 16, then at 32. New Form2 is
                // the only intermediate destination; the Next route proves it.
                bool hasMore = TrainersEvolutionMechanics.Next.ContainsKey(plan.NewName);
                if (hasMore) candidate.AddStatusEffect(Evolve, 32);

                int numTypes = Enum.GetValues(typeof(TrainersElement))
                    .Cast<TrainersElement>()
                    .Count(t => candidate.GetStatusEffectStacks(TrainersElementalRules.StatusId(t)) > 0);
                return numTypes == 1 && candidate.GetStatusEffectStacks(desired) == 1 &&
                    candidate.GetStatusEffectStacks(Evolve) == (hasMore ? 32 : 0);
            }
            catch (Exception ex)
            {
                Warn("Unable to normalize evolved Type / Evolve state: " + ex);
                return false;
            }
        }

        // Registered CardData records expose both their Unity/Trainworks asset
        // names (e.g. HexTrolls.Plugin-Card-TrainersPocketMonManshrimpForm1Card)
        // and their runtime IDs (GUIDs). CardState stores the latter.
        private static string ResolveRegisteredSourceName(AllGameData data,
            string runtimeCardDataId, out CardData registered)
        {
            registered = null;
            if (data == null || String.IsNullOrWhiteSpace(runtimeCardDataId))
                return null;
            try
            {
                registered = data.GetAllCardData()?.FirstOrDefault(c => c != null &&
                    String.Equals(c.GetID(), runtimeCardDataId,
                                  StringComparison.Ordinal));
                string assetName = ShortName(registered?.name);
                if (!String.IsNullOrEmpty(assetName) && assetName.StartsWith(
                    "TrainersPocketMon", StringComparison.OrdinalIgnoreCase))
                    return assetName;
            }
            catch (Exception ex)
            {
                Warn("Runtime CardData registry lookup failed: " + ex.Message);
            }
            // Known evolution sources are also resolvable directly by their
            // registered asset keys, without depending on Unity's .name value.
            try
            {
                var known = TrainersEvolutionMechanics.Next.Keys
                    .Concat(new[] { TrainersEvolutionMechanics.BaseWhelp,
                                    TrainersEvolutionMechanics.BaseParriot })
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (string shortId in known)
                {
                    var candidate = data.FindCardDataByName(Prefix + shortId);
                    if (candidate != null && String.Equals(candidate.GetID(),
                        runtimeCardDataId, StringComparison.Ordinal))
                    {
                        registered = candidate;
                        return shortId;
                    }
                }
            }
            catch (Exception ex)
            {
                Warn("Known-card registry fallback failed: " + ex.Message);
            }
            return null;
        }

        private static string ShortName(string full)
        {
            if (String.IsNullOrEmpty(full)) return null;
            int pos = full.LastIndexOf("-Card-", StringComparison.OrdinalIgnoreCase);
            if (pos >= 0) return full.Substring(pos + 6);
            if (full.StartsWith("TrainersPocketMon", StringComparison.OrdinalIgnoreCase)) return full;
            return null;
        }

        private static bool OnFloor(RoomState room, CharacterState target)
        {
            if (room == null || target == null) return false;
            var units = new List<CharacterState>();
            try { room.AddCharactersToList(units, Team.Type.Monsters); }
            catch { return true; } // Fail closed if we can't inspect the floor.
            return units.Any(unit => object.ReferenceEquals(unit, target));
        }

        private static IEnumerator MakeDespawnCoroutine(CardEffectBase native,
            CardEffectState effect, CardEffectParams parameters, CharacterState target,
            ICoreGameManagers core, ISystemManagers systems)
        {
            if (native == null || effect == null || parameters == null ||
                parameters.targets == null || target == null) return null;
            CardEffectParams selected = parameters;
            if (parameters.targets.Count != 1 ||
                !object.ReferenceEquals(parameters.targets[0], target))
            {
                // The failed tentative replacement needs cleanup without redirecting
                // the live Gem's remaining effects or changing its selected target.
                // Create an independent CardEffectParams copy and give it ONLY the clone.
                try
                {
                    var memberwise = typeof(object).GetMethod("MemberwiseClone", Flags);
                    selected = memberwise?.Invoke(parameters, null) as CardEffectParams;
                    var field = typeof(CardEffectParams).GetField("targets", Flags);
                    if (selected == null || field == null ||
                        !field.FieldType.IsAssignableFrom(typeof(List<CharacterState>)))
                    {
                        Warn("Cannot construct independent cleanup target; preserving old unit.");
                        return null;
                    }
                    field.SetValue(selected, new List<CharacterState> { target });
                }
                catch (Exception ex)
                {
                    Warn("Could not redirect created-unit cleanup: " + ex);
                    return null;
                }
            }
            try { return native.ApplyEffect(effect, selected, core, systems); }
            catch (Exception ex)
            {
                Warn("Native silent despawn invocation failed: " + ex);
                return null;
            }
        }
    }
}
