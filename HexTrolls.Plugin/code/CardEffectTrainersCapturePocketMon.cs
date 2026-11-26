// Trainers / PocketMon Capture Spell v1.8 (copy enemy Level to permanent card).
// Adds a custom single-target spell effect. Keep the working
// TrainersWildPocketMonInvasion.cs v9.1 and TrainersRosterProtocol.cs unchanged.
// No Damage, Kill, Sacrifice, death, or Consume effects are invoked on the enemy.
// Native CardEffectDespawnCharacter performs the silent removal.
// This source deliberately resolves optional game APIs via reflection so it
// compiles without referencing internal/despawn classes directly.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TrainworksReloaded.Core;
using TrainworksReloaded.Base;
using TrainworksReloaded.Core.Interfaces;

namespace HexTrolls.Plugin.code
{
    public sealed class CardEffectTrainersCapturePocketMon : CardEffectBase
    {
        public override bool CanPlayAfterBossDead => false;
        public override bool CanApplyInPreviewMode => false;

        // Required abstract member of MT2 CardEffectBase.
        public override PropDescriptions CreateEditorInspectorDescriptions()
        {
            return new PropDescriptions();
        }

        // The game may query TestEffect before the player has selected a target.
        // Failing here makes drop-target spells unusable. Do NOT inspect
        // args.targets / save.PreviewMode at card-aim time. The effect JSON also
        // sets should_test=false; real eligibility is validated in ApplyEffect.
        public override bool TestEffect(CardEffectState state, CardEffectParams args,
            ICoreGameManagers coreGameManagers)
        {
            return true;
        }

