using System;
using System.Collections;
using TrainworksReloaded.Base;
using TrainworksReloaded.Core;
using TrainworksReloaded.Core.Interfaces;

namespace HexTrolls.Plugin.code
{
    // These two *champion-only* statuses are also the concise card keywords.
    // Stacks are granted through the existing champion upgrade tree.
    public sealed class StatusEffectMoonrendState : StatusEffectState
    {
        public override bool TestTrigger(InputTriggerParams inputTriggerParams,
            OutputTriggerParams outputTriggerParams, ICoreGameManagers coreGameManagers)
        {
            CharacterState attacker = inputTriggerParams.attacker;
            CharacterState victim = inputTriggerParams.attacked;
            return attacker != null && victim != null && attacker.IsAlive && victim.IsAlive
                && ReferenceEquals(attacker, GetAssociatedCharacter())
                && LunarmancerMoonCycle.IsNewMoon
                && victim.HasStatusEffect("hextrolls.plugin_mark")
                && attacker.GetStatusEffectStacks(GetStatusId()) > 0;
        }

        protected override IEnumerator OnTriggered(InputTriggerParams inputTriggerParams,
            OutputTriggerParams outputTriggerParams, ICoreGameManagers coreGameManagers)
        {
            CharacterState attacker = inputTriggerParams.attacker;
            CharacterState victim = inputTriggerParams.attacked;
            if (attacker == null || victim == null || !victim.IsAlive ||
                !LunarmancerMoonCycle.IsNewMoon ||
                !victim.HasStatusEffect("hextrolls.plugin_mark"))
                yield break;

            int damage = attacker.GetStatusEffectStacks(GetStatusId());
            if (damage <= 0) yield break;

            // Bonus post-strike damage. Does NOT permanently increase the champion's Attack.
            // Using ApplyDamageToTarget follows the game's damage pipeline.
            var combat = coreGameManagers.GetCombatManager();
            if (combat == null) yield break;
            Plugin.Logger.LogInfo($"[Lunarmancer] Moonrend: +{damage} vs Marked target '{victim.GetName()}'");
            yield return combat.ApplyDamageToTarget(damage, victim,
                new CombatManager.ApplyDamageToTargetParameters { damageType = Damage.Type.Default });
        }

        public override int GetEffectMagnitude(int stacks = 1) => stacks;
    }

    public sealed class StatusEffectMoonshockState : StatusEffectState
    {
        public override bool TestTrigger(InputTriggerParams inputTriggerParams,
            OutputTriggerParams outputTriggerParams, ICoreGameManagers coreGameManagers)
        {
            CharacterState attacker = inputTriggerParams.attacker;
            CharacterState victim = inputTriggerParams.attacked;
            return attacker != null && victim != null && attacker.IsAlive && victim.IsAlive
                && ReferenceEquals(attacker, GetAssociatedCharacter())
                && LunarmancerMoonCycle.IsFullMoon
                && attacker.GetStatusEffectStacks(GetStatusId()) > 0;
        }

        protected override IEnumerator OnTriggered(InputTriggerParams inputTriggerParams,
            OutputTriggerParams outputTriggerParams, ICoreGameManagers coreGameManagers)
        {
            CharacterState attacker = inputTriggerParams.attacker;
            CharacterState victim = inputTriggerParams.attacked;
            if (attacker == null || victim == null || !victim.IsAlive || !LunarmancerMoonCycle.IsFullMoon)
                yield break;
            int count = attacker.GetStatusEffectStacks(GetStatusId());
            if (count > 0)
            {
                victim.AddStatusEffect("pyregel", count);
                Plugin.Logger.LogInfo($"[Lunarmancer] Moonshock: applied Pyregel {count} to '{victim.GetName()}'");
            }
            yield break;
        }

        public override int GetEffectMagnitude(int stacks = 1) => stacks;
    }
}
