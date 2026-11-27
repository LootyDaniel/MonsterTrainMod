// Web is a negative status: minus 20% Attack per stack, root while any stack remains,
// lose one on direct attack/spell damage, and lose one at end of turn.
// Clamp, Attack calculation, and Root ownership are handled by TrainersWebRuntime.
using System;
using System.Collections;
using TrainworksReloaded.Core.Interfaces;
namespace HexTrolls.Plugin.code
{
    public sealed class StatusEffectTrainerWebState : StatusEffectState
    {
        public override int GetEffectMagnitude(int stacks=1) => 20*Math.Min(5,Math.Max(0,stacks));
        public override bool TestTrigger(InputTriggerParams input,OutputTriggerParams output,ICoreGameManagers core)
        {
            CharacterState victim=input.associatedCharacter;
            if(victim==null||victim.GetStatusEffectStacks(GetStatusId())<=0)return false;
            string type=input.damageType.ToString();
            bool direct=type.IndexOf("DirectAttack",StringComparison.OrdinalIgnoreCase)>=0
                ||type.IndexOf("Spell",StringComparison.OrdinalIgnoreCase)>=0
                ||input.damageSourceCard!=null;
            return direct; // Hit can be fully shielded and still count as a direct attack.
        }
        protected override IEnumerator OnTriggered(InputTriggerParams input,OutputTriggerParams output,ICoreGameManagers core)
        {
            CharacterState victim=input.associatedCharacter;
            if(victim!=null&&victim.GetStatusEffectStacks(GetStatusId())>0)
                victim.RemoveStatusEffect(GetStatusId(),1,false);
            yield break;
        }
    }
}