        public override IEnumerator ApplyEffect(CardEffectState state, CardEffectParams args,
            ICoreGameManagers coreGameManagers, ISystemManagers systemManagers)
        {
            SaveManager save = coreGameManagers == null
                ? null : coreGameManagers.GetSaveManager();
            if (save == null || save.PreviewMode)
                yield break;

            CaptureTarget captured;
            if (!TrainersPocketMonCapture.TryResolve(args, out captured))
            {
                TrainersPocketMonCapture.Warn("Capture not applied: selected character must be a living enemy PocketMon with 1-9 current HP. " +
                    TrainersPocketMonCapture.TargetDebug(args));
                yield break;
            }

            // Revalidate immediately before applying the effect, because card
            // targets/HP can change between TestEffect and ApplyEffect.
            if (!TrainersPocketMonCapture.StillEligible(captured))
                yield break;

            // Use the native DESPAWN effect, not Kill, Sacrifice or a forced
            // death. This is the game's triggerless removal mechanism. The
            // native effect uses a counter; retry it with the SAME effect
            // instance, rather than spawning independent counters.
            CardEffectBase silentDespawn = TrainersPocketMonCapture.MakeSilentDespawn();
            if (silentDespawn == null)
            {
                TrainersPocketMonCapture.Warn("Game's silent despawn effect not available. Nothing captured.");
                yield break;
            }

            // IMPORTANT: Grant the permanent card BEFORE despawning the enemy.
            // If saving the card fails, leave the enemy on the train untouched.
            // Do NOT infer despawn failure from Unity scene-object presence.
            // Unity can retain CharacterState instances during async clean-up,
            // even after the native effect has removed them from combat.
            CardState addedCard;
            if (!TrainersPocketMonCapture.TryAddPermanentCard(save, captured.Card, out addedCard))
            {
                TrainersPocketMonCapture.Warn("Capture aborted: " + captured.CardName +
                    " could not be added to the permanent run deck. Enemy left untouched.");
                yield break;
            }

            // Copy the enemy's LIVE Level into the new PERMANENT card before
            // removing the enemy. This uses exactly the same Level-bonus card
            // upgrades as CardEffectTrainersGainExp, so future battles keep it.
            // Do not change the enemy's EXP or apply additional combat bonuses.
            int capturedLevel;
            if (!TrainersPocketMonCapture.TryCopyEnemyLevel(captured, addedCard, out capturedLevel))
            {
                TrainersPocketMonCapture.RollbackAddedCard(save, addedCard);
                TrainersPocketMonCapture.Warn("Capture cancelled: could not persist the " +
                    "enemy's Level on " + captured.CardName + ". Enemy left untouched.");
                yield break;
            }

            if (!TrainersPocketMonCapture.StillEligible(captured))
            {
                TrainersPocketMonCapture.RollbackAddedCard(save, addedCard);
                yield break;
            }

            // Native despawn is a coroutine and Unity may retain its CharacterState
            // scene object until a subsequent cleanup step. v1.6 incorrectly used
            // FindObjectsOfType<CharacterState>() as its success condition: the
            // freshly added (and VERIFIED) permanent card was then rolled back.
            // Invoke native despawn ONCE. The game owns the asynchronous cleanup.
            IEnumerator action = null;
            try
            {
                action = silentDespawn.ApplyEffect(state, args, coreGameManagers, systemManagers);
            }
            catch (Exception ex)
            {
                // The removal call could not even be started, so undo the card.
                TrainersPocketMonCapture.RollbackAddedCard(save, addedCard);
                TrainersPocketMonCapture.Warn("Native silent despawn could not start; capture reversed: " + ex);
                yield break;
            }
            if (action == null)
            {
                TrainersPocketMonCapture.RollbackAddedCard(save, addedCard);
                TrainersPocketMonCapture.Warn("Native silent despawn returned no coroutine; capture reversed.");
                yield break;
            }
            yield return action;

            // Check the *permanent SaveManager deck* (which is what the player
            // requested). Do not roll back based on transient scene objects.
            int savedCount = -1;
            bool cardStillInRunDeck = false;
            try
            {
                var runDeck = save.GetDeckState();
                if (runDeck != null)
                {
                    savedCount = runDeck.Count;
                    cardStillInRunDeck = runDeck.Contains(addedCard);
                }
            }
            catch (Exception ex)
            {
                TrainersPocketMonCapture.Warn("Post-despawn run deck check failed: " + ex);
            }
            if (!cardStillInRunDeck)
            {
                TrainersPocketMonCapture.Warn("Despawn coroutine returned for " +
                    captured.CharacterName + ", but the permanent card could not " +
                    "be confirmed afterward. runDeckCount=" + savedCount +
                    ". Check for unrelated deck-removal code.");
            }
            else
            {
                TrainersPocketMonCapture.Log("CAPTURED " + captured.CharacterName +
                    " -> permanent run-deck card " + captured.CardName +
                    "; enemyLevel=" + (capturedLevel > 0 ? capturedLevel.ToString() : "none") +
                    "; runDeckCount=" + savedCount +
                    "; native silent-despawn coroutine completed. " +
                    "Card remains in the run deck (not automatically in combat hand).");
            }
        }
    }

    internal sealed class CaptureTarget
    {
        internal CharacterState Unit;
        internal CharacterData Character;
        internal CardData Card;
        internal string CharacterName;
        internal string CardName;
        internal string LevelUpgradeName;
    }

    internal static class TrainersPocketMonCapture
    {
        private const string Prefix = "TrainersPocketMon";
        private const int MaxEligibleHP = 9; // Less than 10 CURRENT HP.
        private const string LevelStatusId = "hextrolls.plugin_level";
        // Safety limit: do not create unbounded card-upgrade lists from a bad stack value.
        private const int MaxCapturedLevel = 1000;
        private static readonly BindingFlags Flags = BindingFlags.Public |
            BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly string[] Lines = {
            "Cuttlfish:3", "Hyena:2", "BloodWorm:2", "Snapsaurus:3",
            "Teslasaurus:3", "Garhound:3", "Fireary:1", "Frosset:3",
            "Umbrabeak:3", "Magmoth:2", "Chilload:1", "DragonMoth:3",
            "Leafshrew:2", "Manshrimp:3", "Jourmancoil:2", "Vampirant:3"
        };
        private static readonly Dictionary<string, CardData> CardByCharacterId =
            new Dictionary<string, CardData>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> NameByCharacterId =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> LevelUpgradeByCharacterId =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool catalogComplete;
        private static bool loggedCatalog;
        private static readonly HashSet<string> Once = new HashSet<string>();
        private static Type despawnType;
        private static bool despawnTypeChecked;

