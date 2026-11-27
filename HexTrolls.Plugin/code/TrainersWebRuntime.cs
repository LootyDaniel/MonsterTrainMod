// DragonMoth Web: 20% Attack per stack, native Rooted, max 5 stacks, decay 1/turn.
// Patches are installed ONCE through Plugin.Awake(), not PatchAll(), to avoid double reductions.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace HexTrolls.Plugin.code
{
    public static class TrainersWebRuntime
    {
        public const string WebStatusId = "hextrolls.plugin_web";
        private const string RootStatusId = "rooted";
        private static bool installed;
        private sealed class WebRootOwnership { public bool AddedRoot; }
        private static readonly ConditionalWeakTable<CharacterState, WebRootOwnership> RootOwners =
            new ConditionalWeakTable<CharacterState, WebRootOwnership>();

        public static void Install()
        {
            if (installed) return;
            installed = true;
            var harmony = new Harmony("HexTrolls.Plugin.TrainersWebRuntime.v1");
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                var attack = typeof(CharacterState).GetMethods(flags)
                    .FirstOrDefault(m => m.Name == "GetAttackDamage" && m.GetParameters().Length == 0 && m.ReturnType == typeof(int));
                if (attack != null)
                {
                    harmony.Patch(attack, postfix: new HarmonyMethod(typeof(TrainersWebRuntime), nameof(AfterAttackRead)));
                    Plugin.Logger.LogInfo("[TrainersWeb] Attack reduction installed: CharacterState.GetAttackDamage().");
                }
                else Plugin.Logger.LogWarning("[TrainersWeb] No parameterless GetAttackDamage method; Web will not reduce Attack until game API is adapted.");

                foreach (var method in typeof(CharacterState).GetMethods(flags).Where(m => m.Name == "AddStatusEffect" &&
                         m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(string)))
                    harmony.Patch(method, postfix: new HarmonyMethod(typeof(TrainersWebRuntime), nameof(AfterStatusAdded)));

                foreach (var method in typeof(CharacterState).GetMethods(flags).Where(m => m.Name == "RemoveStatusEffect" &&
                         m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(string)))
                    harmony.Patch(method, postfix: new HarmonyMethod(typeof(TrainersWebRuntime), nameof(AfterStatusRemoved)));

                Plugin.Logger.LogInfo("[TrainersWeb] Web max-5 stack clamp and rooted-status cleanup installed.");
            }
            catch (Exception ex) { Plugin.Logger.LogError("[TrainersWeb] Failed to install runtime: " + ex); }
        }

        private static void AfterAttackRead(CharacterState __instance, ref int __result)
        {
            if (__instance == null || __result <= 0) return;
            int stacks = Math.Min(5, Math.Max(0, __instance.GetStatusEffectStacks(WebStatusId)));
            if (stacks > 0) __result = (int)Math.Floor((double)__result * (5 - stacks) / 5);
        }

        // Native ability CardEffectAddStatusEffect can apply Web. We enforce the five-stack cap
        // and add a single native Rooted stack for the lifetime of that Web.
        private static void AfterStatusAdded(CharacterState __instance, object[] __args)
        {
            if (__instance == null || __args == null || __args.Length == 0 || !String.Equals(__args[0] as string, WebStatusId, StringComparison.OrdinalIgnoreCase)) return;
            int stacks = __instance.GetStatusEffectStacks(WebStatusId);
            if (stacks > 5)
            {
                __instance.RemoveStatusEffect(WebStatusId, stacks - 5, false);
                stacks = __instance.GetStatusEffectStacks(WebStatusId);
            }
            if (stacks <= 0) return;
            var marker = RootOwners.GetOrCreateValue(__instance);
            if (!marker.AddedRoot && __instance.GetStatusEffectStacks(RootStatusId) <= 0)
            {
                __instance.AddStatusEffect(RootStatusId, 1, (CharacterState)null, false, false);
                marker.AddedRoot = __instance.GetStatusEffectStacks(RootStatusId) > 0;
            }
        }

        // Handles both OnHit (all stacks) and native remove_stack_at_end_of_turn (one stack).
        // Never remove another effect's Rooted stack if Web did not add it.
        private static void AfterStatusRemoved(CharacterState __instance, object[] __args)
        {
            if (__instance == null || __args == null || __args.Length == 0) return;
            string removedId = __args[0] as string;
            // Rooted itself may lose its native one-turn stack. Web must keep its target
            // rooted until all Web stacks are gone, including across several turns.
            if (String.Equals(removedId, RootStatusId, StringComparison.OrdinalIgnoreCase))
            {
                if (__instance.GetStatusEffectStacks(WebStatusId) > 0 &&
                    __instance.GetStatusEffectStacks(RootStatusId) <= 0)
                {
                    __instance.AddStatusEffect(RootStatusId, 1, (CharacterState)null, false, false);
                    RootOwners.GetOrCreateValue(__instance).AddedRoot =
                        __instance.GetStatusEffectStacks(RootStatusId) > 0;
                }
                return;
            }
            if (!String.Equals(removedId, WebStatusId, StringComparison.OrdinalIgnoreCase)) return;
            if (__instance.GetStatusEffectStacks(WebStatusId) > 0) return;
            WebRootOwnership marker;
            if (!RootOwners.TryGetValue(__instance, out marker) || !marker.AddedRoot) return;
            marker.AddedRoot = false;
            if (__instance.GetStatusEffectStacks(RootStatusId) > 0)
                __instance.RemoveStatusEffect(RootStatusId, 1, false);
        }
    }
}
