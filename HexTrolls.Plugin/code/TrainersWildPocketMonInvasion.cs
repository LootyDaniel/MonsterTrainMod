// Trainers Wild PocketMon Invasion — isolated enemy-spawn replacement relic.
// Does NOT modify your Trainer roster controller, existing roster relic,
// run deck, or any CharacterData assets. v9 adds enemy-only facing;
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
                    CharacterUI ui = unit.GetCharacterUI();
                    if (ui == null) continue;
                    artReady++;
                    TrainersWildPocketMonInvasion.TryFlipEnemyVisual(unit, ui);
                }
                if (diagnostics < 4 && (matched > 0 || (++diagnostics % 2) == 0))
                {
                    TrainersWildPocketMonInvasion.LogDiagnostic(
                        "v9 facing scan: live CharacterState=" + live.Length +
                        "; enemy PocketMon=" + matched + "; ready CharacterUI=" + artReady);
                    if (matched > 0) diagnostics = 4;
                }
            }
            catch (Exception ex) { TrainersWildPocketMonInvasion.LogFacingErrorOnce(ex); }
        }
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
                    var spriteAnimation = GetComponentInChildren<CharacterUIMeshAnimatedSprite>(true);
                    if (spriteAnimation != null) art = spriteAnimation.transform;
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
            TrainersWildPocketMonInvasion.LogDiagnostic("v9 CreateHeroState hook=" + found);
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
                TrainersWildPocketMonInvasion.LogDiagnostic("v9 CreateHeroState ERROR " + ex);
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
            TrainersWildPocketMonInvasion.LogDiagnostic("v9 SpawnHeroInRoom hook=" + found);
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
                TrainersWildPocketMonInvasion.LogDiagnostic("v9 SpawnHeroInRoom ERROR " + ex);
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
                Log("v9 ENEMY FACING watcher installed (checks spawned art every 0.25 sec)");
            }
            catch (Exception ex) { LogFacingErrorOnce(ex); }
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
                if (marker == null)
                    marker = ui.gameObject.AddComponent<TrainersWildPocketMonFacingVisual>();
                if (marker.SetEnemy(unit) && facingLogs++ < 20)
                    Log("v9 MIRRORED enemy PocketMon LEFT: " +
                        unit.GetSourceCharacterData().GetName() +
                        " using artwork child " + marker.ArtPath);
            }
            catch (Exception ex) { LogFacingErrorOnce(ex); }
        }

        internal static void LogFacingErrorOnce(Exception ex)
        {
            if (facingErrorLogged) return;
            facingErrorLogged = true;
            Warn("v9 enemy-only facing visual skipped: " + ex);
        }

        internal static void RecordSpawn(CharacterData original, CharacterData candidate, string path)
        {
            if (original == null || candidate == null) return;
            if (loggedSwapPaths++ < 35)
                Log("v9 SWAPPED " + path + ": " + original.GetName() +
                    " -> " + candidate.GetName());
        }

        internal static void DiagnoseBattleStart()
        {
            Log("v9 BeginInitialSpawn entered. Checking relic ownership and safe catalog before wave.");
            try
            {
                var game=AllGameManagers.Instance;
                var save=game==null ? null : game.GetSaveManager();
                if (save == null || save.PreviewMode) return;
                bool owned=OwnsThisRelic(save);
                Log("v9 battle-start ownership=" + owned);
                if (owned)
                {
                    Log("v9 battle-start PocketMon catalog=" + EnsureCatalog() +
                        "; enemy-safe forms=" + Candidates.Count);
                    Log("v9 explicit mappings=" + EnemyPools.Count +
                        "; defined pools=" + PocketMonPools.Count +
                        "; unmapped enemies stay original. Cuttlfish excluded.");
                    EnableFacingWatcher();
                }
            }
            catch(Exception ex) { Warn("v9 battle-start probe failed: " + ex); }
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
                    Warn("v9 MAPPED BUT PROTECTED by boss/special safeguards: " +
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
                    Warn("v9 no configured forms for pool '" + poolName +
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
                    Warn("v9 pool '" + poolName + "' has no enemy-safe registered forms. " +
                         "Keeping original enemy " + enemyName);
                return false;
            }
            var chosen = eligible[UnityEngine.Random.Range(0, eligible.Count)];
            replacement = chosen;
            if (replacementsLogged < 35)
            {
                ++replacementsLogged;
                Log("v9 Enemy swap [" + poolName + "]: " + enemyName +
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
                    Log("v9 Loaded " + Candidates.Count + "/39 enemy-safe Trainer PocketMon characters" +
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