        internal static void Log(string message) =>
            HexTrolls.Plugin.Plugin.Logger.LogInfo("[TrainersCapture] " + message);
        internal static void Warn(string message) =>
            HexTrolls.Plugin.Plugin.Logger.LogWarning("[TrainersCapture] " + message);
        internal static void WarnOnce(string label, Exception ex)
        {
            if (Once.Add(label)) Warn(label + ": " + ex);
        }

        private static bool EnsureCatalog()
        {
            if (catalogComplete) return true;
            try
            {
                var container = Railend.GetContainer();
                if (container == null) return false;
                var characters = container.GetInstance<IRegister<CharacterData>>();
                var cards = container.GetInstance<IRegister<CardData>>();
                if (characters == null || cards == null) return false;
                CardByCharacterId.Clear();
                NameByCharacterId.Clear();
                LevelUpgradeByCharacterId.Clear();
                int found = 0;
                foreach (string line in Lines)
                {
                    var parts = line.Split(':');
                    string family = parts[0];
                    int total = int.Parse(parts[1]);
                    for (int form = 1; form <= total; ++form)
                    {
                        string charKey = Prefix + family + "Form" + form + "Character";
                        string cardKey = Prefix + family + "Form" + form + "Card";
                        CharacterData character = FindCharacter(characters, charKey);
                        CardData card = FindCard(cards, cardKey);
                        if (character == null || card == null) continue;
                        string id = character.GetID(); // Trainworks' actual GUID, not asset name.
                        if (String.IsNullOrEmpty(id)) continue;
                        CardByCharacterId[id] = card;
                        NameByCharacterId[id] = character.GetName();
                        LevelUpgradeByCharacterId[id] = Prefix + family +
                            "Form" + form + "LevelStatBonus";
                        ++found;
                    }
                }
                // Never capture a lookalike or an unregistered character.
                catalogComplete = found == 39;
                if (!loggedCatalog)
                {
                    loggedCatalog = true;
                    Log("Resolved " + found + "/39 PocketMon character-to-card pairs." +
                        (catalogComplete ? "" : " Retrying when missing registrations become available."));
                }
                return CardByCharacterId.Count > 0;
            }
            catch (Exception ex)
            {
                WarnOnce("Catalog resolution", ex);
                return false;
            }
        }

        private static CharacterData FindCharacter(IRegister<CharacterData> registry, string key)
        {
            CharacterData result;
            foreach (var name in Keys("Character", key))
                if (registry.TryGetValue(name, out result) && result != null) return result;
            return null;
        }

        private static CardData FindCard(IRegister<CardData> registry, string key)
        {
            CardData result;
            foreach (var name in Keys("Card", key))
                if (registry.TryGetValue(name, out result) && result != null) return result;
            return null;
        }

        private static IEnumerable<string> Keys(string kind, string key)
        {
            yield return MyPluginInfo.PLUGIN_GUID + "-" + kind + "-" + key;
            yield return MyPluginInfo.PLUGIN_GUID + "-" + kind + "Data-" + key;
            yield return MyPluginInfo.PLUGIN_GUID + "-" + key;
            yield return key;
        }

        // Only read this when the card was actually cast. Target lists are often
        // empty before drop-target selection has completed.
        internal static string TargetDebug(CardEffectParams args)
        {
            if (args == null) return "CardEffectParams=null";
            if (args.targets == null) return "targets=null";
            if (args.targets.Count == 0) return "targets=0 (drop target not forwarded)";
            var target = args.targets[0];
            if (target == null) return "targets=" + args.targets.Count + ", first=null";
            string desc = "targets=" + args.targets.Count;
            try { desc += ", team=" + target.GetTeamType(); } catch { }
            try { desc += ", source=" + (target.GetSourceCharacterData()?.GetName() ?? "none"); } catch { }
            int hp;
            if (ReadCurrentHP(target, out hp)) desc += ", hp=" + hp;
            else desc += ", hp=unknown";
            return desc;
        }

