using Conductor.UI;
using HarmonyLib;
using System.Reflection;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using TrainworksReloaded.Base;
using TrainworksReloaded.Core;
using TrainworksReloaded.Core.Interfaces;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HexTrolls.Plugin.code
{
    // One handler per Conductor-owned small HUD. Unlike the old overlay,
    // this object is part of the actual run HUD, with the normal EventSystem.
    public sealed class TrainersRosterSlotClick : MonoBehaviour,
        IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public int Slot;
        private RectTransform target;

        public void SetVisual(RectTransform visual)
        {
            target = visual != null ? visual : transform as RectTransform;
        }

        // The roster's approved zigzag component owns ALL visual scaling.
        // Do not animate this root transform: it would undo that sizing.
        public void OnPointerEnter(PointerEventData eventData)
        {
            SoundManager.PlaySfxSignal?.Dispatch("UI_Hover");
        }
        public void OnPointerExit(PointerEventData eventData) { }
        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            TrainersNativeRosterController.HandleSlotClick(Slot, gameObject);
        }
    }

    // Temporary UI is created only when the player opens the deck picker.
    // The six persistent buttons themselves are Conductor native run HUDs.
    public sealed class TrainersRosterPickerGuard : MonoBehaviour
    {
        private void Update()
        {
            if (!TrainersNativeRosterController.CanEdit())
                TrainersNativeRosterController.ClosePicker();
        }
    }

    public static class TrainersNativeRosterController
    {
        private const int SlotCount = 6;
        private const int RowsPerPage = 8;
        private static readonly CardUpgradeData[] markerTemplates = new CardUpgradeData[SlotCount];
        private static readonly string[] markerIds = new string[SlotCount];
        private static readonly GameObject[] hudObjects = new GameObject[SlotCount];
        private static readonly TextMeshProUGUI[] labels = new TextMeshProUGUI[SlotCount];
        private static string trainersRuntimeGuid;
        private static GameObject picker;
        private static int selectedSlot = -1;
        private static int page;
        private static bool ready;
        private static float nextAutoInitAttempt;
        private static float nextHudRefresh;
        private static bool autoInitLogged;
        // Roster cards can be called once per battle, independently for each slot.
        // Reset on the actual inactive -> active combat transition, not each turn.
        // Each roster marker is attached to ONE persistent CardState, not its base card ID.
        // A slot may be called repeatedly while its unit survives (hand/deck/discard/field).
        private static readonly bool[] rosterDead = new bool[SlotCount];
        private static readonly bool[] actionPending = new bool[SlotCount];
        private static readonly CardState[] miniCardOwners = new CardState[SlotCount];
        private static readonly GameObject[] miniCardHudOwners = new GameObject[SlotCount];
        private static readonly Image[] miniCardBlackout = new Image[SlotCount];
        private static bool combatWasRunning;
        private static bool deathHookInstalled;
        private static bool pileApiWarningLogged;
        private static bool cardBadgeHookInstalled;
        private const string CardBadgeObjectName = "TrainersRosterSlotAlwaysVisible";
        private static CardUI canonicalDeckCardPrefab;
        // Registered in json/status_effects/TrainersRosterSlot.json. The stack
        // count is the ONE-based roster slot number (1-6), not a power level.
        private const string RosterSlotStatusId = "hextrolls.plugin_roster_slot";
        private static float nextRosterStatusSync;
        private static bool rosterStatusWarningLogged;


        // Called from the live Conductor HUD's LateUpdate. This avoids requiring
        // a separate manual Plugin.cs callback that may never have been added.
        // The layout is NOT touched here: TrainersRosterZigZagLayout owns it.
        public static void OnLiveHudFrame()
        {
            UpdateBattleTransition();
            if (!ready)
            {
                if (Time.unscaledTime < nextAutoInitAttempt) return;
                nextAutoInitAttempt = Time.unscaledTime + 1f;
                try
                {
                    var container = Railend.GetContainer();
                    if (container == null) return;
                    var upgrades = container.GetInstance<IRegister<CardUpgradeData>>();
                    var classes = container.GetInstance<IRegister<ClassData>>();
                    Initialize(upgrades, classes);
                    if (ready && !autoInitLogged)
                    {
                        autoInitLogged = true;
                        Info("AUTO INITIALIZED from live Conductor HUD (no Plugin.cs change required).");
                    }
                }
                catch (Exception ex)
                {
                    Warn("Auto initialization pending: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            if (!ready) return;
            if (combatWasRunning && Time.unscaledTime >= nextRosterStatusSync)
            {
                nextRosterStatusSync = Time.unscaledTime + 0.75f;
                SyncDeployedRosterStatus();
            }
            if (Time.unscaledTime < nextHudRefresh) return;
            nextHudRefresh = Time.unscaledTime + 0.75f;
            AttachAllHuds();
            RefreshLabels();
        }

        // Optional: a manual call in the existing Railend.ConfigurePostAction
        // also works. Both paths are idempotent.
        public static void Initialize(IRegister<CardUpgradeData> upgrades,
            IRegister<ClassData> classes)
        {
            if (ready) return;
            string classKey = MyPluginInfo.PLUGIN_GUID + "-Class-TrainersClan";
            ClassData trainers;
            if (classes == null || !classes.TryGetValue(classKey, out trainers) || trainers == null)
            {
                Warn("Trainers class definition missing: " + classKey);
                return;
            }
            trainersRuntimeGuid = trainers.GetID();
            for (int i = 0; i < SlotCount; ++i)
            {
                string shortName = "TrainersRosterSlot" + (i + 1).ToString("D2");
                string key = MyPluginInfo.PLUGIN_GUID + "-Upgrade-" + shortName;
                CardUpgradeData template;
                if (upgrades == null || !upgrades.TryGetValue(key, out template) || template == null)
                {
                    Warn("Missing slot marker " + key + "; is TrainersDeckDeploy.json merged?");
                    return;
                }
                var probe = new CardUpgradeState();
                probe.Setup(template, false, false);
                markerTemplates[i] = template;
                markerIds[i] = probe.GetCardUpgradeDataId();

                // Check the *finalized* upgrade definition, not merely the
                // saved assignment marker. A marker with no trait can be
                // assigned successfully without ever granting Deployable.
                var addedTraits = template.GetTraitDataUpgrades();
                if (addedTraits == null || addedTraits.Count == 0)
                    Warn("Roster slot " + (i + 1) + " has NO trait upgrades; verify TrainersDeckDeploy.json is merged and finalized.");
                if (String.IsNullOrEmpty(markerIds[i]))
                {
                    Warn("No runtime marker ID for " + key);
                    return;
                }
            }
            ready = true;
            InstallDeathHook();
            InstallCardBadgeHook();
            Info("Native Conductor roster: Trainers GUID=" + trainersRuntimeGuid + "; six markers ready.");
            AttachAllHuds();
        }

        public static void AttachAllHuds()
        {
            if (!ready) return;
            for (int i = 0; i < SlotCount; ++i)
            {
                string name = "TrainersRosterHud" + (i + 1).ToString("D2");
                var hud = HudManager.GetHUD(MyPluginInfo.PLUGIN_GUID, name);
                if (hud == null) continue;
                var obj = hud.gameObject;
                if (obj == null) continue;
                var handler = obj.GetComponent<TrainersRosterSlotClick>();
                bool needsAttach = hudObjects[i] != obj || handler == null ||
                                   handler.Slot != i || labels[i] == null;
                if (!needsAttach) continue;

                hudObjects[i] = obj;

                // Transparent full-root graphic creates a reliable pointer hitbox.
                // Decorative child images must not intercept the parent click.
                var hitbox = obj.GetComponent<Graphic>();
                if (hitbox == null)
                {
                    var transparent = obj.AddComponent<Image>();
                    transparent.color = new Color(0f, 0f, 0f, 0f);
                    hitbox = transparent;
                }
                hitbox.raycastTarget = true;
                foreach (var graphic in obj.GetComponentsInChildren<Graphic>(true))
                    if (graphic != hitbox) graphic.raycastTarget = false;

                if (handler == null) handler = obj.AddComponent<TrainersRosterSlotClick>();
                handler.Slot = i;
                handler.SetVisual(obj.transform as RectTransform);
                labels[i] = EnsureLabel(obj, "RosterSlotText", 13f);
                miniCardHudOwners[i] = null; // HUD was recreated; rebuild its card art.
                Info("Clickable Conductor slot attached: " + name);
            }
            RefreshLabels();
        }

        // The live Conductor HUD subclass (TrainersRosterSlotHud) applies
        // the stagger after Unity's Canvas layout pass. Do not reparent HUDs
        // here: Conductor can recreate its UI later in the run.
        private static void ApplyZigZagLayout() { }

        private static TextMeshProUGUI EnsureLabel(GameObject hud, string name, float size)
        {
            Transform child = hud.transform.Find(name);
            GameObject obj;
            if (child != null) obj = child.gameObject;
            else
            {
                obj = new GameObject(name, typeof(RectTransform));
                obj.transform.SetParent(hud.transform, false);
            }
            var rect = obj.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, -6f);
            rect.sizeDelta = new Vector2(0f, 36f);
            var text = obj.GetComponent<TextMeshProUGUI>();
            if (text == null) text = obj.AddComponent<TextMeshProUGUI>();
            text.fontSize = size;
            text.fontStyle = FontStyles.Bold;
            text.alignment = TextAlignmentOptions.Center;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.color = Color.white;
            text.raycastTarget = false;
            return text;
        }

        private static bool IsCombatActive()
        {
            var all = AllGameManagers.Instance;
            var combat = all == null ? null : all.GetCombatManager();
            return combat != null && combat.GetIsRunningCombat();
        }

        private static void UpdateBattleTransition()
        {
            bool active = IsCombatActive();
            if (active && !combatWasRunning)
            {
                Array.Clear(rosterDead, 0, rosterDead.Length);
                Array.Clear(actionPending, 0, actionPending.Length);
                pileApiWarningLogged = false;
                Info("New battle: roster live-card state reset.");
                RefreshLabels();
            }
            if (!active && combatWasRunning)
                Info("Battle ended; roster assignments can be edited again.");
            combatWasRunning = active;
        }

        // Between battles an icon edits its assigned unit. During battle it
        // draws that exact saved card into the real game hand; the player then
        // selects a floor and pays the normal card cost as usual.
        public static void HandleSlotClick(int slot, GameObject clickedHud)
        {
            if (slot < 0 || slot >= SlotCount) return;
            UpdateBattleTransition();
            if (IsCombatActive())
            {
                CallUnitIntoHand(slot);
                return;
            }
            OpenPicker(slot, clickedHud);
        }

        // Click routing is stateful: don't generate cards when their unit is on
        // the battlefield, already in hand, dead, exhausted or otherwise absent.
        private static void CallUnitIntoHand(int slot)
        {
            if (!ready || !IsCombatActive()) return;
            var all = AllGameManagers.Instance;
            var save = all == null ? null : all.GetSaveManager();
            var cards = all == null ? null : all.GetCardManager();
            if (save == null || cards == null || save.PreviewMode) return;
            var owner = FindOwner(save, slot);
            if (owner == null || rosterDead[slot] || actionPending[slot])
            {
                InvalidRosterClick();
                return;
            }
            try
            {
                // First check for the living CharacterState, not card data ID.
                var deployed = FindDeployedUnit(slot, owner);
                if (deployed != null)
                {
                    RecallUnit(slot, deployed);
                    return;
                }
                if (cards.GetHand(false).Any(c => IsSameRosterCopy(c, owner, slot)))
                {
                    Info("Slot " + (slot + 1) + ": card already in hand.");
                    InvalidRosterClick();
                    return;
                }

                bool canInspect;
                bool inDraw = InCombatPile(cards, owner, slot, false, out canInspect);
                bool discardInspect;
                bool inDiscard = InCombatPile(cards, owner, slot, true, out discardInspect);
                if (!canInspect || !discardInspect)
                {
                    if (!pileApiWarningLogged)
                    {
                        pileApiWarningLogged = true;
                        Warn("Could not verify both battle card piles; refusing to create a duplicate. " +
                             "Available CardManager pile methods: " + DescribePileMethods(cards));
                    }
                    InvalidRosterClick();
                    return;
                }
                if (!inDraw && !inDiscard)
                {
                    Info("Slot " + (slot + 1) + ": card is neither in draw nor discard. " +
                         "It cannot be called while played, exhausted, or missing.");
                    InvalidRosterClick();
                    return;
                }

                save.DrawSpecificCard(owner); // same upgraded card copy; no new CardState
                Info("Roster draw from " + (inDraw ? "deck" : "discard") +
                     ": slot " + (slot + 1) + " -> " + DisplayName(owner));
                RefreshLabels();
                SoundManager.PlaySfxSignal?.Dispatch("UI_Hover");
            }
            catch (Exception ex)
            {
                Warn("Roster call failed: " + ex);
                InvalidRosterClick();
            }
        }

        private static void InvalidRosterClick()
        {
            SoundManager.PlaySfxSignal?.Dispatch("UI_PlayInvalidCard");
        }

        private static bool IsSameRosterCopy(CardState card, CardState owner, int slot)
        {
            return card != null && (ReferenceEquals(card, owner) || HasMarker(card, slot));
        }

        // Inspect ONLY the combat piles, never SaveManager.GetDeckState(): the
        // permanent deck contains cards even after they have been played.
        // Monster Train 2's CardManager exposes GetDrawPile(bool) and
        // GetDiscardPile(bool), NOT parameterless getters. It also exposes
        // IsCardInDrawPile(CardState) / IsCardInDiscardPile(CardState).
        // We use both routes so identical PocketMon copies remain distinct.
        private static bool InCombatPile(object cards, CardState owner, int slot,
            bool discard, out bool inspected)
        {
            inspected = false;
            string getterName = discard ? "GetDiscardPile" : "GetDrawPile";
            var getter = cards.GetType().GetMethod(getterName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(bool) }, null);
            if (getter != null)
            {
                try
                {
                    // false means the standard, visible combat pile in this
                    // game build; it does not shuffle or move any cards.
                    var pile = getter.Invoke(cards, new object[] { false });
                    var liveCards = ExtractCards(pile);
                    if (liveCards != null)
                    {
                        inspected = true;
                        if (liveCards.Any(c => IsSameRosterCopy(c, owner, slot)))
                            return true;
                    }
                }
                catch (Exception ex)
                {
                    Warn("Could not read " + getterName + "(false): " + ex.Message);
                }
            }

            // The membership predicates are a second, official card-state
            // check, and also work when the pile getter returns a custom type.
            string predicateName = discard ? "IsCardInDiscardPile" : "IsCardInDrawPile";
            var predicate = cards.GetType().GetMethod(predicateName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(CardState) }, null);
            if (predicate != null && predicate.ReturnType == typeof(bool))
            {
                try
                {
                    var answer = predicate.Invoke(cards, new object[] { owner });
                    if (answer is bool)
                    {
                        inspected = true;
                        if ((bool)answer) return true;
                    }
                }
                catch (Exception ex)
                {
                    Warn("Could not run " + predicateName + ": " + ex.Message);
                }
            }
            return false;
        }

        private static List<CardState> ExtractCards(object source)
        {
            if (source == null || source is string) return null;
            if (source is IEnumerable enumerable)
            {
                var list = new List<CardState>();
                foreach (var entry in enumerable)
                {
                    if (entry is CardState card) list.Add(card);
                    else if (entry != null) return null; // not a CardState collection
                }
                return list;
            }
            foreach (var name in new[] { "GetCards", "GetCardStates", "GetCardsInPile" })
            {
                var inner = InvokeNoArgs(source, name);
                if (inner is IEnumerable) return ExtractCards(inner);
            }
            foreach (var field in source.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (!field.Name.ToLowerInvariant().Contains("card")) continue;
                try { var value = field.GetValue(source); if (value is IEnumerable) return ExtractCards(value); }
                catch { }
            }
            return null;
        }

        private static string DescribePileMethods(object cards)
        {
            return String.Join(", ", cards.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name.IndexOf("Pile", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            m.Name.IndexOf("Deck", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            m.Name.IndexOf("Discard", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(m => m.Name + "(" + String.Join(",", m.GetParameters().Select(p => p.ParameterType.Name).ToArray()) + ")")
                .Distinct().Take(30).ToArray());
        }

        private static object InvokeNoArgs(object source, string methodName)
        {
            if (source == null) return null;
            try
            {
                var method = source.GetType().GetMethod(methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                return method == null ? null : method.Invoke(source, null);
            }
            catch { return null; }
        }

        private static CharacterState FindDeployedUnit(int slot, CardState owner)
        {
            var all = AllGameManagers.Instance;
            var roomManager = all == null ? null : all.GetRoomManager();
            if (roomManager == null) return null;
            for (int roomIndex = 0; roomIndex < 3; ++roomIndex)
            {
                var room = roomManager.GetRoom(roomIndex);
                if (room == null) continue;
                var units = new List<CharacterState>();
                room.AddCharactersToList(units, Team.Type.Monsters);
                foreach (var unit in units)
                {
                    if (unit == null || !unit.IsAlive) continue;
                    var sourceCard = unit.GetSpawnerCard();
                    if (IsSameRosterCopy(sourceCard, owner, slot)) return unit;
                }
            }
            return null;
        }

        // Invoke the same native return-to-hand operation the base game's
        // CardEffectReturnUnitToHand uses. Do not manually Destroy a unit:
        // that loses state and can trigger unintended death effects.
        private static void RecallUnit(int slot, CharacterState unit)
        {
            var all = AllGameManagers.Instance;
            var combat = all == null ? null : all.GetCombatManager();
            if (combat == null || unit == null || !unit.IsAlive) return;
            if (rosterDead[slot] || actionPending[slot]) return;
            actionPending[slot] = true;
            try
            {
                // If this build exposes a direct recall API, use it.
                foreach (object target in new object[] { combat, all.GetRoomManager(), unit })
                {
                    if (target == null) continue;
                    foreach (var method in target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (method.Name != "ReturnUnitToHand" && method.Name != "ReturnCharacterToHand" &&
                            method.Name != "RecallUnit" && method.Name != "ReturnToHand") continue;
                        var args = method.GetParameters();
                        if (target != unit && (args.Length < 1 || !args[0].ParameterType.IsAssignableFrom(typeof(CharacterState)))) continue;
                        if (target == unit && args.Length > 2) continue;
                        if (target != unit && args.Length > 3) continue;
                        object[] values = new object[args.Length];
                        for (int i = 0; i < args.Length; i++)
                        {
                            if (target != unit && i == 0) values[i] = unit;
                            else if (args[i].ParameterType == typeof(bool)) values[i] = false;
                            else if (args[i].HasDefaultValue) values[i] = args[i].DefaultValue;
                            else if (!args[i].ParameterType.IsValueType) values[i] = null;
                            else values[i] = Activator.CreateInstance(args[i].ParameterType);
                        }
                        var returned = method.Invoke(target, values);
                        if (returned is IEnumerator coroutine)
                            combat.StartCoroutine(FinishRecall(slot, coroutine));
                        else
                        {
                            actionPending[slot] = false;
                            Info("Roster native recall requested: slot " + (slot + 1));
                        }
                        SoundManager.PlaySfxSignal?.Dispatch("UI_Hover");
                        return;
                    }
                }
                // The base game also exposes recall as CardEffectReturnUnitToHand.
                // Run it through the same CardEffectState / CardEffectParams
                // execution path used by other HexTrolls effects.
                if (TryNativeReturnCardEffect(slot, unit, combat)) return;
                actionPending[slot] = false;
                Warn("Native return-to-hand API not found for slot " + (slot + 1) +
                     ". Keeping the live unit intact. Relevant methods: " +
                     String.Join(", ", combat.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         .Where(m => m.Name.IndexOf("Return", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     m.Name.IndexOf("Recall", StringComparison.OrdinalIgnoreCase) >= 0)
                         .Select(m => m.Name).Distinct().ToArray()));
                InvalidRosterClick();
            }
            catch (Exception ex)
            {
                actionPending[slot] = false;
                Warn("Native roster recall failed, unit left in place: " + ex);
                InvalidRosterClick();
            }
        }

        private static bool TryNativeReturnCardEffect(int slot, CharacterState unit,
            CombatManager combat)
        {
            try
            {
                var game = AllGameManagers.Instance;
                var effectData = Activator.CreateInstance(typeof(CardEffectData), true) as CardEffectData;
                if (effectData == null) return false;
                var type = typeof(CardEffectData);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                bool configured = false;
                foreach (var field in type.GetFields(flags))
                {
                    if (field.FieldType != typeof(string) ||
                        field.Name.Replace("_", "").IndexOf("effectstatename", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    field.SetValue(effectData, "CardEffectReturnUnitToHand");
                    configured = true;
                    break;
                }
                if (!configured)
                {
                    foreach (var property in type.GetProperties(flags))
                    {
                        if (!property.CanWrite || property.PropertyType != typeof(string) ||
                            property.Name.Replace("_", "").IndexOf("effectstatename", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        property.SetValue(effectData, "CardEffectReturnUnitToHand", null);
                        configured = true;
                        break;
                    }
                }
                if (!configured)
                {
                    Warn("CardEffectData's effect state member was not found; cannot recall safely.");
                    return false;
                }
                var state = new CardEffectState();
                state.Setup(effectData, null);
                var effect = state.GetCardEffect();
                if (effect == null) return false;
                int room = -1;
                var readRoom = InvokeNoArgs(unit, "GetCurrentRoomIndex");
                if (readRoom is int index) room = index;
                var parameters = new CardEffectParams
                {
                    selectedRoom = room,
                    targets = new List<CharacterState> { unit }
                };
                var core = game.GetCoreManagers();
                var systems = game.GetSystemManagers();
                if (!effect.TestEffect(state, parameters, core))
                {
                    Warn("CardEffectReturnUnitToHand.TestEffect rejected slot " + (slot + 1));
                    return false;
                }
                var task = effect.ApplyEffect(state, parameters, core, systems);
                combat.StartCoroutine(FinishRecall(slot, task));
                Info("Started built-in CardEffectReturnUnitToHand on roster slot " + (slot + 1));
                SoundManager.PlaySfxSignal?.Dispatch("UI_Hover");
                return true;
            }
            catch (Exception ex)
            {
                Warn("Built-in return effect unavailable for this game build: " + ex.Message);
                return false;
            }
        }

        private static IEnumerator FinishRecall(int slot, IEnumerator coroutine)
        {
            // The game handles removing the unit and restoring its own CardState.
            yield return coroutine;
            actionPending[slot] = false;
            RefreshLabels();
            Info("Roster native recall completed: slot " + (slot + 1));
        }

        private static void InstallDeathHook()
        {
            if (deathHookInstalled) return;
            var death = AccessTools.Method(typeof(CharacterState), "InnerCharacterDeath");
            if (death == null) { Warn("CharacterState.InnerCharacterDeath not found; death blackout unavailable."); return; }
            try
            {
                var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID + ".TrainersRosterDeath");
                harmony.Patch(death, prefix: new HarmonyMethod(typeof(TrainersNativeRosterController)
                    .GetMethod(nameof(OnRosterUnitDeath), BindingFlags.NonPublic | BindingFlags.Static)));
                deathHookInstalled = true;
                Info("Roster death tracking patched to CharacterState.InnerCharacterDeath");
            }
            catch (Exception ex) { Warn("Roster death hook could not install: " + ex.Message); }
        }

        private static void OnRosterUnitDeath(CharacterState __instance, bool __0)
        {
            if (!__0 || __instance == null || !combatWasRunning) return;
            try
            {
                var save = AllGameManagers.Instance == null ? null : AllGameManagers.Instance.GetSaveManager();
                if (save == null || save.PreviewMode) return;
                var source = __instance.GetSpawnerCard();
                if (source == null) return;
                int slot = SlotOf(source);
                if (slot < 0 || actionPending[slot]) return;
                rosterDead[slot] = true;
                Info("PocketMon lost during battle: slot " + (slot + 1));
                RefreshLabels();
            }
            catch (Exception ex) { Warn("Roster death tracking: " + ex.Message); }
        }

        public static bool CanEdit()
        {
            if (!ready) return false;
            var all = AllGameManagers.Instance;
            if (all == null) return false;
            var save = all.GetSaveManager();
            if (save == null || save.PreviewMode || save.GetDeckState() == null)
                return false;
            var main = save.GetMainClass();
            var ally = save.GetSubClass();
            string mainId = main == null ? null : main.GetID();
            string allyId = ally == null ? null : ally.GetID();
            if (!String.Equals(trainersRuntimeGuid, mainId, StringComparison.OrdinalIgnoreCase)
                && !String.Equals(trainersRuntimeGuid, allyId, StringComparison.OrdinalIgnoreCase))
                return false;
            var combat = all.GetCombatManager();
            if (combat != null && combat.GetIsRunningCombat()) return false;
            return true;
        }

        private static string EditBlockReason()
        {
            if (!ready) return "slot definitions are not initialized";
            var all = AllGameManagers.Instance;
            if (all == null) return "AllGameManagers is null";
            var save = all.GetSaveManager();
            if (save == null) return "SaveManager is null";
            if (save.PreviewMode) return "preview mode";
            if (save.GetDeckState() == null) return "run deck is unavailable";
            var mainId = save.GetMainClass()?.GetID();
            var allyId = save.GetSubClass()?.GetID();
            if (!String.Equals(trainersRuntimeGuid, mainId, StringComparison.OrdinalIgnoreCase)
                && !String.Equals(trainersRuntimeGuid, allyId, StringComparison.OrdinalIgnoreCase))
                return "not a Trainers run (main=" + mainId + ", ally=" + allyId + ")";
            var combat = all.GetCombatManager();
            if (combat != null && combat.GetIsRunningCombat()) return "combat is active";
            return "unknown restriction";
        }

        // Keep the informational, noncombat STATUS on the live deployed unit.
        // Assigned cards also display a permanent Roster Slot badge before play.
        // This is deliberately keyed by GetSpawnerCard() + the permanent slot
        // marker; identical species / card-data IDs cannot steal a slot.
        // It is re-applied after a recall/redeploy, and never touches Level/EXP.
        private static void SyncDeployedRosterStatus()
        {
            if (!combatWasRunning || !ready) return;
            try
            {
                var game = AllGameManagers.Instance;
                var rooms = game == null ? null : game.GetRoomManager();
                if (rooms == null) return;
                for (int floor = 0; floor < 3; ++floor)
                {
                    var room = rooms.GetRoom(floor);
                    if (room == null) continue;
                    var units = new List<CharacterState>();
                    room.AddCharactersToList(units, Team.Type.Monsters);
                    foreach (var unit in units)
                    {
                        if (unit == null || !unit.IsAlive || unit.PreviewMode) continue;
                        var card = unit.GetSpawnerCard();
                        if (card == null) continue;
                        int slot = SlotOf(card);
                        if (slot < 0) continue;
                        int desired = slot + 1;
                        int current = unit.GetStatusEffectStacks(RosterSlotStatusId);
                        if (current == desired) continue;
                        if (current > 0)
                            unit.RemoveStatusEffect(RosterSlotStatusId, current, false);
                        unit.AddStatusEffect(RosterSlotStatusId, desired,
                            (CharacterState)null, false, false);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!rosterStatusWarningLogged)
                {
                    rosterStatusWarningLogged = true;
                    Warn("Could not apply Roster Slot status. Confirm TrainersRosterSlot.json " +
                         "is in json/status_effects and StatusEffectTrainersRosterSlotState is compiled: " + ex);
                }
            }
        }

        // A CharacterState status doesn't exist until the unit is spawned.
        // Show the permanent card's roster assignment BEFORE it is played as
        // a badge on every CardUI rendering (run deck, hand, picker, previews).
        // The deployed unit continues to receive the actual passive status.
        // This uses the saved CardState marker and does NOT change shared
        // CharacterData (so identical PocketMon copies stay independent).
        private static void InstallCardBadgeHook()
        {
            if (cardBadgeHookInstalled) return;
            try
            {
                var methods = typeof(CardUI).GetMethods(BindingFlags.Instance |
                    BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => m.Name == "ApplyStateToUI" && !m.IsGenericMethod &&
                        m.GetParameters().Length > 0 &&
                        m.GetParameters()[0].ParameterType == typeof(CardState))
                    .ToArray();
                if (methods.Length == 0)
                {
                    Warn("CardUI.ApplyStateToUI(CardState, ...) not found; " +
                         "pre-play Roster Slot badges unavailable.");
                    return;
                }
                var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID + ".TrainersRosterCardBadge");
                var postfix = new HarmonyMethod(typeof(TrainersNativeRosterController)
                    .GetMethod(nameof(OnCardUIRendered), BindingFlags.NonPublic | BindingFlags.Static));
                foreach (var method in methods)
                    harmony.Patch(method, postfix: postfix);
                cardBadgeHookInstalled = true;
                Info("Permanent Roster Slot card badges installed (" + methods.Length +
                     " CardUI ApplyStateToUI overloads).");
            }
            catch (Exception ex)
            {
                Warn("Unable to install always-visible Roster Slot card badges: " + ex);
            }
        }

        private static void OnCardUIRendered(CardUI __instance, CardState __0)
        {
            if (__instance == null || !ready) return;
            try
            {
                int slot = SlotOf(__0);
                var transform = __instance.transform;
                var badge = transform.Find(CardBadgeObjectName);
                if (slot < 0)
                {
                    if (badge != null) badge.gameObject.SetActive(false);
                    return;
                }
                if (badge == null)
                {
                    var obj = new GameObject(CardBadgeObjectName, typeof(RectTransform), typeof(Image));
                    obj.transform.SetParent(transform, false);
                    var rect = obj.GetComponent<RectTransform>();
                    rect.anchorMin = rect.anchorMax = new Vector2(1f, 1f);
                    rect.pivot = new Vector2(1f, 1f);
                    rect.sizeDelta = new Vector2(156f, 28f);
                    rect.anchoredPosition = new Vector2(-9f, -8f);
                    var background = obj.GetComponent<Image>();
                    background.color = new Color(.12f, .09f, .22f, .94f);
                    background.raycastTarget = false;
                    var textObj = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
                    textObj.transform.SetParent(obj.transform, false);
                    var textRect = textObj.GetComponent<RectTransform>();
                    textRect.anchorMin = Vector2.zero;
                    textRect.anchorMax = Vector2.one;
                    textRect.offsetMin = Vector2.zero;
                    textRect.offsetMax = Vector2.zero;
                    var text = textObj.GetComponent<TextMeshProUGUI>();
                    text.fontSize = 18f;
                    text.fontStyle = FontStyles.Bold;
                    text.alignment = TextAlignmentOptions.Center;
                    text.color = Color.white;
                    text.raycastTarget = false;
                    badge = obj.transform;
                }
                badge.gameObject.SetActive(true);
                badge.SetAsLastSibling();
                var label = badge.Find("Label");
                if (label != null)
                {
                    var text = label.GetComponent<TextMeshProUGUI>();
                    if (text != null) text.text = "ROSTER SLOT " + (slot + 1);
                }
            }
            catch (Exception ex)
            {
                Warn("Could not render Roster Slot card badge: " + ex.Message);
            }
        }

        public static void RefreshLabels()
        {
            var all = AllGameManagers.Instance;
            var save = all == null ? null : all.GetSaveManager();
            for (int i = 0; i < SlotCount; ++i)
            {
                var owner = save == null ? null : FindOwner(save, i);
                if (labels[i] != null)
                {
                    labels[i].text = (i + 1) + "\n" +
                        (owner == null ? "+" :
                         (combatWasRunning && rosterDead[i] ? "DEAD" : CompactName(owner)));
                }
                UpdateMiniCard(i, owner, save);
            }
        }

        private static bool combatSpriteWarningLogged;

        // Trainworks stores combat art separately from card art in
        // CharacterData.characterPrefabVariantRef (AssetReferenceGameObject).
        // Resolve the registered prefab and take its SpriteRenderer sprite.
        // This never instantiates or removes a battlefield character.
        private static Sprite FindCombatCharacterSprite(CardState card)
        {
            try
            {
                if (card == null) return null;
                var character = card.GetSpawnCharacterData();
                if (character == null) return null;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var artField = AccessTools.Field(typeof(CharacterData), "characterPrefabVariantRef");
                object reference = artField == null ? null : artField.GetValue(character);
                if (reference == null)
                {
                    foreach (var field in typeof(CharacterData).GetFields(flags))
                    {
                        if (field.Name.IndexOf("art", StringComparison.OrdinalIgnoreCase) < 0 &&
                            field.Name.IndexOf("prefab", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (field.FieldType == typeof(GameObject) ||
                            field.FieldType.Name.IndexOf("AssetReference", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            reference = field.GetValue(character);
                            if (reference != null) break;
                        }
                    }
                }
                if (reference is GameObject direct)
                    return SpriteOnCharacterPrefab(direct);
                if (reference == null) return null;
                // Some builds expose the loaded prefab directly.
                foreach (string propName in new[] { "Asset", "m_Asset", "GameObject", "Prefab" })
                {
                    var p = reference.GetType().GetProperty(propName, flags);
                    if (p == null || p.GetIndexParameters().Length != 0) continue;
                    try
                    {
                        if (p.GetValue(reference, null) is GameObject asset)
                        {
                            var sprite = SpriteOnCharacterPrefab(asset);
                            if (sprite != null) return sprite;
                        }
                    }
                    catch { }
                }
                // The mod's GameObject register supplies the custom character
                // art referenced by the runtime Addressables key.
                object keyObject = null;
                var runtimeKey = reference.GetType().GetProperty("RuntimeKey", flags);
                if (runtimeKey != null)
                {
                    try { keyObject = runtimeKey.GetValue(reference, null); }
                    catch { }
                }
                string key = keyObject == null ? null : keyObject.ToString();
                if (String.IsNullOrEmpty(key))
                {
                    var guid = reference.GetType().GetProperty("AssetGUID", flags);
                    if (guid != null)
                    {
                        try { key = guid.GetValue(reference, null) as string; }
                        catch { }
                    }
                }
                if (String.IsNullOrEmpty(key)) return null;
                var container = Railend.GetContainer();
                if (container == null) return null;
                var register = container.GetInstance<IRegister<GameObject>>();
                if (register == null) return null;
                var candidates = new[] { key,
                    MyPluginInfo.PLUGIN_GUID + "-GameObject-" + key,
                    key.StartsWith("@") ? key.Substring(1) : key };
                foreach (string candidate in candidates.Distinct())
                {
                    GameObject prefab;
                    if (!register.TryGetValue(candidate, out prefab) || prefab == null) continue;
                    var sprite = SpriteOnCharacterPrefab(prefab);
                    if (sprite != null) return sprite;
                }
            }
            catch (Exception ex)
            {
                if (!combatSpriteWarningLogged)
                {
                    combatSpriteWarningLogged = true;
                    Warn("Character-art sprite lookup failed: " + ex.Message);
                }
            }
            return null;
        }

        private static Sprite SpriteOnCharacterPrefab(GameObject prefab)
        {
            if (prefab == null) return null;
            foreach (var renderer in prefab.GetComponentsInChildren<SpriteRenderer>(true))
                if (renderer != null && renderer.sprite != null) return renderer.sprite;
            return null;
        }

        // A noninteractive combat-character sprite is mounted inside each
        // Conductor HUD. The parent click handler remains unchanged.
        private static void UpdateMiniCard(int slot, CardState owner, SaveManager save)
        {
            var hud = hudObjects[slot];
            if (hud == null) return;
            if (miniCardOwners[slot] != owner || miniCardHudOwners[slot] != hud)
            {
                var old = hud.transform.Find("PocketMonMiniCard");
                if (old != null) UnityEngine.Object.Destroy(old.gameObject);
                miniCardOwners[slot] = owner;
                miniCardHudOwners[slot] = hud;
                miniCardBlackout[slot] = null;
                if (owner != null && save != null)
                {
                    try
                    {
                        var root = new GameObject("PocketMonMiniCard", typeof(RectTransform));
                        root.transform.SetParent(hud.transform, false);
                        var rect = root.GetComponent<RectTransform>();
                        rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
                        rect.pivot = new Vector2(.5f, .5f);
                        rect.sizeDelta = new Vector2(61f, 55f);
                        rect.anchoredPosition = new Vector2(0f, 2f);
                        // This is the COMBAT character sprite, not the deck
                        // CardUI, so no card frame, cost, stats or card text.
                        var sprite = FindCombatCharacterSprite(owner);
                        if (sprite != null)
                        {
                            var picture = new GameObject("PocketMonCombatSprite",
                                typeof(RectTransform), typeof(Image));
                            picture.transform.SetParent(rect, false);

                            var pr = picture.GetComponent<RectTransform>();
                            pr.anchorMin = pr.anchorMax = new Vector2(.5f, .5f);
                            pr.pivot = new Vector2(.5f, .5f);

                            // Make the art bigger than the button so it can overflow visually.
                            // 2x is what you asked for earlier; bump this if you want even more.
                            pr.sizeDelta = new Vector2(rect.sizeDelta.x * 2f, rect.sizeDelta.y * 2f);
                            pr.anchoredPosition = new Vector2(0f, 2f);

                            var img = picture.GetComponent<Image>();
                            img.sprite = sprite;
                            img.preserveAspect = true;
                            img.color = Color.white;
                            img.raycastTarget = false;
                        }
                        else
                        {
                            // Never display a whole card as a substitute: it
                            // was the visual problem this patch fixes.
                            var fallback = root.AddComponent<Image>();
                            fallback.color = new Color(.25f, .2f, .3f, .9f);
                            fallback.raycastTarget = false;
                            if (!combatSpriteWarningLogged)
                            {
                                combatSpriteWarningLogged = true;
                                Warn("Combat sprite not yet found in the character-art prefab. " +
                                     "Roster slot still works; check its character_art registration.");
                            }
                        }
                        // Blackout stays over the card, but below the slot label.
                        var shade = new GameObject("PocketMonDeadOverlay", typeof(RectTransform), typeof(Image));
                        shade.transform.SetParent(rect, false);
                        var sr = shade.GetComponent<RectTransform>();
                        sr.anchorMin = Vector2.zero;
                        sr.anchorMax = Vector2.one;
                        sr.offsetMin = Vector2.zero;
                        sr.offsetMax = Vector2.zero;
                        var dark = shade.GetComponent<Image>();
                        dark.raycastTarget = false;
                        miniCardBlackout[slot] = dark;
                        if (labels[slot] != null) labels[slot].transform.SetAsLastSibling();
                    }
                    catch (Exception ex) { Warn("Miniature card rendering failed: " + ex.Message); }
                }
            }
            if (miniCardBlackout[slot] != null)
            {
                miniCardBlackout[slot].color = combatWasRunning && rosterDead[slot]
                    ? new Color(0f, 0f, 0f, .93f) : new Color(0f, 0f, 0f, 0f);
            }
        }

        public static void OpenPicker(int slot, GameObject clickedHud)
        {
            if (slot < 0 || slot >= SlotCount) return;
            if (!CanEdit())
            {
                Warn("Slot " + (slot + 1) + " clicked but editing is unavailable. " + EditBlockReason());
                SoundManager.PlaySfxSignal?.Dispatch("UI_PlayInvalidCard");
                return;
            }
            ClosePicker();
            var canvas = clickedHud.GetComponentInParent<Canvas>();
            if (canvas != null) canvas = canvas.rootCanvas;
            if (canvas == null)
            {
                Warn("Unable to find active Canvas for the Trainers picker.");
                return;
            }
            selectedSlot = slot;
            page = 0;
            picker = new GameObject("TrainersRosterNativePicker", typeof(RectTransform), typeof(Image));
            picker.transform.SetParent(canvas.transform, false);
            var background = picker.GetComponent<Image>();
            background.color = new Color(0f, 0f, 0f, 0.66f);
            background.raycastTarget = true;
            var fullscreen = picker.GetComponent<RectTransform>();
            fullscreen.anchorMin = Vector2.zero;
            fullscreen.anchorMax = Vector2.one;
            fullscreen.offsetMin = Vector2.zero;
            fullscreen.offsetMax = Vector2.zero;
            picker.transform.SetAsLastSibling();
            picker.AddComponent<TrainersRosterPickerGuard>();
            BuildPicker();
            Info("Opened unit picker for slot " + (slot + 1));
        }

        public static void ClosePicker()
        {
            if (picker != null) UnityEngine.Object.Destroy(picker);
            picker = null;
            selectedSlot = -1;
        }

        private static RectTransform AddRect(Transform parent, string name, Vector2 size,
            Vector2 position, Vector2 anchor, Vector2 pivot)
        {
            var obj = new GameObject(name, typeof(RectTransform));
            obj.transform.SetParent(parent, false);
            var rect = obj.GetComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            return rect;
        }

        private static TextMeshProUGUI AddText(Transform parent, string value,
            Vector2 size, Vector2 position, float font, TextAlignmentOptions alignment)
        {
            var rect = AddRect(parent, "Text", size, position, new Vector2(0f, 1f), new Vector2(0f, 1f));
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.text = value;
            text.fontSize = font;
            text.alignment = alignment;
            text.enableWordWrapping = true;
            text.raycastTarget = false;
            text.color = Color.white;
            return text;
        }

        private static void AddButton(Transform parent, string label, Vector2 size,
            Vector2 position, Action click, Color color)
        {
            var rect = AddRect(parent, "Button_" + label, size, position,
                new Vector2(0f, 1f), new Vector2(0f, 1f));
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = true;
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var textRect = AddRect(rect, "Label", size, Vector2.zero,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            var text = textRect.gameObject.AddComponent<TextMeshProUGUI>();
            text.text = label;
            text.fontSize = 17f;
            text.alignment = TextAlignmentOptions.Center;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.color = Color.white;
            text.raycastTarget = false;
            button.onClick.AddListener(() => click());
        }

        // Use the real DeckScreen's card prefab, so every selection tile has the
        // same card face, art, stats and upgrades that the game shows in Deck.
        // The grid is deliberately its own modal: invoking vanilla DeckScreen's
        // upgrade/purge modes would change the deck or discard our slot callback.
        private static CardUI FindVanillaDeckCardPrefab()
        {
            if (canonicalDeckCardPrefab != null) return canonicalDeckCardPrefab;
            try
            {
                var all = AllGameManagers.Instance;
                var screens = all == null ? null : all.GetScreenManager();
                if (screens == null) return null;
                var screen = screens.GetScreen((ScreenName)3); // Deck
                if (screen == null) return null;
                var field = AccessTools.Field(screen.GetType(), "cardPrefab")
                            ?? AccessTools.Field(typeof(DeckScreen), "cardPrefab");
                var prefab = field == null ? null : field.GetValue(screen) as CardUI;
                if (prefab != null) { canonicalDeckCardPrefab = prefab; return prefab; }
            }
            catch (Exception e) { Warn("DeckScreen card prefab lookup: " + e.Message); }
            // Alternative used by working MT2 card-display mods: prefer a
            // prefab that is not part of an active scene.
            try
            {
                CardUI any = null;
                foreach (var candidate in Resources.FindObjectsOfTypeAll<CardUI>())
                {
                    if (candidate == null) continue;
                    // Never clone our own miniature or picker cards as a prefab.
                    if (candidate.GetComponentInParent<TrainersRosterSlotHud>() != null) continue;
                    bool inPicker = false;
                    for (Transform parent = candidate.transform; parent != null; parent = parent.parent)
                        if (parent.name == "TrainersRosterNativePicker") { inPicker = true; break; }
                    if (inPicker) continue;
                    if (!candidate.gameObject.scene.IsValid())
                    {
                        canonicalDeckCardPrefab = candidate;
                        return candidate;
                    }
                    if (any == null) any = candidate;
                }
                canonicalDeckCardPrefab = any;
                return any;
            }
            catch (Exception e) { Warn("Fallback card prefab lookup: " + e.Message); }
            return null;
        }

        // CardUIState is internal/not exposed by some MT2 reference sets.
        // Resolve the enum from CardUI.SetUIState at runtime so the project
        // compiles without directly naming the missing enum type.
        private static void SetPickerCardUiState(CardUI cardUI)
        {
            var setter = typeof(CardUI)
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(method => method.Name == "SetUIState" &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType.IsEnum);
            if (setter == null) return;
            var stateType = setter.GetParameters()[0].ParameterType;
            setter.Invoke(cardUI, new object[] { Enum.ToObject(stateType, 3) });
        }

        private static void BuildPicker()
        {
            if (picker == null || selectedSlot < 0) return;
            var oldPanels = new List<GameObject>();
            foreach (Transform t in picker.transform)
                if (t.name == "PickerPanel") oldPanels.Add(t.gameObject);
            foreach (var panel in oldPanels) UnityEngine.Object.Destroy(panel);

            var save = AllGameManagers.Instance == null ? null : AllGameManagers.Instance.GetSaveManager();
            if (save == null || save.GetDeckState() == null) return;
            // Keep every individual saved CardState, even when the displayed
            // name and CardDataID are identical. Do not Distinct() by card ID.
            var choices = save.GetDeckState().Where(Eligible).ToList();
            // If the deck actually reuses a CardState reference, the roster
            // cannot treat those entries as different upgrade hosts. Log it.
            for (int i = 0; i < choices.Count; ++i)
            {
                if (choices.Take(i).Any(c => object.ReferenceEquals(c, choices[i])))
                {
                    Warn("Deck contains repeated references to the same PocketMon CardState. " +
                         "Only truly separate CardState instances can hold separate roster markers.");
                    break;
                }
            }
            var prefab = FindVanillaDeckCardPrefab();
            if (prefab == null)
            {
                Warn("Vanilla DeckScreen prefab unavailable; showing safe roster text picker.");
                BuildTextPicker(save, choices);
                return;
            }

            // Full-size deck-style panel. No changes to the six Conductor icons.
            var panelRect = AddRect(picker.transform, "PickerPanel",
                new Vector2(1220f, 850f), Vector2.zero,
                new Vector2(.5f, .5f), new Vector2(.5f, .5f));
            var bg = panelRect.gameObject.AddComponent<Image>();
            bg.color = new Color(.075f, .07f, .12f, .99f);
            bg.raycastTarget = true;
            var border = panelRect.gameObject.AddComponent<Outline>();
            border.effectColor = new Color(.9f, .68f, .25f, 1f);
            border.effectDistance = new Vector2(2f,-2f);

            AddText(panelRect, "TRAINERS  —  ASSIGN ROSTER SLOT " + (selectedSlot + 1),
                new Vector2(970f, 45f), new Vector2(35f,-19f), 30f,
                TextAlignmentOptions.TopLeft);
            AddText(panelRect, "YOUR DECK  •  POCKETMON ONLY  •  " + choices.Count +
                    " COPIES  •  ONE UNIT PER SLOT  •  CLICK TO REASSIGN",
                new Vector2(965f, 32f), new Vector2(35f,-68f), 17f,
                TextAlignmentOptions.TopLeft);
            AddButton(panelRect, "X", new Vector2(45f,42f), new Vector2(1132f,-19f),
                ClosePicker, new Color(.51f,.16f,.19f,1f));
            AddButton(panelRect, "Clear slot", new Vector2(160f,40f),
                new Vector2(985f,-68f), () => {
                    if (TryClear(save, selectedSlot)) { RefreshLabels(); ClosePicker(); }
                }, new Color(.42f,.24f,.18f,1f));

            var viewport = AddRect(panelRect, "DeckViewport", new Vector2(1150f,685f),
                new Vector2(0f,-38f), new Vector2(.5f,.5f), new Vector2(.5f,.5f));
            viewport.gameObject.AddComponent<RectMask2D>();
            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 33f;
            scroll.viewport = viewport;

            const float tileWidth=265f, tileHeight=336f, gapX=18f, gapY=24f;
            const int columns=4;
            int rows=(choices.Count+columns-1)/columns;
            var content = AddRect(viewport,"DeckCards",
                new Vector2(1134f,Mathf.Max(685f,rows*(tileHeight+gapY)+20f)),
                Vector2.zero,new Vector2(.5f,1f),new Vector2(.5f,1f));
            scroll.content=content;
            var grid = content.gameObject.AddComponent<GridLayoutGroup>();
            grid.constraint=GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount=columns;
            grid.cellSize=new Vector2(tileWidth,tileHeight);
            grid.spacing=new Vector2(gapX,gapY);
            grid.padding=new RectOffset(10,10,12,12);
            grid.childAlignment=TextAnchor.UpperCenter;

            // Soul-Savior-style attachment: a separate selectable tile for
            // every saved card copy, even if several copies have identical art.
            var copyTotals = choices.GroupBy(c => c.GetCardDataID(),
                StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var copiesSeen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int rendered=0;
            foreach (var card in choices)
            {
                string cardDataId = card.GetCardDataID();
                int copyIndex;
                copiesSeen.TryGetValue(cardDataId, out copyIndex);
                ++copyIndex;
                copiesSeen[cardDataId] = copyIndex;
                int copyTotal = copyTotals[cardDataId];
                var tile = new GameObject("DeckCard_" + rendered + "_" + cardDataId,
                    typeof(RectTransform),typeof(Image));
                tile.transform.SetParent(content,false);
                var tileRect = tile.GetComponent<RectTransform>();
                tileRect.sizeDelta=new Vector2(tileWidth,tileHeight);
                var tileBackground=tile.GetComponent<Image>();
                tileBackground.color=new Color(.16f,.14f,.22f,.97f);
                tileBackground.raycastTarget=true;

                try
                {
                    var cardUI=UnityEngine.Object.Instantiate(prefab,tileRect,false);
                    cardUI.gameObject.name="TrainersVanillaDeckCard";
                    cardUI.gameObject.SetActive(true);
                    var rect=cardUI.transform as RectTransform;
                    rect.anchorMin=rect.anchorMax=new Vector2(.5f,.5f);
                    rect.pivot=new Vector2(.5f,.5f);
                    rect.anchoredPosition=new Vector2(0f,3f);
                    // Scale the entire actual card, not only its layout rectangle.
                    float w=Mathf.Max(1f,Mathf.Abs(rect.rect.width));
                    float h=Mathf.Max(1f,Mathf.Abs(rect.rect.height));
                    if (w <= 1f) w=Mathf.Max(1f,Mathf.Abs(rect.sizeDelta.x));
                    if (h <= 1f) h=Mathf.Max(1f,Mathf.Abs(rect.sizeDelta.y));
                    float scale=Mathf.Min(252f/w,314f/h);
                    if (scale<=0f || scale>2f) scale=.82f;
                    rect.localScale=new Vector3(scale,scale,1f);

                    cardUI.SetCardVisibleAndInteractable(true,null);
                    SetPickerCardUiState(cardUI);
                    cardUI.ApplyStateToUI(card,null,null,null,null,save,
                        save.GetMastery(card),true,false,null,null,null);
                    foreach (var graphic in cardUI.GetComponentsInChildren<Graphic>(true))
                        graphic.raycastTarget=false;
                    foreach (var cg in cardUI.GetComponentsInChildren<CanvasGroup>(true))
                        cg.blocksRaycasts=false;
                }
                catch (Exception ex)
                {
                    Warn("Card UI render for " + DisplayName(card) + ": " + ex.Message);
                    // Still selectable if a specific card art prefab is missing.
                    AddText(tileRect,DisplayName(card),new Vector2(248f,110f),
                        new Vector2(9f,-95f),22f,TextAlignmentOptions.TopLeft);
                }

                // Explicit per-copy badge prevents identical PocketMon from
                // looking like the very same clickable card.
                if (copyTotal > 1)
                {
                    var copyBadge = AddRect(tileRect, "CopyBadge", new Vector2(120f, 26f),
                        new Vector2(8f,-8f), new Vector2(0f,1f), new Vector2(0f,1f));
                    var copyBackground = copyBadge.gameObject.AddComponent<Image>();
                    copyBackground.color = new Color(.09f,.09f,.16f,.94f);
                    copyBackground.raycastTarget = false;
                    // A Unity UI object can contain only one Graphic component.
                    // Put the text on a CHILD rather than beside the Image.
                    var copyLabelRect = AddRect(copyBadge, "CopyBadgeLabel",
                        new Vector2(120f, 26f), Vector2.zero,
                        new Vector2(.5f, .5f), new Vector2(.5f, .5f));
                    var copyText = copyLabelRect.gameObject.AddComponent<TextMeshProUGUI>();
                    copyText.text = "COPY " + copyIndex + " / " + copyTotal;
                    copyText.fontSize = 15f;
                    copyText.fontStyle = FontStyles.Bold;
                    copyText.alignment = TextAlignmentOptions.Center;
                    copyText.color = Color.white;
                    copyText.raycastTarget = false;
                }

                int assigned=SlotOf(card);
                if (assigned>=0)
                {
                    var indicator=AddRect(tileRect,"AssignedMarker",new Vector2(240f,30f),
                        new Vector2(0f,-2f),new Vector2(.5f,1f),new Vector2(.5f,1f));
                    var band=indicator.gameObject.AddComponent<Image>();
                    band.color=assigned==selectedSlot ? new Color(.23f,.45f,.22f,.94f)
                                                     : new Color(.48f,.27f,.1f,.94f);
                    band.raycastTarget=false;
                    // The selection band already has an Image; its text must
                    // live on a separate child to avoid a missing TMP component.
                    var indicatorLabelRect = AddRect(indicator, "AssignedMarkerLabel",
                        new Vector2(240f, 30f), Vector2.zero,
                        new Vector2(.5f, .5f), new Vector2(.5f, .5f));
                    var txt=indicatorLabelRect.gameObject.AddComponent<TextMeshProUGUI>();
                    txt.text=assigned == selectedSlot
                        ? "SLOT " + (assigned + 1) + " - SELECTED"
                        : "SLOT " + (assigned + 1) + " - CLICK TO MOVE";
                    txt.fontSize=14f;
                    txt.fontStyle=FontStyles.Bold;
                    txt.alignment=TextAlignmentOptions.Center;
                    txt.color=Color.white;
                    txt.raycastTarget=false;
                }

                // Parent Button receives all pointer hits. Clone card graphics
                // must not eat the click (they have raycastTarget=false above).
                var button=tile.AddComponent<Button>();
                button.targetGraphic=tileBackground;
                CardState chosen=card; // exact permanent deck copy, not CardData
                button.onClick.AddListener(() => {
                    if (TryAssign(save,selectedSlot,chosen)) { RefreshLabels(); ClosePicker(); }
                });
                rendered++;
            }
            scroll.verticalNormalizedPosition=1f;
            Info("Native card-face roster picker rendered " + rendered + " PocketMon cards.");
            if (choices.Count==0)
                AddText(panelRect,"No PocketMon unit cards in your deck.",
                    new Vector2(780f,100f),new Vector2(200f,-320f),26f,
                    TextAlignmentOptions.TopLeft);
        }

        // Fallback only if game/DeckScreen is not yet initialized.
        // It retains the previously proven assignment flow.
        private static void BuildTextPicker(SaveManager save,List<CardState> choices)
        {
            int maxPage=choices.Count==0?0:(choices.Count-1)/RowsPerPage;
            page=Mathf.Clamp(page,0,maxPage);
            var panel=AddRect(picker.transform,"PickerPanel",new Vector2(510f,550f),
                new Vector2(-34f,-115f),new Vector2(1f,1f),new Vector2(1f,1f));
            var bg=panel.gameObject.AddComponent<Image>();
            bg.color=new Color(.09f,.075f,.15f,.99f);
            bg.raycastTarget=true;
            AddText(panel,"TRAINERS — ASSIGN SLOT " + (selectedSlot+1),
                new Vector2(430f,40f),new Vector2(18f,-14f),25f,TextAlignmentOptions.TopLeft);
            AddButton(panel,"X",new Vector2(36f,34f),new Vector2(462f,-12f),
                ClosePicker,new Color(.52f,.15f,.2f,1f));
            AddButton(panel,"Clear slot",new Vector2(153f,34f),new Vector2(18f,-87f),()=>{
                if (TryClear(save,selectedSlot)) { RefreshLabels(); ClosePicker(); }
            },new Color(.48f,.28f,.18f,1f));
            for (int row=0;row<RowsPerPage;++row)
            {
                int index=page*RowsPerPage+row;
                if (index>=choices.Count) break;
                CardState target=choices[index];
                int assigned=SlotOf(target);
                // Deck order is the copy identity, not GetID() (which may
                // be the same base card ID for multiple identical PocketMon).
                string label=DisplayName(target)+"  [copy "+(index+1)+"]"+
                    (assigned<0?"":"  (slot "+(assigned+1)+"; click to move)");
                AddButton(panel,label,new Vector2(470f,38f),
                    new Vector2(18f,-136f-row*42f),()=>{
                        if (TryAssign(save,selectedSlot,target)) { RefreshLabels(); ClosePicker(); }
                    },new Color(.31f,.28f,.47f,1f));
            }
            AddButton(panel,"< Prev",new Vector2(112f,35f),new Vector2(18f,-492f),
                ()=>{if(page>0){page--;BuildPicker();}},new Color(.35f,.29f,.42f,1f));
            AddButton(panel,"Next >",new Vector2(112f,35f),new Vector2(366f,-492f),
                ()=>{if(page<maxPage){page++;BuildPicker();}},new Color(.35f,.29f,.42f,1f));
            if (choices.Count==0)
                AddText(panel,"No PocketMon unit cards in your run deck.",
                    new Vector2(460f,100f),new Vector2(18f,-175f),18f,
                    TextAlignmentOptions.TopLeft);
        }

        private static bool Eligible(CardState card)
        {
            if (card == null || card.IsChampionCard()) return false;
            var character = card.GetSpawnCharacterData();
            if (character == null) return false;

            // "Trainers" identifies the CLAN; "PocketMon" is the UNIT SUBTYPE.
            // Check the spawned character's actual subtype data so the picker
            // includes all and only PocketMon unit cards, including evolved
            // forms and separate upgraded copies, regardless of card ID.
            var subtypes = character.GetSubtypes();
            if (subtypes == null) return false;
            return subtypes.Any(subtype => subtype != null &&
                ((!String.IsNullOrEmpty(subtype.Key) &&
                  (subtype.Key.Equals("TrainersPocketMon", StringComparison.OrdinalIgnoreCase) ||
                   subtype.Key.EndsWith("-Subtype-TrainersPocketMon", StringComparison.OrdinalIgnoreCase))) ||
                 String.Equals(subtype.LocalizedName, "PocketMon", StringComparison.OrdinalIgnoreCase)));
        }
        private static string DisplayName(CardState card)
        {
            try
            {
                var character = card.GetSpawnCharacterData();
                if (character != null) return character.GetName();
            }
            catch { }
            return card.GetCardDataID();
        }
        private static string CompactName(CardState card)
        {
            string value = DisplayName(card);
            return value.Length > 9 ? value.Substring(0, 8) + "…" : value;
        }
        private static string ShortId(CardState card)
        {
            string id = card.GetID().ToString();
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }
        private static bool HasMarker(CardState card, int slot)
        {
            if (card == null || !ready || String.IsNullOrEmpty(markerIds[slot])) return false;
            var modifiers = card.GetCardStateModifiers();
            var upgrades = modifiers == null ? null : modifiers.GetCardUpgrades();
            return upgrades != null && upgrades.Any(u => u != null &&
                String.Equals(u.GetCardUpgradeDataId(), markerIds[slot],
                    StringComparison.OrdinalIgnoreCase));
        }
        private static CardState FindOwner(SaveManager save, int slot)
        {
            CardState owner = null;
            var deck = save == null ? null : save.GetDeckState();
            if (deck == null) return null;
            foreach (var candidate in deck)
            {
                if (!HasMarker(candidate, slot)) continue;
                if (owner != null) return null; // duplicate marker: fail safe
                owner = candidate;
            }
            return owner;
        }
        private static int SlotOf(CardState card)
        {
            for (int i = 0; i < SlotCount; ++i)
                if (HasMarker(card, i)) return i;
            return -1;
        }

        // Public only so the Harmony patch below can resolve a SINGLE saved
        // card copy. This never changes the source CharacterData/other copies.
        internal static bool IsRosterAssigned(CardState card)
        {
            return card != null && ready && SlotOf(card) >= 0;
        }
        private static bool RemoveMarker(CardState card, int slot)
        {
            var modifiers = card.GetCardStateModifiers();
            var upgrades = modifiers == null ? null : modifiers.GetCardUpgrades() as IList<CardUpgradeState>;
            if (upgrades == null) { Warn("Card upgrade collection is not mutable."); return false; }
            int count = 0, index = -1;
            for (int i = 0; i < upgrades.Count; ++i)
            {
                if (upgrades[i] == null || !String.Equals(upgrades[i].GetCardUpgradeDataId(),
                    markerIds[slot], StringComparison.OrdinalIgnoreCase)) continue;
                ++count; index = i;
            }
            if (count != 1) { Warn("Expected one marker; found " + count); return false; }
            upgrades.RemoveAt(index);
            try { card.UpdateCardBodyText(); }
            catch (Exception ex) { Warn("Card text refresh after clearing slot: " + ex.Message); }
            return !HasMarker(card, slot);
        }
        private static bool TryClear(SaveManager save, int slot)
        {
            if (!CanEdit()) return false;
            var cards = save.GetDeckState().Where(c => HasMarker(c, slot)).ToList();
            if (cards.Count == 0) return true;
            if (cards.Count != 1) { Warn("Ambiguous card owners for slot " + (slot + 1)); return false; }
            bool done = RemoveMarker(cards[0], slot);
            if (done) Info("Cleared slot " + (slot + 1));
            return done;
        }
        // Attach or reattach a slot marker to one actual deck card copy.
        // This follows Soul Savior's one-attachment-per-host behavior:
        // selecting an assigned copy moves its attachment, and selecting a
        // different copy replaces the current occupant of this slot.
        private static bool AddMarker(CardState card, int slot)
        {
            if (HasMarker(card, slot)) return true;
            var modifiers = card == null ? null : card.GetCardStateModifiers();
            if (modifiers == null) return false;
            var marker = new CardUpgradeState();
            marker.Setup(markerTemplates[slot], false, false);
            modifiers.AddUpgrade(marker);
            if (!HasMarker(card, slot)) return false;
            try { card.UpdateCardBodyText(); }
            catch (Exception ex) { Warn("Card text refresh after attachment: " + ex.Message); }
            return true;
        }

        private static bool TryAssign(SaveManager save, int slot, CardState card)
        {
            if (!CanEdit() || slot < 0 || slot >= SlotCount || !Eligible(card)) return false;
            var deck = save.GetDeckState();
            if (deck == null || !deck.Any(c => object.ReferenceEquals(c, card))) return false;

            // Compare actual CardState instances, NOT GetID/GetCardDataID.
            // Two unupgraded PocketMon of the same species are different hosts.
            int oldSlot = SlotOf(card);
            var owners = deck.Where(c => HasMarker(c, slot)).ToList();
            if (owners.Count > 1)
            {
                Warn("More than one card owns slot " + (slot + 1) + "; refusing to overwrite.");
                return false;
            }
            var previous = owners.Count == 1 ? owners[0] : null;
            if (oldSlot == slot && object.ReferenceEquals(previous, card)) return true;

            // Avoid corrupting an already broken card carrying multiple roster
            // markers. A single unit may belong to only one roster slot.
            if (oldSlot >= 0)
            {
                for (int i = 0; i < SlotCount; ++i)
                    if (i != oldSlot && HasMarker(card, i))
                    {
                        Warn("Selected PocketMon already has multiple slot markers; cannot reassign safely.");
                        return false;
                    }
            }

            bool removedOld = false;
            bool removedPrevious = false;
            try
            {
                // Detach first: never leave two slot upgrades on one unit.
                if (oldSlot >= 0 && oldSlot != slot)
                {
                    if (!RemoveMarker(card, oldSlot)) return false;
                    removedOld = true;
                }
                if (previous != null && !object.ReferenceEquals(previous, card))
                {
                    if (!RemoveMarker(previous, slot))
                    {
                        if (removedOld) AddMarker(card, oldSlot);
                        return false;
                    }
                    removedPrevious = true;
                }
                if (!AddMarker(card, slot))
                    throw new InvalidOperationException("Roster slot marker did not attach.");

                Info("PocketMon attached: slot " + (slot + 1) + " -> " + DisplayName(card) +
                     " (deck index " + deck.ToList().FindIndex(c => object.ReferenceEquals(c, card)) + ", ID " + card.GetID() + ")" +
                     (oldSlot >= 0 && oldSlot != slot ? "; moved from slot " + (oldSlot + 1) : "") +
                     (removedPrevious ? "; replaced previous occupant" : ""));
                return true;
            }
            catch (Exception ex)
            {
                Warn("PocketMon assignment failed; rolling back: " + ex);
                if (HasMarker(card, slot) && oldSlot != slot) RemoveMarker(card, slot);
                if (removedOld && !HasMarker(card, oldSlot)) AddMarker(card, oldSlot);
                if (removedPrevious && previous != null && !HasMarker(previous, slot))
                    AddMarker(previous, slot);
                return false;
            }
        }
        private static void Info(string s)
        {
            global::HexTrolls.Plugin.Plugin.Logger.LogInfo("[TrainersNativeRoster] " + s);
        }
        private static void Warn(string s)
        {
            global::HexTrolls.Plugin.Plugin.Logger.LogWarning("[TrainersNativeRoster] " + s);
        }
    }

    // Passive informational status. Stacks indicate roster slot 1-6;
    // there is no attack/health effect or trigger logic.
    public sealed class StatusEffectTrainersRosterSlotState : StatusEffectState
    {
    }

    // The former CardState deployment-predicate Harmony patch was removed.
    // MT2 does not expose a matching predicate in this game build; an empty
    // HarmonyTargetMethods result causes Harmony.PatchAll to throw at startup.
    // Keep the supported CardManager deployment checks below, as well as
    // the already-working roster DrawSpecificCard combat path above.

    // Some MT2 builds perform the deployment check inside CardManager or
    // CombatManager with a CardState argument rather than on CardState itself.
    // In those builds this hook retains per-copy semantics without editing
    // the shared banner subtype of a CharacterData asset.
    [HarmonyPatch]
    internal static class TrainersRosterDeploymentFilterPatch
    {
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var asm = typeof(CardState).Assembly;
            string[] owners = { "CardManager", "CombatManager", "SaveManager" };
            foreach (string ownerName in owners)
            {
                var owner = asm.GetType(ownerName);
                if (owner == null) continue;
                foreach (var method in owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                       BindingFlags.Instance | BindingFlags.Static))
                {
                    if (method.ReturnType != typeof(bool)) continue;
                    if (method.Name.IndexOf("Banner", StringComparison.OrdinalIgnoreCase) < 0 &&
                        method.Name.IndexOf("Deploy", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    var args = method.GetParameters();
                    if (!args.Any(x => x.ParameterType == typeof(CardState))) continue;
                    global::HexTrolls.Plugin.Plugin.Logger.LogInfo(
                        "[TrainersRosterDeploy] Patching deployment filter: " + ownerName + "." + method.Name);
                    yield return method;
                }
            }
        }

        [HarmonyPostfix]
        private static void Postfix(object[] __args, ref bool __result)
        {
            if (__result || __args == null) return;
            foreach (object arg in __args)
            {
                var card = arg as CardState;
                if (card != null && TrainersNativeRosterController.IsRosterAssigned(card))
                {
                    __result = true;
                    return;
                }
            }
        }
    }
}