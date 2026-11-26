// Trainers Wild PocketMon Invasion — isolated enemy-spawn replacement relic.
// Does NOT modify your Trainer roster controller, existing roster relic,
// run deck, or any CharacterData assets. v9.1 adds enemy-only facing;
// retains v7 pools and Cuttlfish protection; no coroutine wrapping.
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
    // v9: Enemy-only visual orientation. Registered CharacterData.GetID()
    // returns a generated runtime GUID, not the string TrainersPocketMon*.
    // Detect known REGISTERED CharacterData, rather than substring matching IDs.
    [HarmonyPatch(typeof(CharacterState), "GetCharacterUI")]
    internal static class TrainersWildPocketMonEnemyFacingPatch
    {
        [HarmonyPostfix]
        private static void Postfix(CharacterState __instance, CharacterUI __result)
        {
            TrainersWildPocketMonInvasion.TryFlipEnemyVisual(__instance, __result);
        }
    }

    // Even if the game's GetCharacterUI getter isn't normally invoked during
    // a spawn, poll live scene CharacterState objects until their art has been
    // constructed. Never alter shared GameObject prefabs or card art.
    [DefaultExecutionOrder(10000)]
    public sealed class TrainersWildPocketMonFacingWatcher : MonoBehaviour
    {
        private float nextScan;
        private int diagnostics;

        private void Update()
        {
            if (Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + 0.25f;
            try
            {
                int matched = 0;
                int artReady = 0;
                var live = UnityEngine.Object.FindObjectsOfType<CharacterState>();
                foreach (var unit in live)
                {
                    if (unit == null || !TrainersWildPocketMonInvasion.IsEnemyPocketMon(unit)) continue;
                    matched++;
                    // Apply the enemy's initial Level once per spawned CharacterState,
                    // before/independently of the visual-mirroring work.
                    TrainersWildPocketMonInvasion.TryAssignEnemyEntryLevel(unit);
                    CharacterUI ui = unit.GetCharacterUI();
                    if (ui == null) continue;
                    artReady++;
                    TrainersWildPocketMonInvasion.TryFlipEnemyVisual(unit, ui);
                }
                if (diagnostics < 4 && (matched > 0 || (++diagnostics % 2) == 0))
                {
                    TrainersWildPocketMonInvasion.LogDiagnostic(
                        "v9.1 facing scan: live CharacterState=" + live.Length +
                        "; enemy PocketMon=" + matched + "; ready CharacterUI=" + artReady);
                    if (matched > 0) diagnostics = 4;
                }
            }
            catch (Exception ex) { TrainersWildPocketMonInvasion.LogFacingErrorOnce(ex); }
        }
    }

    // Lives on the individual enemy CharacterState, never its shared CharacterData.
    // Ensures a random roll is used only once, even if the art watcher scans
    // the unit hundreds of times or CharacterUI is rebuilt.
    public sealed class TrainersWildPocketMonEntryLevelMarker : MonoBehaviour
    {
        internal bool Applied;
        internal bool Rolled;
        internal int Roll;
        internal int EntryLevel;
        internal bool StatsAttempted;
        internal int Ring;
        internal int Wave;
        internal string WaveSource;
        internal bool RolledAtSpawn;
        internal bool FailureLogged;
    }

    // Attach to the per-unit CharacterUI, but flip ONLY the rendered artwork
    // child. Trainworks animated 2D character sheets are rendered on
    // CharacterScale/CharacterUI/Quad_Default (CharacterUIMeshAnimatedSprite).
    // Scaling CharacterUI itself does not reliably mirror that active quad.
    [DefaultExecutionOrder(10000)]
    public sealed class TrainersWildPocketMonFacingVisual : MonoBehaviour
    {
        private Transform art;
        private CharacterState owner;
        private int nativeSign;
        private bool activeMirror;

        internal bool SetEnemy(CharacterState newOwner)
        {
            if (newOwner == null) return false;
            if (activeMirror && owner != null && !object.ReferenceEquals(owner, newOwner))
                Restore();

            if (art == null)
            {
                art = transform.Find("Quad_Default");
                if (art == null)
                {
                    // Quad_Default can be nested. Find its Transform anywhere
                    // below this character UI without depending on a game-only type.
                    foreach (var child in GetComponentsInChildren<Transform>(true))
                    {
                        if (child != transform && child.name == "Quad_Default")
                        {
                            art = child;
                            break;
                        }
                    }
                }
                if (art == null)
                {
                    // The animated sprite class isn't always public/referenced
                    // by consumer mod projects. Look it up by runtime type NAME;
                    // do NOT introduce a compile-time reference to the class.
                    foreach (var component in GetComponentsInChildren<Component>(true))
                    {
                        if (component == null) continue;
                        if (component.GetType().Name.IndexOf(
                            "CharacterUIMeshAnimatedSprite", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            art = component.transform;
                            break;
                        }
                    }
                }
                if (art == null)
                {
                    var mesh = GetComponentInChildren<MeshRenderer>(true);
                    if (mesh != null && mesh.transform != transform) art = mesh.transform;
                }
                if (art == null) return false; // sprite can be instantiated later
            }

            bool first = !activeMirror;
            if (first)
            {
                float x = art.localScale.x;
                if (Mathf.Approximately(x, 0f)) return false;
                nativeSign = x < 0f ? -1 : 1;
                activeMirror = true;
            }
            owner = newOwner;
            ApplyMirror();
            return first;
        }

        private void ApplyMirror()
        {
            if (!activeMirror || art == null) return;
            var scale = art.localScale;
            float currentMagnitude = Mathf.Abs(scale.x);
            if (currentMagnitude <= 0.00001f) return;
            float desired = -nativeSign * currentMagnitude;
            if (!Mathf.Approximately(scale.x, desired))
            {
                scale.x = desired;
                art.localScale = scale;
            }
        }

        private void LateUpdate()
        {
            if (!activeMirror) return;
            try
            {
                if (owner == null || !TrainersWildPocketMonInvasion.IsEnemyPocketMon(owner))
                {
                    Restore();
                    return;
                }
                ApplyMirror();
            }
            catch (Exception) { Restore(); }
        }

        internal string ArtPath => art == null ? "not-loaded" : art.name;

        internal void Restore()
        {
            if (activeMirror && art != null)
            {
                var scale = art.localScale;
                scale.x = nativeSign * Mathf.Abs(scale.x);
                art.localScale = scale;
            }
            owner = null;
            activeMirror = false;
            art = null;
        }
        private void OnDisable() { Restore(); }
    }

    // v7: Keep v6's proven CharacterData-by-ref spawn interception.
    // Do NOT patch/wrap CharacterState.Setup; that probe isn't needed.
    [HarmonyPatch]
    internal static class TrainersWildPocketMonHeroCreatePatch
    {
        private static MethodBase TargetMethod()
        {
            var found = typeof(HeroManager).GetMethods(BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "CreateHeroState" &&
                    m.GetParameters().Length == 5 &&
                    m.GetParameters()[0].ParameterType == typeof(CharacterData) &&
                    m.GetParameters()[4].ParameterType == typeof(CharacterData));
            TrainersWildPocketMonInvasion.LogDiagnostic("v9.1 CreateHeroState hook=" + found);
            return found;
        }

        [HarmonyPrefix]
        private static void Prefix(ref CharacterData __0, ref CharacterData __4)
        {
            try
            {
                CharacterData candidate;
                var original = __0;
                if (!TrainersWildPocketMonInvasion.TryChoose(original, out candidate)) return;
                if (object.ReferenceEquals(__4, original)) __4 = candidate;
                __0 = candidate;
                TrainersWildPocketMonInvasion.RecordSpawn(original, candidate, "CreateHeroState (ref arg)");
            }
            catch (Exception ex)
            {
                TrainersWildPocketMonInvasion.LogDiagnostic("v9.1 CreateHeroState ERROR " + ex);
            }
        }
    }

    [HarmonyPatch]
    internal static class TrainersWildPocketMonHeroRoomPatch
    {
        private static MethodBase TargetMethod()
        {
            var found = typeof(HeroManager).GetMethods(BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "SpawnHeroInRoom" &&
                    m.GetParameters().Length == 5 &&
                    m.GetParameters()[0].ParameterType == typeof(CharacterData));
            TrainersWildPocketMonInvasion.LogDiagnostic("v9.1 SpawnHeroInRoom hook=" + found);
            return found;
        }

        [HarmonyPrefix]
        private static void Prefix(ref CharacterData __0)
        {
            try
            {
                CharacterData candidate;
                var original = __0;
                if (!TrainersWildPocketMonInvasion.TryChoose(original, out candidate)) return;
                __0 = candidate;
                TrainersWildPocketMonInvasion.RecordSpawn(original, candidate, "SpawnHeroInRoom (ref arg)");
            }
            catch (Exception ex)
            {
                TrainersWildPocketMonInvasion.LogDiagnostic("v9.1 SpawnHeroInRoom ERROR " + ex);
            }
        }
    }

    // A permanent probe for the known wave entry point. This confirms the
    // relic is OWNED and all 39 forms resolved BEFORE units are initialized.
    [HarmonyPatch(typeof(HeroManager), "BeginInitialSpawn")]
    internal static class TrainersWildPocketMonInitialWaveProbe
    {
        private static int entered;
        [HarmonyPrefix]
        private static void Prefix()
        {
            if (entered++ >= 3) return;
            TrainersWildPocketMonInvasion.DiagnoseBattleStart();
        }
    }

    internal static class TrainersWildPocketMonInvasion
    {
        // Change to 50 for a 50% chance, or 0 to temporarily disable.
        private const int ReplacementPercent = 100;
        private const string RelicKey = "TrainersWildPocketMonInvasion";
        private const string RelicTitle = "Wild PocketMon Invasion";
        private static readonly BindingFlags Flags = BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly string[] CreatureLines = {
            "Cuttlfish:3", "Hyena:2", "BloodWorm:2", "Snapsaurus:3",
            "Teslasaurus:3", "Garhound:3", "Fireary:1", "Frosset:3",
            "Umbrabeak:3", "Magmoth:2", "Chilload:1", "DragonMoth:3",
            "Leafshrew:2", "Manshrimp:3", "Jourmancoil:2", "Vampirant:3"
        };
        // These assignments are taken verbatim from the user's edited Excel planner.
        // Only enemies with a filled PocketMon pool are eligible for replacement.
        // Bosses and encounters marked Keep are not in this map.
        private static readonly Dictionary<string,string> EnemyPools =
            new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        {
                { "Cowering Sycophant", "Ring 1 Large" },
                { "Fallen Champion", "Ring 1 Large" },
                { "Forsaken", "Ring 1 Small" },
                { "Lurking Miscreant", "Ring 1 Small" },
                { "Ominous Acolyte", "Ring 1 Small" },
                { "Energizing Flautist", "Ring 2 Small" },
                { "Flagellant", "Ring 2 Large" },
                { "Savage Spearman", "Ring 2 Small" },
                { "Sloven Knight", "Ring 2 Large" },
                { "Chosen Acolyte", "Ring 3 Small" },
                { "Chosen Flagellant", "Ring 3 Small" },
                { "Chosen Shieldbearer", "Ring 3 Large" },
                { "Misshapen Fiend", "Ring 3 Small" },
        };

        // A plain family name (e.g., DragonMoth) means Form 1.
        // Pool entries stay configured even when their Ring has no mapped enemies yet.
        private static readonly Dictionary<string,string[]> PocketMonPools =
            new Dictionary<string,string[]>(StringComparer.OrdinalIgnoreCase)
        {
                { "Ring 1 Small", new [] { "DragonMoth1", "Vampirant1" } },
                { "Ring 1 Large", new [] { "Umbrabeak1" } },
                { "Ring 2 Small", new [] { "Manshrimp1", "BloodWorm1" } },
                { "Ring 2 Large", new [] { "DragonMoth2", "Vampirant2" } },
                { "Ring 3 Small", new [] { "Hyena1", "Cuttlfish1" } },
                { "Ring 3 Large", new [] { "Umbrabeak2" } },
                { "Ring 4 Small", new [] { "Manshrimp2", "Hyena2" } },
                { "Ring 4 Large", new [] { "Fireary1", "Leafshrew1" } },
                { "Ring 5 Small", new [] { "Jourmancoil1" } },
                { "Ring 5 Large", new [] { "Leafshrew2" } },
                { "Ring 6 Small", new [] { "Magmoth1" } },
                { "Ring 6 Large", new [] { "Magmoth2" } },
                { "Ring 7 Small", new [] { "Jourmancoil2" } },
                { "Ring 7 Large", new [] { "Chilload1" } },
        };
        private static readonly Dictionary<string,CharacterData> CandidateByForm =
            new Dictionary<string,CharacterData>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> WarnedMissingPool =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static int blockedMappedEnemiesLogged;
        private static readonly List<CharacterData> Candidates = new List<CharacterData>();
        private static bool catalogReady;
        private static bool loggedMissingCatalog;
        private static bool loggedOwnershipError;
        private static bool loggedOwnershipFailure;
        private static bool loggedResolvedRelic;
        private static bool loggedActive;
        private static int replacementsLogged;
        private static RelicData relic;

        // The existing enemy mapping labels identify the encounter Ring
        // (e.g. "Ring 2 Large"). Cache it only when the replacement is chosen.
        // No character prefab, pool assignment, or friendly unit is modified.
        private static int entryRing;
        private static bool warnedMissingWave;
        private static bool warnedMissingRing;
        private const string LevelStatusId = "hextrolls.plugin_level";
        // Inclusive random range: -1, 0, +1 (equal probability).
        private const int MinimumEntryLevel = 1;

        // Each spawn stores its ring/wave/roll against the ORIGINAL registered
        // CharacterData ID, not against a cloned ScriptableObject. A FIFO permits
        // several identical PocketMon enemies in the same wave. The live unit
        // consumes its entry record once; no reroll when art/UI refreshes.
        private sealed class PendingEntry
        {
            internal int Ring, Wave, Roll, Level;
            internal string WaveSource;
        }
        private static readonly Dictionary<string, Queue<PendingEntry>> PendingByCharacterId =
            new Dictionary<string, Queue<PendingEntry>>(StringComparer.OrdinalIgnoreCase);

        private static void QueueEnemyEntry(CharacterData data, int ring,
            int wave, string waveSource, int roll, int level)
        {
            if (data == null) return;
            string id = data.GetID();
            if (String.IsNullOrEmpty(id)) return;
            Queue<PendingEntry> q;
            if (!PendingByCharacterId.TryGetValue(id, out q))
            {
                q = new Queue<PendingEntry>();
                PendingByCharacterId[id] = q;
            }
            // Guard against pending entries from an aborted/previewed spawn.
            if (q.Count >= 64) q.Dequeue();
            q.Enqueue(new PendingEntry { Ring=ring, Wave=wave,
                WaveSource=waveSource, Roll=roll, Level=level });
        }

        private static bool TryTakeEnemyEntry(CharacterData data, out PendingEntry entry)
        {
            entry = null;
            if (data == null) return false;
            Queue<PendingEntry> q;
            if (!PendingByCharacterId.TryGetValue(data.GetID(), out q) || q.Count == 0)
                return false;
            entry = q.Dequeue();
            return true;
        }

        // IMPORTANT: Always spawn the ORIGINAL registered CharacterData. Cloning
        // a CharacterData with UnityEngine.Object.Instantiate creates an object
        // which Trainworks' character-art registry does not recognize; the game
        // renders the black "CHARACTER ART NOT FOUND" placeholder.
        //
        // Enemy stat scaling is done on the live ENEMY CharacterState, never on
        // CharacterData and never on any permanent player card. No level or
        // stat modifiers are added to summoned player units.
        private static bool TryReadBaseStat(CharacterData data, string name, out int value)
        {
            value = 0;
            if (data == null) return false;
            try
            {
                for (Type t = data.GetType(); t != null; t = t.BaseType)
                {
                    FieldInfo f = t.GetField(name, BindingFlags.DeclaredOnly | Flags);
                    if (f == null || f.FieldType != typeof(int)) continue;
                    value = (int)f.GetValue(data);
                    return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        // Use a native CharacterState setter where the installed game version
        // exposes one. The fallback only touches an int field on THIS live unit.
        // Every path is verified through the public stats getters.
        private static bool TryWriteLiveInt(CharacterState unit, int target,
            Func<int> getter, string[] absoluteMethods, string[] deltaMethods,
            string[] fieldNames)
        {
            int current = getter();
            if (current == target) return true;
            Type type = unit.GetType();
            foreach (bool absolute in new[] { true, false })
            {
                string[] names = absolute ? absoluteMethods : deltaMethods;
                foreach (string name in names)
                {
                    // Do not guess at incompatible signatures or extra params.
                    foreach (MethodInfo m in type.GetMethods(Flags))
                    {
                        if (m.Name != name) continue;
                        ParameterInfo[] pars = m.GetParameters();
                        if (pars.Length != 1 || pars[0].ParameterType != typeof(int)) continue;
                        try
                        {
                            current = getter();
                            m.Invoke(unit, new object[] { absolute ? target : (target - current) });
                            if (getter() == target) return true;
                        }
                        catch (Exception) { /* test next available setter */ }
                    }
                }
            }
            foreach (string fieldName in fieldNames)
            {
                for (Type t = type; t != null; t = t.BaseType)
                {
                    FieldInfo f = t.GetField(fieldName, BindingFlags.DeclaredOnly | Flags);
                    if (f == null || f.FieldType != typeof(int) || f.IsInitOnly) continue;
                    try
                    {
                        int prior = (int)f.GetValue(unit);
                        f.SetValue(unit, target);
                        if (getter() == target) return true;
                        f.SetValue(unit, prior); // no effect: revert immediately
                    }
                    catch (Exception) { }
                }
            }
            return false;
        }

        private static void ScaleEnemyLiveStats(CharacterState unit, int level)
        {
            CharacterData source = unit.GetSourceCharacterData();
            int baseAttack, baseHp;
            if (!TryReadBaseStat(source, "attackDamage", out baseAttack) ||
                !TryReadBaseStat(source, "health", out baseHp) ||
                baseHp < 1 || baseAttack < 0)
            {
                Warn("Enemy stats: couldn't read registered base stats for " +
                    (source == null ? "unknown" : source.GetName()));
                return;
            }

            int wantedAttack, wantedHp;
            try
            {
                wantedAttack = checked(baseAttack * level);
                wantedHp = checked(baseHp * level);
            }
            catch (OverflowException) { Warn("Enemy stat scaling overflow; skipped."); return; }

            bool attackOk = TryWriteLiveInt(unit, wantedAttack,
                () => unit.GetAttackDamage(),
                new[] { "SetAttackDamage", "SetAttack", "SetBaseAttackDamage" },
                new[] { "AddAttackDamage", "BuffDamage", "BuffAttackDamage", "ModifyAttackDamage" },
                new[] { "attackDamage", "baseAttackDamage", "attack", "attackDamageBase" });
            // Monster Train 2 uses a TWO-argument health setter:
            // SetHealth(currentHp, maxHp). The old one-argument setter probe
            // could change Attack but could not change Max HP, leaving every
            // higher-Level enemy at its Level-1 HP. Use the game's native
            // CharacterState setter, not a CharacterData clone or a status buff.
            // This runs once per newly spawned enemy (StatsAttempted marker).
            bool hpOk = unit.GetMaxHP() == wantedHp;
            if (!hpOk)
            {
                try
                {
                    unit.SetHealth(wantedHp, wantedHp);
                    hpOk = unit.GetMaxHP() == wantedHp;
                }
                catch (Exception ex)
                {
                    Warn("Native SetHealth(currentHP, maxHP) failed for " +
                        source.GetName() + ": " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            Log("Enemy combat stats: " + source.GetName() +
                "; level=" + level +
                "; Attack=" + unit.GetAttackDamage() + "/" + wantedAttack +
                "; MaxHP=" + unit.GetMaxHP() + "/" + wantedHp +
                "; original registered art preserved" +
                "; attackSet=" + attackOk + "; hpSet=" + hpOk);
            if (!attackOk || !hpOk)
            {
                Warn("Enemy-only stat scaling incomplete for " + source.GetName() +
                    ": Attack=" + unit.GetAttackDamage() + "/" + wantedAttack +
                    ", MaxHP=" + unit.GetMaxHP() + "/" + wantedHp +
                    ". Original character artwork remains registered.");
            }
        }

        private static void Log(string m) =>
            HexTrolls.Plugin.Plugin.Logger.LogInfo("[TrainersWildPocketMon] " + m);
        private static void Warn(string m) =>
            HexTrolls.Plugin.Plugin.Logger.LogWarning("[TrainersWildPocketMon] " + m);

        private static int loggedSwapPaths;

        internal static void LogDiagnostic(string message) => Log(message);

        private static int facingLogs;
        private static bool facingErrorLogged;
        private static TrainersWildPocketMonFacingWatcher facingWatcher;

        internal static void EnableFacingWatcher()
        {
            try
            {
                if (facingWatcher != null) return;
                var host = new GameObject("TrainersWildPocketMon_FacingWatcher");
                UnityEngine.Object.DontDestroyOnLoad(host);
                facingWatcher = host.AddComponent<TrainersWildPocketMonFacingWatcher>();
                Log("v9.1 ENEMY FACING watcher installed (checks spawned art every 0.25 sec)");
            }
            catch (Exception ex) { LogFacingErrorOnce(ex); }
        }

        /// <summary>
        /// Enemy PocketMon entry Level:
        ///   max(1, 2 * ring + floor(wave / 2) + random(-1, 0, +1)).
        ///
        /// Fix: the previous version returned WITHOUT applying any status when
        /// its guessed wave getters were unavailable, leaving enemies at Level 0.
        /// A missing API now uses a visible logged fallback to Wave 1. The
        /// per-character marker stores one random result and prevents rerolls.
        /// </summary>
        internal static void TryAssignEnemyEntryLevel(CharacterState unit)
        {
            if (unit == null || unit.IsDestroyed || !IsEnemyPocketMon(unit)) return;
            var game = AllGameManagers.Instance;
            var save = game == null ? null : game.GetSaveManager();
            if (save == null || save.PreviewMode) return;

            var marker = unit.GetComponent<TrainersWildPocketMonEntryLevelMarker>();
            if (marker != null && marker.Applied) return;
            if (marker == null)
                marker = unit.gameObject.AddComponent<TrainersWildPocketMonEntryLevelMarker>();

            if (!marker.Rolled)
            {
                PendingEntry pending;
                if (TryTakeEnemyEntry(unit.GetSourceCharacterData(), out pending))
                {
                    marker.Ring = pending.Ring;
                    marker.Wave = pending.Wave;
                    marker.WaveSource = pending.WaveSource;
                    marker.Roll = pending.Roll;
                    marker.EntryLevel = pending.Level;
                    marker.Rolled = true;
                    marker.RolledAtSpawn = true;
                }
            }

            int ring = marker.RolledAtSpawn ? marker.Ring : entryRing;
            if (ring < 1)
            {
                // All mapped ring assignments in TryChoose have a Ring N label.
                // Never leave an enemy without Level solely due to stale ring
                // context; explicitly report that this fallback was necessary.
                ring = 1;
                if (!warnedMissingRing)
                {
                    warnedMissingRing = true;
                    Warn("Enemy Level: ring wasn't determined by the spawn mapping; " +
                         "using Ring 1 fallback (check encounter mappings).");
                }
            }

            int wave;
            string source;
            if (marker.RolledAtSpawn)
            {
                wave = marker.Wave;
                source = marker.WaveSource;
            }
            else if (!TryReadCurrentWave(game, out wave, out source))
            {
                // The previous version aborted here, yielding Level 0. Wave 1
                // is a deliberate, explicit safety fallback, not a claim of
                // accurate later-wave progression. Diagnostics make it visible.
                wave = 1;
                source = "wave-1 fallback: runtime wave/turn counter unavailable";
                if (!warnedMissingWave)
                {
                    warnedMissingWave = true;
                    Warn("Enemy Level: no game wave/turn counter found. " +
                         "Using Wave 1 fallback to prevent Level 0. " +
                         "Send this log to identify the correct wave API.");
                    DiagnoseWaveAccess(game);
                }
            }

            if (!marker.Rolled)
            {
                marker.Roll = UnityEngine.Random.Range(-1, 2);
                marker.EntryLevel = Math.Max(MinimumEntryLevel,
                    ring * 2 + wave / 2 + marker.Roll);
                marker.Rolled = true;
            }
            int desired = marker.EntryLevel;

            try
            {
                int current = unit.GetStatusEffectStacks(LevelStatusId);
                if (current != desired)
                {
                    if (current > 0)
                        unit.RemoveStatusEffect(LevelStatusId, current, false);
                    // Use same live-status AddStatusEffect overload as
                    // CardEffectTrainersGainExp.SyncLiveStatus.
                    unit.AddStatusEffect(LevelStatusId, desired,
                        (CharacterState)null, false, false);
                }
                int actual = unit.GetStatusEffectStacks(LevelStatusId);
                if (actual != desired)
                {
                    if (!marker.FailureLogged)
                    {
                        marker.FailureLogged = true;
                        Warn("Enemy entry Level did not stick: " +
                             unit.GetSourceCharacterData().GetName() +
                             "; requested=" + desired + ", observed=" + actual +
                             "; status=" + LevelStatusId +
                             "; ring=" + ring + "; wave=" + wave + " (" + source + ")");
                    }
                    // Leave marker.Applied=false to retry same rolled level.
                    return;
                }
                marker.Applied = true;
                if (!marker.StatsAttempted)
                {
                    marker.StatsAttempted = true;
                    ScaleEnemyLiveStats(unit, desired);
                }
                Log("Enemy entry Level: " + unit.GetSourceCharacterData().GetName() +
                    "; Ring=" + ring + "; Wave=" + wave + " [" + source + "]" +
                    "; random=" + (marker.Roll >= 0 ? "+" : "") + marker.Roll +
                    "; Level=" + actual + " (verified, native art)");
            }
            catch (Exception ex)
            {
                if (!marker.FailureLogged)
                {
                    marker.FailureLogged = true;
                    Warn("Enemy Level assignment failed for " +
                         unit.GetSourceCharacterData().GetName() + ": " + ex);
                }
            }
        }

        // Game versions expose wave counters through different managers.
        // Discover them without depending on a fragile compile-time API.
        // Prefer explicit WaveNumber getters, then zero-based WaveIndex.
        // Only use a per-combat turn number if no wave counter is published.
        // One-time diagnostic: if the game version publishes a wave counter
        // under an unexpected name, log the names (not values) of candidates.
        // This avoids silently confusing the fallback with a true wave value.
        private static void DiagnoseWaveAccess(object game)
        {
            try
            {
                object[] hosts = {
                    ReadMember(game, "GetHeroManager"),
                    ReadMember(game, "GetBattleManager"),
                    ReadMember(game, "GetCombatManager"),
                    ReadMember(game, "GetTurnManager"), game
                };
                foreach (var host in hosts)
                {
                    if (host == null) continue;
                    var type = host.GetType();
                    var names = type.GetMembers(Flags)
                        .Where(m => m.Name.IndexOf("wave", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    m.Name.IndexOf("turn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    m.Name.IndexOf("round", StringComparison.OrdinalIgnoreCase) >= 0)
                        .Select(m => m.Name).Distinct().Take(25).ToArray();
                    Log("Wave API candidates on " + type.Name + ": " +
                        (names.Length > 0 ? String.Join(", ", names) : "(none found)"));
                }
            }
            catch (Exception ex) { Warn("Wave API diagnostics failed: " + ex.Message); }
        }

        private static bool TryReadCurrentWave(object game, out int wave, out string source)
        {
            wave = 0;
            source = null;
            object[] hosts = {
                ReadMember(game, "GetHeroManager"),
                ReadMember(game, "GetBattleManager"),
                ReadMember(game, "GetCombatManager"),
                ReadMember(game, "GetTurnManager"),
                ReadMember(game, "GetSaveManager"), game
            };
            foreach (var host in hosts)
            {
                if (host == null) continue;
                foreach (string name in new[] {
                    "GetCurrentWaveNumber", "GetWaveNumber", "CurrentWaveNumber",
                    "WaveNumber", "currentWaveNumber", "waveNumber",
                    "GetCurrentWaveIndex", "GetWaveIndex", "CurrentWaveIndex",
                    "WaveIndex", "currentWaveIndex", "waveIndex",
                    "GetCurrentWave", "GetWave", "CurrentWave", "currentWave"
                })
                {
                    int raw;
                    if (!TryInt(ReadMember(host, name), out raw)) continue;
                    bool zeroBased = name.IndexOf("Index", StringComparison.OrdinalIgnoreCase) >= 0;
                    int candidate = zeroBased ? raw + 1 : Math.Max(1, raw);
                    if (raw < 0 || candidate < 1 || candidate > 100) continue;
                    wave = candidate;
                    source = host.GetType().Name + "." + name;
                    return true;
                }
                // A few game builds store an active WaveState object rather
                // than a direct integer; inspect only its index/number fields.
                foreach (string parent in new[] { "GetCurrentWave", "CurrentWave", "currentWave" })
                {
                    object state = ReadMember(host, parent);
                    if (state == null) continue;
                    foreach (string member in new[] { "GetWaveNumber", "WaveNumber",
                        "GetWaveIndex", "WaveIndex", "Index", "Number" })
                    {
                        int raw;
                        if (!TryInt(ReadMember(state, member), out raw)) continue;
                        int candidate = member.IndexOf("Index", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        member == "Index" ? raw + 1 : raw;
                        if (candidate < 1 || candidate > 100) continue;
                        wave = candidate;
                        source = host.GetType().Name + "." + parent + "." + member;
                        return true;
                    }
                }
            }
            foreach (var host in hosts)
            {
                if (host == null) continue;
                // Monster Train normally introduces one wave each combat turn.
                // This is a fallback only when the current wave isn't exposed.
                foreach (string name in new[] {
                    "GetCurrentTurnNumber", "GetTurnNumber", "CurrentTurnNumber",
                    "currentTurnNumber", "GetCurrentTurn", "CurrentTurn",
                    "GetCurrentTurnIndex", "CurrentTurnIndex", "currentTurnIndex",
                    "GetTurnIndex", "TurnIndex", "turnIndex",
                    "GetTurnCount", "TurnCount", "turnCount",
                    "GetNumTurns", "NumTurns", "numTurns",
                    "GetRoundNumber", "RoundNumber", "roundNumber"
                })
                {
                    int value;
                    if (!TryInt(ReadMember(host, name), out value)) continue;
                    // TurnIndex is zero-based; zero-valued initial counters
                    // in some builds also correspond to the first wave.
                    int candidate = name.IndexOf("Index", StringComparison.OrdinalIgnoreCase) >= 0
                        ? value + 1 : Math.Max(1, value);
                    if (candidate < 1 || candidate > 100) continue;
                    wave = candidate;
                    source = host.GetType().Name + "." + name + " (turn fallback)";
                    return true;
                }
            }
            return false;
        }

        private static object ReadMember(object owner, string name)
        {
            if (owner == null) return null;
            try
            {
                var type = owner.GetType();
                var method = type.GetMethod(name, Flags, null, Type.EmptyTypes, null);
                if (method != null && method.ReturnType != typeof(void))
                    return method.Invoke(owner, null);
                var property = type.GetProperty(name, Flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                    return property.GetValue(owner, null);
                var field = type.GetField(name, Flags);
                return field == null ? null : field.GetValue(owner);
            }
            catch { return null; }
        }

        private static bool TryInt(object value, out int result)
        {
            result = 0;
            if (value == null || value is bool) return false;
            try { result = Convert.ToInt32(value); return true; }
            catch { return false; }
        }

        internal static bool IsEnemyPocketMon(CharacterState unit)
        {
            if (unit == null || unit.GetTeamType() != Team.Type.Heroes) return false;
            var data = unit.GetSourceCharacterData();
            if (data == null || !EnsureCatalog()) return false;

            // Matching by substring GetID() fails with Trainworks runtime GUIDs.
            // Match exact registered runtime identity/ID, then displayed name.
            string runtimeId = data.GetID();
            string displayName = data.GetName();
            foreach (var known in Candidates)
            {
                if (known == null) continue;
                if (object.ReferenceEquals(known, data)) return true;
                if (!String.IsNullOrEmpty(runtimeId) &&
                    String.Equals(known.GetID(), runtimeId, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!String.IsNullOrEmpty(displayName) &&
                    String.Equals(known.GetName(), displayName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        internal static void TryFlipEnemyVisual(CharacterState unit, CharacterUI ui)
        {
            if (unit == null || ui == null) return;
            try
            {
                bool enemy = IsEnemyPocketMon(unit);
                var marker = ui.GetComponent<TrainersWildPocketMonFacingVisual>();
                if (!enemy)
                {
                    if (marker != null) marker.Restore();
                    return;
                }
                // Also initialize on UI creation so the Level is visible at
                // first render, not only at the next quarter-second scan.
                TryAssignEnemyEntryLevel(unit);
                if (marker == null)
                    marker = ui.gameObject.AddComponent<TrainersWildPocketMonFacingVisual>();
                if (marker.SetEnemy(unit) && facingLogs++ < 20)
                    Log("v9.1 MIRRORED enemy PocketMon LEFT: " +
                        unit.GetSourceCharacterData().GetName() +
                        " using artwork child " + marker.ArtPath);
            }
            catch (Exception ex) { LogFacingErrorOnce(ex); }
        }

        internal static void LogFacingErrorOnce(Exception ex)
        {
            if (facingErrorLogged) return;
            facingErrorLogged = true;
            Warn("v9.1 enemy-only facing visual skipped: " + ex);
        }

        internal static void RecordSpawn(CharacterData original, CharacterData candidate, string path)
        {
            if (original == null || candidate == null) return;
            if (loggedSwapPaths++ < 35)
                Log("v9.1 SWAPPED " + path + ": " + original.GetName() +
                    " -> " + candidate.GetName());
        }

        internal static void DiagnoseBattleStart()
        {
            // Reset pending per-spawn bookkeeping before the initial enemy wave.
            PendingByCharacterId.Clear();
            Log("v9.1 BeginInitialSpawn entered. Checking relic ownership and safe catalog before wave.");
            try
            {
                var game=AllGameManagers.Instance;
                var save=game==null ? null : game.GetSaveManager();
                if (save == null || save.PreviewMode) return;
                bool owned=OwnsThisRelic(save);
                Log("v9.1 battle-start ownership=" + owned);
                if (owned)
                {
                    Log("v9.1 battle-start PocketMon catalog=" + EnsureCatalog() +
                        "; enemy-safe forms=" + Candidates.Count);
                    Log("v9.1 explicit mappings=" + EnemyPools.Count +
                        "; defined pools=" + PocketMonPools.Count +
                        "; unmapped enemies stay original. Cuttlfish excluded.");
                    EnableFacingWatcher();
                }
            }
            catch(Exception ex) { Warn("v9.1 battle-start probe failed: " + ex); }
        }

        internal static bool TryChoose(CharacterData original, out CharacterData replacement)
        {
            replacement = null;
            if (original == null || ReplacementPercent <= 0) return false;
            var game = AllGameManagers.Instance;
            var save = game == null ? null : game.GetSaveManager();
            // The first enemy wave can be assembled BEFORE GetIsRunningCombat
            // becomes true, and coroutine factory methods are invoked early.
            // HeroManager's spawn path is already the context guard.
            if (save == null || save.PreviewMode) return false;
            // Unassigned enemies are intentionally unchanged. The workbook
            // defines pools for Rings 4-7 but does not yet assign their enemies.
            var enemyName = original.GetName();
            string poolName;
            if (String.IsNullOrEmpty(enemyName) ||
                !EnemyPools.TryGetValue(enemyName.Trim(), out poolName)) return false;
            if (!IsOrdinaryEnemy(original))
            {
                if (blockedMappedEnemiesLogged++ < 8)
                    Warn("v9.1 MAPPED BUT PROTECTED by boss/special safeguards: " +
                         enemyName + " (pool " + poolName + ")");
                return false;
            }
            if (!OwnsThisRelic(save))
            {
                if (!loggedOwnershipFailure)
                {
                    loggedOwnershipFailure = true;
                    Warn("Spawn hook reached, but Wild PocketMon Invasion ownership could not be confirmed. " +
                         "Check the 'Resolved relic' and 'ownership probe' lines.");
                }
                return false;
            }
            if (ReplacementPercent < 100 &&
                UnityEngine.Random.Range(0, 100) >= ReplacementPercent) return false;
            if (!EnsureCatalog()) return false;

            string[] configuredForms;
            if (!PocketMonPools.TryGetValue(poolName, out configuredForms) ||
                configuredForms == null || configuredForms.Length == 0)
            {
                if (WarnedMissingPool.Add(poolName))
                    Warn("v9.1 no configured forms for pool '" + poolName +
                         "'. Skipping enemy replacement rather than choosing randomly.");
                return false;
            }

            // Resolve only the forms assigned to this enemy's pool.
            // Cuttlfish forms are absent from CandidateByForm because Conduit
            // crashes enemy setup. Ring 3 Small therefore uses Hyena 1 only.
            var eligible = new List<CharacterData>();
            foreach (var form in configuredForms)
            {
                CharacterData formData;
                if (CandidateByForm.TryGetValue(form, out formData) &&
                    formData != null && !object.ReferenceEquals(formData, original) &&
                    !formData.GetName().StartsWith("Cuttlfish", StringComparison.OrdinalIgnoreCase))
                    eligible.Add(formData);
            }
            if (eligible.Count == 0)
            {
                if (WarnedMissingPool.Add(poolName))
                    Warn("v9.1 pool '" + poolName + "' has no enemy-safe registered forms. " +
                         "Keeping original enemy " + enemyName);
                return false;
            }
            var chosen = eligible[UnityEngine.Random.Range(0, eligible.Count)];
            // Ring is determined by the ACTIVE enemy's mapped encounter pool;
            // it is not inferred from the chosen PocketMon's species/form.
            var ringParts = poolName.Split(' ');
            int parsedRing = 0;
            if (ringParts.Length >= 2 &&
                string.Equals(ringParts[0], "Ring", StringComparison.OrdinalIgnoreCase) &&
                Int32.TryParse(ringParts[1], out parsedRing) && parsedRing > 0)
                entryRing = parsedRing;
            // Spawn with the ORIGINAL registered CharacterData so its Trainworks
            // art/prefab registration works. Per-enemy stats and Level are applied
            // to the live CharacterState by TryAssignEnemyEntryLevel, once.
            int ring = parsedRing > 0 ? parsedRing : Math.Max(1, entryRing);
            int wave;
            string waveSource;
            if (!TryReadCurrentWave(game, out wave, out waveSource))
            {
                wave = 1;
                waveSource = "wave-1 fallback: runtime counter unavailable";
            }
            int roll = UnityEngine.Random.Range(-1, 2);
            int targetLevel = Math.Max(MinimumEntryLevel,
                ring * 2 + wave / 2 + roll);
            QueueEnemyEntry(chosen, ring, wave, waveSource, roll, targetLevel);
            replacement = chosen;
            if (replacementsLogged < 35)
            {
                ++replacementsLogged;
                Log("v9.1 Enemy swap [" + poolName + "]: " + enemyName +
                    " -> " + chosen.GetName() + " (enemy team)");
                if (replacementsLogged == 35)
                    Log("Further per-unit swap messages suppressed until restart.");
            }
            return true;
        }

        private static bool IsOrdinaryEnemy(CharacterData data)
        {
            // Exclude our own replacements so nested spawn calls cannot reroll.
            // Also avoid defeating encounter win conditions by changing bosses,
            // flying bosses, treasure collectors or scripted special enemies.
            if (HasSubtypeKeyword(data, "PocketMon")) return false;
            var nativeName = data.GetName();
            // "Fallen Champion" is a regular Ring 1 Heavy enemy explicitly
            // mapped by the user. Do not reject it merely for its name;
            // the native boss/miniboss flags below still protect actual bosses.
            if (!String.IsNullOrEmpty(nativeName) &&
                nativeName.IndexOf("Titan", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            foreach (var token in new[] {
                "Boss", "Miniboss", "MiniBoss", "Titan", "TreasureCollector",
                "Collector", "Pyre", "Objective", "Story", "SpecialEnemy"
            })
                if (HasSubtypeKeyword(data, token)) return false;
            foreach (var methodName in new[] {
                "GetIsBoss", "IsBoss", "GetIsMiniboss", "GetIsMiniBoss",
                "GetIsFlyingBoss", "IsFlyingBoss", "GetIsTitan",
                "IsTitan", "GetIsCompanionBoss", "IsCompanionBoss"
            })
            {
                try
                {
                    var method = data.GetType().GetMethod(methodName, Flags,
                        null, Type.EmptyTypes, null);
                    if (method != null && method.ReturnType == typeof(bool) &&
                        (bool)method.Invoke(data, null)) return false;
                }
                catch { /* Unknown flag API: continue with subtype checks. */ }
            }
            return true;
        }

        private static bool HasSubtypeKeyword(CharacterData character, string token)
        {
            try
            {
                var subtypes = character.GetSubtypes();
                if (subtypes == null) return false;
                foreach (var subtype in subtypes)
                {
                    if (subtype == null) continue;
                    if ((!String.IsNullOrEmpty(subtype.Key) &&
                         subtype.Key.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (!String.IsNullOrEmpty(subtype.LocalizedName) &&
                         subtype.LocalizedName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0))
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static bool EnsureCatalog()
        {
            if (catalogReady && Candidates.Count > 0) return true;
            try
            {
                var container = Railend.GetContainer();
                if (container == null) return false;
                var registry = container.GetInstance<IRegister<CharacterData>>();
                if (registry == null) return false;
                Candidates.Clear();
                CandidateByForm.Clear();
                var guid = MyPluginInfo.PLUGIN_GUID;
                int unresolved = 0;
                int excludedUnsafe = 0;
                foreach (var line in CreatureLines)
                {
                    var spec = line.Split(':');
                    var count = Int32.Parse(spec[1]);
                    for (int i = 1; i <= count; ++i)
                    {
                        var shortId = "TrainersPocketMon" + spec[0] +
                                      "Form" + i + "Character";
                        CharacterData candidate = null;
                        foreach (var key in new[] {
                            guid + "-Character-" + shortId,
                            guid + "-CharacterData-" + shortId,
                            guid + "-" + shortId,
                            shortId
                        })
                        {
                            if (registry.TryGetValue(key, out candidate) &&
                                candidate != null) break;
                        }
                        if (candidate != null)
                        {
                            // These three forms all start with Conduit. The user's
                            // crash stack is StatusEffectConduitState.OnStacksAdded
                            // on Cuttlfish 3, which is not safe on the enemy side.
                            // Do not modify registered CharacterData globally.
                            if (spec[0] == "Cuttlfish")
                            {
                                ++excludedUnsafe;
                                continue;
                            }
                            if (!Candidates.Any(c => c.GetID() == candidate.GetID()))
                                Candidates.Add(candidate);
                            CandidateByForm[spec[0] + i] = candidate;
                        }
                        else ++unresolved;
                    }
                }
                catalogReady = Candidates.Count > 0;
                if (catalogReady)
                    Log("v9.1 Loaded " + Candidates.Count + "/39 enemy-safe Trainer PocketMon characters" +
                        " (skipped " + excludedUnsafe + " Conduit-bearing Cuttlfish forms)" +
                        (unresolved > 0 ? " (missing " + unresolved + "; check unit JSON registration)" : ""));
                else if (!loggedMissingCatalog)
                {
                    loggedMissingCatalog = true;
                    Warn("No enemy-safe PocketMon CharacterData IDs resolved. " +
                         "Ensure TrainersPocketMon39.json is registered. " +
                         "Expected e.g. " + guid + "-Character-TrainersPocketMonHyenaForm1Character");
                }
                return catalogReady;
            }
            catch (Exception ex)
            {
                if (!loggedMissingCatalog)
                {
                    loggedMissingCatalog = true;
                    Warn("PocketMon registry lookup failed: " + ex);
                }
                return false;
            }
        }

        // Trainworks registration in the user's log uses -RelicData-, NOT
        // -Relic-. Resolving the runtime RelicData object is essential;
        // GetHasRelic receives the registered object (as in Roster Protocol v3).
        private static RelicData ResolveRelic(SaveManager save)
        {
            if (relic != null) return relic;
            string[] keys = {
                MyPluginInfo.PLUGIN_GUID + "-RelicData-" + RelicKey,
                MyPluginInfo.PLUGIN_GUID + "-Relic-" + RelicKey,
                MyPluginInfo.PLUGIN_GUID + "-" + RelicKey,
                RelicKey
            };
            try
            {
                var container = Railend.GetContainer();
                var register = container == null ? null :
                    container.GetInstance<IRegister<RelicData>>();
                if (register != null)
                {
                    foreach (var key in keys)
                    {
                        RelicData found;
                        if (register.TryGetValue(key, out found) && found != null)
                        {
                            relic = found;
                            LogResolved("Trainworks registry key=" + key, found);
                            return relic;
                        }
                    }
                }
            }
            catch (Exception ex) { Warn("Relic register probe: " + ex.Message); }

            try
            {
                var all = save == null ? null : save.GetAllGameData();
                if (all != null)
                {
                    // First try direct string lookup, when supplied by this
                    // version's AllGameData.
                    foreach (var method in all.GetType().GetMethods(Flags))
                    {
                        if (!method.Name.StartsWith("Find", StringComparison.OrdinalIgnoreCase) ||
                            method.Name.IndexOf("Relic", StringComparison.OrdinalIgnoreCase) < 0 ||
                            !typeof(RelicData).IsAssignableFrom(method.ReturnType))
                            continue;
                        var ps = method.GetParameters();
                        if (ps.Length != 1 || ps[0].ParameterType != typeof(string)) continue;
                        foreach (var key in keys)
                        {
                            try
                            {
                                var match = method.Invoke(all, new object[] { key }) as RelicData;
                                if (match == null) continue;
                                relic = match;
                                LogResolved("AllGameData." + method.Name, match);
                                return relic;
                            }
                            catch { }
                        }
                    }
                    // This exact scan succeeded for Roster Protocol v4 in
                    // the same user's BepInEx logs.
                    foreach (var method in all.GetType().GetMethods(Flags))
                    {
                        if (method.GetParameters().Length != 0 ||
                            (method.Name.IndexOf("Relic", StringComparison.OrdinalIgnoreCase) < 0 &&
                             method.Name.IndexOf("Collectable", StringComparison.OrdinalIgnoreCase) < 0) ||
                            !typeof(IEnumerable).IsAssignableFrom(method.ReturnType)) continue;
                        try
                        {
                            var collection = method.Invoke(all, null) as IEnumerable;
                            var match = FindRegisteredRelic(collection);
                            if (match == null) continue;
                            relic = match;
                            LogResolved("AllGameData." + method.Name, match);
                            return relic;
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Warn("AllGameData relic probe: " + ex.Message); }
            if (!loggedResolvedRelic)
            {
                loggedResolvedRelic = true;
                Warn("Could not find the REGISTERED Wild PocketMon Invasion relic. " +
                     "Expected Trainworks ID=" + keys[0]);
            }
            return null;
        }

        private static RelicData FindRegisteredRelic(IEnumerable list)
        {
            if (list == null) return null;
            foreach (object obj in list)
            {
                var candidate = obj as RelicData;
                if (candidate == null) continue;
                foreach (string name in new[] {
                    "GetName", "Cheat_GetNameEnglish", "GetNameEnglish" })
                {
                    try
                    {
                        var method = candidate.GetType().GetMethod(name,
                            Flags, null, Type.EmptyTypes, null);
                        var display = method == null ? null : method.Invoke(candidate, null) as string;
                        if (String.Equals(display, RelicTitle, StringComparison.OrdinalIgnoreCase))
                            return candidate;
                    }
                    catch { }
                }
            }
            return null;
        }

        private static void LogResolved(string source, RelicData data)
        {
            if (loggedResolvedRelic) return;
            loggedResolvedRelic = true;
            Log("Resolved relic via " + source + "; runtime GetID=" +
                data.GetID() + "; display=" + RelicTitle);
        }

        private static bool OwnsThisRelic(SaveManager save)
        {
            var ownedData = ResolveRelic(save);
            if (ownedData == null) return false; // never run without proof of ownership
            var runtimeId = ownedData.GetID();
            bool inspected = false;
            foreach (var method in save.GetType().GetMethods(Flags))
            {
                if ((method.Name != "GetHasRelic" && method.Name != "HasRelic" &&
                     method.Name != "GetHasArtifact") ||
                    method.ReturnType != typeof(bool)) continue;
                var parameters = method.GetParameters();
                if (parameters.Length != 1) continue;
                var values = new List<object>();
                if (parameters[0].ParameterType.IsInstanceOfType(ownedData))
                    values.Add(ownedData);
                else if (parameters[0].ParameterType == typeof(string))
                {
                    values.Add(runtimeId);
                    values.Add(MyPluginInfo.PLUGIN_GUID + "-Relic-" + RelicKey);
                    values.Add(RelicKey);
                }
                foreach (var input in values)
                {
                    try
                    {
                        inspected = true;
                        bool answer = (bool)method.Invoke(save, new[] { input });
                        if (!loggedOwnershipError)
                        {
                            loggedOwnershipError = true;
                            Log("Native ownership probe " + method.Name + "(" +
                                parameters[0].ParameterType.Name + ") with registered " +
                                (input is RelicData ? "RelicData" : "ID") + " => " + answer);
                        }
                        if (!answer) continue;
                        if (!loggedActive)
                        {
                            loggedActive = true;
                            Log("Relic ownership confirmed; replacing ordinary enemy spawns.");
                        }
                        return true;
                    }
                    catch { }
                }
            }
            // Secondary check, just as the working Roster Protocol v4 does:
            // examine the OWNED inventory, never the global game data catalog.
            foreach (string getterName in new[] {
                "GetRelicStates", "GetRelics", "GetCollectedRelics", "GetOwnedRelics",
                "GetArtifacts", "GetRelicList", "GetCollectableRelics",
                "GetAllRelicStates", "GetCollectedRelicStates",
                "GetActiveRelics", "GetActiveRelicStates"
            })
            {
                try
                {
                    var getter = save.GetType().GetMethod(getterName, Flags,
                        null, Type.EmptyTypes, null);
                    if (getter == null) continue;
                    var entries = getter.Invoke(save, null) as IEnumerable;
                    if (entries == null || entries is string) continue;
                    inspected = true;
                    foreach (var item in entries)
                        if (IsOurOwnedRelic(item, runtimeId)) return true;
                }
                catch { }
            }
            if (!inspected && !loggedOwnershipError)
            {
                loggedOwnershipError = true;
                Warn("Could not inspect owned relic state; no enemy changed. " +
                     "Check SaveManager's GetHasRelic API for this game version.");
            }
            return false;
        }

        private static bool IsOurOwnedRelic(object obj, string id)
        {
            if (obj == null) return false;
            var candidate = obj as RelicData;
            if (candidate != null && candidate.GetID() == id) return true;
            foreach (var methodName in new[] {
                "GetRelicData", "GetData", "GetRelic"
            })
            {
                try
                {
                    var getter = obj.GetType().GetMethod(methodName, Flags,
                        null, Type.EmptyTypes, null);
                    var stateRelic = getter == null ? null : getter.Invoke(obj, null) as RelicData;
                    if (stateRelic != null && stateRelic.GetID() == id) return true;
                }
                catch { }
            }
            foreach (var methodName in new[] { "GetRelicDataId", "GetRelicDataID", "GetID" })
            {
                try
                {
                    var getter = obj.GetType().GetMethod(methodName, Flags,
                        null, Type.EmptyTypes, null);
                    if (getter != null && String.Equals(
                        getter.Invoke(obj, null)?.ToString(), id,
                        StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
            }
            return false;
        }
    }
}