        internal static bool TryResolve(CardEffectParams args, out CaptureTarget result)
        {
            result = null;
            if (args == null || !EnsureCatalog())
                return false;
            CharacterState unit = ReadSelectedTarget(args);
            if (unit == null || unit.GetTeamType() != Team.Type.Heroes) return false;

            CharacterData character = unit.GetSourceCharacterData();
            if (character == null) return false;
            string id = character.GetID();
            CardData card;
            if (String.IsNullOrEmpty(id) || !CardByCharacterId.TryGetValue(id, out card) || card == null)
                return false;

            int hp;
            if (!ReadCurrentHP(unit, out hp) || hp < 1 || hp > MaxEligibleHP)
                return false;
            result = new CaptureTarget {
                Unit = unit,
                Character = character,
                Card = card,
                CharacterName = character.GetName(),
                CardName = card.GetName(),
                LevelUpgradeName = LevelUpgradeByCharacterId[id]
            };
            return true;
        }

        internal static bool StillEligible(CaptureTarget target)
        {
            if (target == null || target.Unit == null ||
                target.Unit.GetTeamType() != Team.Type.Heroes || !StillPresent(target.Unit))
                return false;
            var source = target.Unit.GetSourceCharacterData();
            if (source == null || source.GetID() != target.Character.GetID()) return false;
            int hp;
            return ReadCurrentHP(target.Unit, out hp) && hp > 0 && hp <= MaxEligibleHP;
        }

        // Do not capture an arbitrary occupant: use the exact drop-selected target.
        // MT2 exposes the resolved characters as CardEffectParams.targets.
        private static CharacterState ReadSelectedTarget(CardEffectParams args)
        {
            if (args == null) return null;
            if (args.targets != null)
            {
                if (args.targets.Count != 1) return null;
                return args.targets[0];
            }
            object boxed = (object)args;
            foreach (string name in new[] {
                "targets", "targetCharacters", "targetCharacterStates",
                "targetCharacter", "dropTargetCharacter", "selectedTargetCharacter"
            })
            {
                object data = GetMember(boxed, name);
                CharacterState found = FirstCharacter(data);
                if (found != null) return found;
            }
            // On some game builds the field is called "targets" with a prefix.
            // Accept only a single filtered target; do not guess from the room.
            foreach (var field in boxed.GetType().GetFields(Flags))
            {
                if (field.Name.IndexOf("target", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                var target = FirstCharacter(field.GetValue(boxed));
                if (target != null) return target;
            }
            return null;
        }

        private static CharacterState FirstCharacter(object data)
        {
            if (data is CharacterState) return (CharacterState)data;
            if (data is IEnumerable && !(data is string))
            {
                foreach (var element in (IEnumerable)data)
                    if (element is CharacterState) return (CharacterState)element;
            }
            return null;
        }

        private static object GetMember(object obj, string name)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            try
            {
                var field = t.GetField(name, Flags);
                if (field != null) return field.GetValue(obj);
                var property = t.GetProperty(name, Flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                    return property.GetValue(obj, null);
            }
            catch { }
            return null;
        }

        private static bool ReadCurrentHP(CharacterState target, out int hp)
        {
            hp = -1;
            foreach (string name in new[] {
                "GetHP", "GetCurrentHP", "GetCurrentHealth", "GetHealth", "HP"
            })
            {
                try
                {
                    MethodInfo method = target.GetType().GetMethod(name, Flags,
                        null, Type.EmptyTypes, null);
                    object value = method != null ? method.Invoke(target, null) : GetMember(target, name);
                    if (value is int) { hp = (int)value; return true; }
                    if (value is float) { hp = (int)Math.Ceiling((float)value); return true; }
                }
                catch { }
            }
            return false; // Fail closed: do not capture if current HP is unknown.
        }

        internal static bool StillPresent(CharacterState unit)
        {
            if (unit == null) return false;
            try
            {
                foreach (var live in UnityEngine.Object.FindObjectsOfType<CharacterState>())
                    if (object.ReferenceEquals(live, unit)) return true;
            }
            catch { return true; }
            return false;
        }

        internal static bool HasSilentDespawn()
        {
            if (!despawnTypeChecked)
            {
                despawnTypeChecked = true;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var candidate = assembly.GetType("CardEffectDespawnCharacter");
                        if (candidate != null && typeof(CardEffectBase).IsAssignableFrom(candidate))
                        {
                            despawnType = candidate;
                            break;
                        }
                    }
                    catch { }
                }
                if (despawnType == null)
                    Warn("Native CardEffectDespawnCharacter type not found. Capture disabled safely.");
            }
            return despawnType != null;
        }

