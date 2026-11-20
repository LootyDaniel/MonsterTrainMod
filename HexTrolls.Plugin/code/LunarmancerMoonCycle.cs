using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HexTrolls.Plugin.code
{
    /// <summary>
    /// Lunarmancer's *private* moon phase. It does not turn on Luna Coven's Moon HUD.
    /// When an externally active native phase can be read, that takes precedence.
    /// </summary>
    public static class LunarmancerMoonCycle
    {
        private const string ShockwaveId = "hextrolls.plugin_shockwave";
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly HashSet<CharacterState> OurShockwave = new HashSet<CharacterState>();
        private static bool _fullMoon;
        private static bool _started;
        private static bool _wasMonsterTurn;
        private static bool _enemyPhaseObserved;
        private static int _turns;
        private static bool _warnedRemoval;
        private static int _syncDepth;
        private static bool? _lastNativeSample;
        private static bool _nativeHasAdvanced;

        public static bool IsFullMoon
        {
            get
            {
                bool? native = ReadNativeMoonPhase();
                return native ?? _fullMoon;
            }
        }
        public static bool IsNewMoon => !IsFullMoon;

        public static void Reset()
        {
            // Clear our own bookkeeping; never tamper with another source of Shockwave.
            OurShockwave.Clear();
            _fullMoon = false;           // Start on New Moon, Full Moon next player turn.
            _started = false;
            _wasMonsterTurn = false;
            _enemyPhaseObserved = false;
            _turns = 0;
            _lastNativeSample = null;
            _nativeHasAdvanced = false;
            Plugin.Logger.LogInfo("[Lunarmancer] New battle: private cycle begins at New Moon (HUD unchanged)");
        }

        public static void ObservePhase(CombatManager.Phase current)
        {
            // Don't confuse intermediate/animation phases with a completed turn.
            bool isMonsterTurn = current == CombatManager.Phase.MonsterTurn;
            string phaseName = current.ToString();
            if (phaseName.IndexOf("HeroTurn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                phaseName.IndexOf("EnemyTurn", StringComparison.OrdinalIgnoreCase) >= 0)
                _enemyPhaseObserved = true;

            bool beginning = isMonsterTurn && (!_started || (_enemyPhaseObserved && !_wasMonsterTurn));
            if (beginning)
            {
                if (_started) { _fullMoon = !_fullMoon; ++_turns; }
                else { _started = true; _turns = 1; }
                _enemyPhaseObserved = false;
                bool nativeFound = ReadNativeMoonPhase().HasValue;
                bool full = IsFullMoon;
                Plugin.Logger.LogInfo($"[Lunarmancer] Turn {_turns}: {(full ? "Full Moon" : "New Moon")}" +
                    (nativeFound ? " (native phase)" : " (private cycle; Luna HUD unchanged)"));
                SyncChampionShockwave(full);
            }

            // Also catches champions summoned midturn and native phase-changing spells.
            if (isMonsterTurn) SyncChampionShockwave(IsFullMoon);
            _wasMonsterTurn = isMonsterTurn;
        }

        private static void SyncChampionShockwave(bool fullMoon)
        {
            if (_syncDepth != 0) return;
            _syncDepth++;
            try
            {
                var roomManager = AllGameManagers.Instance?.GetRoomManager();
                if (roomManager == null) return;
                // Standard train floors: 0..2. Errors on unavailable indices are ignored.
                for (int index = 0; index < 3; ++index)
                {
                    RoomState room;
                    try { room = roomManager.GetRoom(index); }
                    catch { continue; }
                    if (room == null) continue;
                    var units = new List<CharacterState>();
                    room.AddCharactersToList(units, Team.Type.Monsters);
                    foreach (var unit in units)
                    {
                        if (unit == null || !unit.IsAlive || !IsLunarmancer(unit)) continue;
                        if (fullMoon)
                        {
                            if (!unit.HasStatusEffect(ShockwaveId))
                            {
                                unit.AddStatusEffect(ShockwaveId, 1);
                                OurShockwave.Add(unit);
                                Plugin.Logger.LogInfo("[Lunarmancer] Added Full Moon Shockwave");
                            }
                        }
                        else if (OurShockwave.Contains(unit))
                        {
                            if (RemoveOneShockwave(unit))
                            {
                                OurShockwave.Remove(unit);
                                Plugin.Logger.LogInfo("[Lunarmancer] Removed Full Moon Shockwave");
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning("[Lunarmancer] Shockwave sync: " + ex.Message); }
            finally { _syncDepth--; }
        }

        private static bool IsLunarmancer(CharacterState unit)
        {
            // Only this champion path receives Moonshock in our JSON upgrade tree.
            return unit.GetStatusEffectStacks("hextrolls.plugin_moonshock") > 0;
        }

        private static bool RemoveOneShockwave(CharacterState unit)
        {
            // These overloads vary between game builds. Only remove the stack we placed.
            foreach (var method in typeof(CharacterState).GetMethods(Flags))
            {
                if (method.Name != "RemoveStatusEffect" && method.Name != "RemoveStatusEffectStacks" &&
                    method.Name != "AdjustStatusEffectStacks") continue;
                var p = method.GetParameters();
                if (p.Length < 1 || p[0].ParameterType != typeof(string)) continue;
                if (p.Skip(1).Any(x => !x.IsOptional && x.ParameterType != typeof(int))) continue;
                try
                {
                    var args = new object[p.Length];
                    args[0] = ShockwaveId;
                    for (int i = 1; i < p.Length; i++)
                        args[i] = p[i].ParameterType == typeof(int)
                            ? (object)(method.Name.StartsWith("Adjust", StringComparison.Ordinal) ? -1 : 1)
                            : p[i].DefaultValue;
                    object result = method.Invoke(unit, args);
                    if (result is IEnumerator coroutine)
                        AllGameManagers.Instance?.GetCombatManager()?.StartCoroutine(coroutine);
                    return true;
                }
                catch (Exception ex) { Plugin.Logger.LogWarning("[Lunarmancer] Could not remove Shockwave: " + ex.Message); }
            }
            if (!_warnedRemoval)
            {
                _warnedRemoval = true;
                string available = string.Join(" | ", typeof(CharacterState).GetMethods(Flags)
                    .Where(m => m.Name.IndexOf("Status", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                (m.Name.IndexOf("Remove", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 m.Name.IndexOf("Adjust", StringComparison.OrdinalIgnoreCase) >= 0))
                    .Select(m => m.ToString()));
                Plugin.Logger.LogWarning("[Lunarmancer] No compatible status-removal method found: " + available);
            }
            return false;
        }

        /// <summary>
        /// Read native phase only when it is already available; do not enable Luna Coven's mechanic.
        /// Unknown APIs fail closed to the independent private cycle.
        /// </summary>
        private static bool? ReadNativeMoonPhase()
        {
            try
            {
                object managers = AllGameManagers.Instance;
                if (managers == null) return null;
                object[] roots = {
                    managers,
                    AllGameManagers.Instance.GetCombatManager(),
                    AllGameManagers.Instance.GetRoomManager(),
                    AllGameManagers.Instance.GetSaveManager()
                };
                foreach (object root in roots)
                {
                    if (root == null) continue;
                    bool? phase = ReadMoonProperty(root);
                    if (!phase.HasValue) continue;
                    bool? enabled = ReadEnabledFlag(root);
                    if (enabled == false) return null;
                    if (enabled == true) return phase;

                    // Some game builds expose a default New Moon even when the lunar
                    // mechanic is DISABLED. Only trust a no-enable-flag getter once
                    // its phase actually changes during this battle.
                    if (_lastNativeSample.HasValue && _lastNativeSample.Value != phase.Value)
                        _nativeHasAdvanced = true;
                    _lastNativeSample = phase;
                    return _nativeHasAdvanced ? phase : null;
                }
            }
            catch { /* Optional native phase support */ }
            return null;
        }

        private static bool? ReadEnabledFlag(object obj)
        {
            string[] names = { "IsMoonPhaseEnabled", "GetIsMoonPhaseEnabled",
                "GetMoonPhaseEnabled", "IsMoonPhaseActive", "GetIsMoonPhaseActive",
                "GetMoonPhaseActive", "HasMoonPhase", "GetHasMoonPhase" };
            foreach (string name in names)
            {
                try
                {
                    var method = obj.GetType().GetMethods(Flags).FirstOrDefault(m =>
                        m.Name == name && m.GetParameters().Length == 0 && m.ReturnType == typeof(bool));
                    if (method != null) return (bool)method.Invoke(obj, null);
                    var property = obj.GetType().GetProperty(name, Flags);
                    if (property != null && property.PropertyType == typeof(bool) && property.GetIndexParameters().Length == 0)
                        return (bool)property.GetValue(obj, null);
                }
                catch { }
            }
            return null;
        }

        private static bool? ReadMoonProperty(object obj)
        {
            Type type = obj.GetType();
            // Explicitly named members only: avoid arbitrary fields with unrelated 'moon' text.
            string[] names = { "GetCurrentMoonPhase", "GetMoonPhase", "CurrentMoonPhase", "MoonPhase", "GetMoonPhaseState" };
            foreach (string name in names)
            {
                try
                {
                    object value = null;
                    var method = type.GetMethods(Flags).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == 0);
                    if (method != null) value = method.Invoke(obj, null);
                    else
                    {
                        var prop = type.GetProperty(name, Flags);
                        if (prop != null && prop.GetIndexParameters().Length == 0) value = prop.GetValue(obj, null);
                    }
                    if (value == null) continue;
                    string token = value.ToString().Replace("_", "").Replace(" ", "").ToLowerInvariant();
                    if (token == "fullmoon" || token == "moonlit" || token == "lit") return true;
                    if (token == "newmoon" || token == "moonshade" || token == "shade") return false;
                }
                catch { /* probe next member */ }
            }
            return null;
        }
    }

    [HarmonyPatch(typeof(CombatManager), "StartCombat")]
    internal static class LunarmancerStartCombatPatch
    {
        private static void Postfix(CombatManager __instance)
        {
            LunarmancerMoonCycle.Reset();
            __instance.StartCoroutine(PollPhase(__instance));
        }

        private static IEnumerator PollPhase(CombatManager combat)
        {
            // No HUD activation and no patch on the high-frequency GetCombatPhase method.
            yield return null;
            while (combat != null && combat.GetIsRunningCombat())
            {
                LunarmancerMoonCycle.ObservePhase(combat.GetCombatPhase());
                yield return new WaitForSeconds(0.2f);
            }
        }
    }
}
