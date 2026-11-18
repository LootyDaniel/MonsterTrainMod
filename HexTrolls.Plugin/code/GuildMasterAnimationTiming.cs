using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ShinyShoe;
using UnityEngine;

internal static class GuildMasterAnimationTiming
{
    private static readonly Type? Renderer = AccessTools.TypeByName("TrainworksReloaded.Base.Prefab.CharacterUIMeshAnimatedSprite");
    private static readonly FieldInfo? Playing = Renderer == null ? null : AccessTools.Field(Renderer, "_isPlaying");
    private static readonly FieldInfo? Current = Renderer == null ? null : AccessTools.Field(Renderer, "_currentAnimType");
    private sealed class PendingIdle { internal bool Requested; }
    private static readonly ConditionalWeakTable<object, PendingIdle> Pending = new();

    internal static bool IsGuildMaster(object renderer)
    {
        if (renderer is not Component component) return false;
        for (var transform = component.transform; transform != null; transform = transform.parent)
            if (transform.name.IndexOf("@GuildMasterCharacterArt", StringComparison.Ordinal) >= 0)
                return true;
        return false;
    }

    internal static bool Action(CharacterUI.Anim animation) => animation == CharacterUI.Anim.Attack
        || animation == CharacterUI.Anim.HitReact || animation == CharacterUI.Anim.Attack_Spell;

    [HarmonyPatch]
    private static class SpeedPatch
    {
        private static bool Prepare() => Renderer != null && AccessTools.Method(Renderer, "GetAnimSpeedMultiplier") != null;
        private static MethodBase TargetMethod() => AccessTools.Method(Renderer!, "GetAnimSpeedMultiplier");
        private static bool Prefix(object __instance, ref float __result)
        {
            if (!IsGuildMaster(__instance)) return true;
            __result = 1f;
            return false;
        }
    }

    [HarmonyPatch]
    private static class IdlePatch
    {
        private static bool Prepare() => Renderer != null && Playing != null && Current != null
            && AccessTools.Method(Renderer, "PlayAnimInternal") != null;
        private static MethodBase TargetMethod() => AccessTools.Method(Renderer!, "PlayAnimInternal");
        private static bool Prefix(object __instance, CharacterUI.Anim animType)
        {
            if (!IsGuildMaster(__instance)) return true;
            var pending = Pending.GetValue(__instance, _ => new PendingIdle());
            if (animType == CharacterUI.Anim.Idle && (bool)Playing!.GetValue(__instance)!
                && Action((CharacterUI.Anim)Current!.GetValue(__instance)!))
            {
                pending.Requested = true;
                return false;
            }
            // A new action or death always takes precedence over a queued Idle.
            pending.Requested = false;
            return true;
        }
    }

    [HarmonyPatch]
    private static class FinishPatch
    {
        private static bool Prepare() => Renderer != null && Playing != null && Current != null
            && AccessTools.Method(Renderer, "Update") != null;
        private static MethodBase TargetMethod() => AccessTools.Method(Renderer!, "Update");
        private static void Postfix(object __instance)
        {
            if (!Pending.TryGetValue(__instance, out var pending) || !pending.Requested
                || (bool)Playing!.GetValue(__instance)! || !Action((CharacterUI.Anim)Current!.GetValue(__instance)!)) return;
            pending.Requested = false;
            if (__instance is CharacterUIMeshBase mesh) mesh.PlayAnimLoop(CharacterUI.Anim.Idle, 0f);
        }
    }
}