        internal static CardEffectBase MakeSilentDespawn()
        {
            if (!HasSilentDespawn()) return null;
            try { return Activator.CreateInstance(despawnType) as CardEffectBase; }
            catch (Exception ex) { WarnOnce("Constructing native despawn", ex); return null; }
        }

        // SaveManager's native deck method is used directly (same signature as
        // MT2StartingDraft / Dual Champions 2). Avoid the old 9-parameter
        // reflection lookup which failed without a clear reason after despawn.
        // Fallback creates a real CardState in the live permanent SaveManager
        // deck in case the native method is disabled during active combat.
        internal static bool TryAddPermanentCard(SaveManager save, CardData card,
            out CardState addedCard)
        {
            addedCard = null;
            if (save == null || card == null || save.PreviewMode)
                return false;

            List<CardState> existingDeck;
            try { existingDeck = save.GetDeckState(); }
            catch (Exception ex)
            {
                Warn("Could not read permanent run deck: " + ex);
                return false;
            }
            if (existingDeck == null)
            {
                Warn("SaveManager.GetDeckState() returned null; capture blocked.");
                return false;
            }

            var before = new HashSet<CardState>(existingDeck);
            string wantedId = card.GetID();
            Log("Attempting permanent addition: " + card.GetName() +
                " (cardDataId=" + wantedId + ", runDeckBefore=" + before.Count + ").");

            try
            {
                // extraCopies = 0 means exactly one new copy.
                save.AddCardToDeck(card, (CardStateModifiers)null,
                    false, 0, false, false, true, false, true);
            }
            catch (Exception ex)
            {
                Warn("Native AddCardToDeck threw (will inspect deck before fallback): " + ex);
            }

            try
            {
                // Validate an actual NEW CardState with the expected CardData,
                // not merely a deck-count change caused by an unrelated effect.
                List<CardState> deck = save.GetDeckState();
                if (deck == null)
                    return false;
                addedCard = deck.FirstOrDefault(state =>
                    state != null && !before.Contains(state) &&
                    String.Equals(state.GetCardDataID(), wantedId,
                        StringComparison.OrdinalIgnoreCase));
                if (addedCard != null)
                {
                    Log("Native permanent deck add VERIFIED: " + card.GetName() +
                        "; runDeckAfter=" + deck.Count + ".");
                    return true;
                }

                // Defensive fallback. SaveManager.GetDeckState() is the live
                // run-deck list, used by other MT2 mods for permanent deck ops.
                Warn("Native AddCardToDeck created no visible " + card.GetName() +
                    " CardState (runDeckAfter=" + deck.Count +
                    "). Trying persistent-deck CardState fallback.");
                var stateToAdd = new CardState(card, save, false, false);
                stateToAdd.RefetchAssets(card);
                if (!String.Equals(stateToAdd.GetCardDataID(), wantedId,
                    StringComparison.OrdinalIgnoreCase))
                {
                    Warn("Fallback CardState did not match " + wantedId +
                        "; leaving enemy untouched.");
                    return false;
                }
                deck.Add(stateToAdd);
                if (!deck.Contains(stateToAdd) ||
                    !save.GetDeckState().Contains(stateToAdd))
                {
                    Warn("Fallback deck insertion not reflected in saved deck.");
                    return false;
                }
                addedCard = stateToAdd;
                Log("Fallback permanent deck add VERIFIED: " + card.GetName() +
                    "; runDeckAfter=" + deck.Count + ".");
                return true;
            }
            catch (Exception ex)
            {
                Warn("Saving captured unit card failed; target remains untouched: " + ex);
                return false;
            }
        }

        // Read the actual enemy's *current* Level, not its species default,
        // form number, enemy HP, or the newly-created player's card level.
        // Enemy-replacement characters can have no Level status at all. In
        // that case preserve the new card's own baseline, and log the fact.
        private static int ReadEnemyLevel(CharacterState enemy)
        {
            if (enemy == null) return 0;
            try
            {
                int stacks = enemy.GetStatusEffectStacks(LevelStatusId);
                if (stacks > 0) return stacks;
                var statuses = new List<CharacterState.StatusEffectStack>();
                enemy.GetStatusEffects(ref statuses);
                foreach (CharacterState.StatusEffectStack entry in statuses)
                {
                    if (entry.State == null) continue;
                    string id = entry.State.GetStatusId();
                    if (String.Equals(id, LevelStatusId, StringComparison.OrdinalIgnoreCase) ||
                        (id != null && id.StartsWith("hextrolls.plugin", StringComparison.OrdinalIgnoreCase) &&
                         (id.EndsWith("_level", StringComparison.OrdinalIgnoreCase) ||
                          id.EndsWith("-level", StringComparison.OrdinalIgnoreCase))))
                    {
                        int alternate = enemy.GetStatusEffectStacks(id);
                        if (alternate > 0) return alternate;
                    }
                }
            }
            catch (Exception ex)
            {
                WarnOnce("Reading enemy's current Level", ex);
            }
            return 0;
        }

        private static bool TryGetLevelUpgrade(string name,
            IRegister<CardUpgradeData> registry,
            out CardUpgradeData template, out string runtimeId)
        {
            template = null;
            runtimeId = null;
            if (!registry.TryGetValue(MyPluginInfo.PLUGIN_GUID + "-Upgrade-" + name,
                out template) || template == null)
            {
                Warn("Cannot find registered Level upgrade " + name);
                return false;
            }
            var probe = new CardUpgradeState();
            probe.Setup(template, false, false);
            runtimeId = probe.GetCardUpgradeDataId();
            if (String.IsNullOrEmpty(runtimeId))
            {
                Warn("No persisted ID for Level upgrade " + name);
                return false;
            }
            return true;
        }

        private static int CountUpgrade(IList<CardUpgradeState> upgrades, string runtimeId)
        {
            return upgrades.Count(upgrade => upgrade != null &&
                String.Equals(upgrade.GetCardUpgradeDataId(), runtimeId,
                    StringComparison.OrdinalIgnoreCase));
        }

        // Make the captured card's permanent progression exactly match the
        // enemy's observable Level. Uses existing per-form stat+Level upgrades;
        // no new status definitions, extra stats, or changes to EXP are needed.
        // Does not touch any other card or card upgrades.
        internal static bool TryCopyEnemyLevel(CaptureTarget target,
            CardState newCard, out int storedLevel)
        {
            storedLevel = 0;
            if (target == null || target.Unit == null || newCard == null ||
                String.IsNullOrEmpty(target.LevelUpgradeName))
                return false;
            try
            {
                var modifiers = newCard.GetCardStateModifiers();
                if (modifiers == null) { Warn("Captured card has no modifiers."); return false; }
                var upgrades = modifiers.GetCardUpgrades() as IList<CardUpgradeState>;
                if (upgrades == null) { Warn("Captured card upgrade list is not writable."); return false; }

                int enemyLevel = ReadEnemyLevel(target.Unit);
                if (enemyLevel == 0)
                {
                    // Some invasion-generated enemy forms have no Level
                    // status. Do not assign an invented Level or remove the
                    // pre-existing initial upgrades on their captured card.
                    storedLevel = 0; // Unknown; not a claim that the card is Level 0.
                    Log("Enemy " + target.CharacterName + " has no Level status; " +
                        "preserving the captured card's configured starting Level " +
                        "and stat upgrades without modification.");
                    return true;
                }
                if (enemyLevel > MaxCapturedLevel)
                {
                    Warn("Refusing invalid enemy Level " + enemyLevel +
                        " (max supported " + MaxCapturedLevel + ").");
                    return false;
                }

                var container = Railend.GetContainer();
                var registry = container == null ? null :
                    container.GetInstance<IRegister<CardUpgradeData>>();
                if (registry == null) { Warn("Level upgrade registry is unavailable."); return false; }

                CardUpgradeData baseTemplate, formTemplate;
                string baseId, formId;
                if (!TryGetLevelUpgrade("TrainersLevelBaseMarker", registry,
                        out baseTemplate, out baseId) ||
                    !TryGetLevelUpgrade(target.LevelUpgradeName, registry,
                        out formTemplate, out formId))
                    return false;

                int oldLevel = 1 + CountUpgrade(upgrades, formId);

                // Base marker = Level 1 (no stats). Each of the same form's
                // stat-bonus upgrades contributes one additional Level.
                // Remove surplus initial bonuses for lower-level captures and
                // add missing bonuses for higher-level captures. These are
                // permanent modifiers, like Multistrike, not temporary stacks.
                for (int index = upgrades.Count - 1;
                     index >= 0 && CountUpgrade(upgrades, baseId) > 1; --index)
                {
                    if (upgrades[index] != null && String.Equals(
                        upgrades[index].GetCardUpgradeDataId(), baseId,
                        StringComparison.OrdinalIgnoreCase))
                        upgrades.RemoveAt(index);
                }
                if (CountUpgrade(upgrades, baseId) == 0)
                {
                    var marker = new CardUpgradeState();
                    marker.Setup(baseTemplate, false, false);
                    modifiers.AddUpgrade(marker);
                }

                upgrades = modifiers.GetCardUpgrades() as IList<CardUpgradeState>;
                int desiredBonuses = enemyLevel - 1;
                int existingBonuses = CountUpgrade(upgrades, formId);
                for (int index = upgrades.Count - 1;
                     index >= 0 && existingBonuses > desiredBonuses; --index)
                {
                    if (upgrades[index] != null && String.Equals(
                        upgrades[index].GetCardUpgradeDataId(), formId,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        upgrades.RemoveAt(index);
                        --existingBonuses;
                    }
                }
                for (int i = existingBonuses; i < desiredBonuses; ++i)
                {
                    var bonus = new CardUpgradeState();
                    bonus.Setup(formTemplate, false, false);
                    modifiers.AddUpgrade(bonus);
                }
                upgrades = modifiers.GetCardUpgrades() as IList<CardUpgradeState>;
                if (upgrades == null || CountUpgrade(upgrades, baseId) != 1 ||
                    CountUpgrade(upgrades, formId) != desiredBonuses)
                {
                    Warn("Permanent Level upgrades did not commit for " +
                        target.CardName + "; capture cancelled.");
                    return false;
                }
                storedLevel = 1 + CountUpgrade(upgrades, formId);
                newCard.UpdateCardBodyText();
                Log("Copied enemy Level " + enemyLevel + " to " + target.CardName +
                    " permanent card (previous starting Level " + oldLevel +
                    ", saved Level " + storedLevel + ").");
                return true;
            }
            catch (Exception ex)
            {
                Warn("Saving captured Level failed: " + ex);
                return false;
            }
        }

        internal static void RollbackAddedCard(SaveManager save, CardState addedCard)
        {
            if (save == null || addedCard == null) return;
            try
            {
                var deck = save.GetDeckState();
                if (deck != null && deck.Contains(addedCard))
                {
                    save.RemoveCardFromDeck(addedCard);
                    Log("Rolled back only the pending capture's newly-added card.");
                }
            }
            catch (Exception ex)
            {
                Warn("Could not rollback the pending capture card: " + ex);
            }
        }
    }
}
