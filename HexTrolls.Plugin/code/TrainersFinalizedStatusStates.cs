using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TrainworksReloaded.Core.Interfaces;

namespace HexTrolls.Plugin.code
{
    public sealed class StatusEffectTrainersSnapState : StatusEffectState
    {
        public override bool TestTrigger(InputTriggerParams input, OutputTriggerParams output, ICoreGameManagers core)
        {
            CharacterState owner=GetAssociatedCharacter();
            return owner!=null && owner.IsAlive && owner.GetStatusEffectStacks(GetStatusId())>0
                && input.attacker!=null && input.attacker.IsAlive
                && input.damageType.ToString().IndexOf("DirectAttack",StringComparison.OrdinalIgnoreCase)>=0
                && input.attacker.GetMaxHP() < TrainersFinalized.CurrentHealth(owner);
        }
        protected override IEnumerator OnTriggered(InputTriggerParams input, OutputTriggerParams output, ICoreGameManagers core)
        {
            if (!TestTrigger(input,output,core)) yield break;
            CharacterState owner=GetAssociatedCharacter();
            CharacterState attacker=input.attacker;
            int amount=attacker.GetMaxHP();
            yield return TrainersFinalizedHP.Kill(core,attacker);
            if(!attacker.IsAlive)
            {
                owner.RemoveStatusEffect(GetStatusId(),1,false);
                TrainersBloodstoreHeal.Heal(owner,amount);
            }
        }
    }
    // The status itself, not Teslasaurus's unit trigger, owns Resolve damage.
    // Revenge on each form adds 1/2/3 charges; every existing stack deals
    // one damage to each enemy on that floor each Resolve without decaying.
    public sealed class StatusEffectTrainersTeslaChargeState : StatusEffectState
    {
        public override bool TestTrigger(InputTriggerParams input, OutputTriggerParams output, ICoreGameManagers core)
        {
            CharacterState owner = GetAssociatedCharacter();
            return core != null && owner != null && owner.IsAlive &&
                owner.GetCurrentRoom() != null && owner.GetStatusEffectStacks(GetStatusId()) > 0;
        }

        protected override IEnumerator OnTriggered(InputTriggerParams input, OutputTriggerParams output, ICoreGameManagers core)
        {
            if (!TestTrigger(input, output, core)) yield break;
            CharacterState owner = GetAssociatedCharacter();
            int charges = owner.GetStatusEffectStacks(GetStatusId());
            RoomState floor = owner.GetCurrentRoom();
            if (floor == null || charges <= 0) yield break;

            // Snapshot only opponents on this floor. The snapshot remains safe
            // when damage removes characters during the Resolve iteration.
            Team.Type opposingTeam = owner.GetTeamType() == Team.Type.Monsters
                ? Team.Type.Heroes : Team.Type.Monsters;
            var enemies = new List<CharacterState>();
            floor.AddCharactersToList(enemies, opposingTeam);
            foreach (CharacterState enemy in enemies)
            {
                if (enemy != null && enemy.IsAlive)
                    yield return TrainersFinalized.DealDamage(core, enemy, charges);
            }
            // Do not remove Tesla Charge stacks here: it accumulates all battle.
        }

        public override int GetEffectMagnitude(int stacks = 1)
        {
            return Math.Max(0, stacks); // One Resolve damage per Tesla Charge.
        }
    }
    public sealed class StatusEffectTrainersFreezeState : StatusEffectState {}
    public sealed class StatusEffectTrainersFreezeBreakerState : StatusEffectState {}
    public sealed class StatusEffectTrainersShadowWalkerState : StatusEffectState {}
    public sealed class StatusEffectTrainersSwiftState : StatusEffectState
    {
        public override bool TestTrigger(InputTriggerParams input,OutputTriggerParams output,ICoreGameManagers core)
        {
            CharacterState c=GetAssociatedCharacter();
            return c!=null&&c.IsAlive&&c.GetStatusEffectStacks(GetStatusId())>0;
        }
        protected override IEnumerator OnTriggered(InputTriggerParams input,OutputTriggerParams output,ICoreGameManagers core)
        {
            CharacterState c=GetAssociatedCharacter();
            if(c!=null&&c.IsAlive&&c.GetStatusEffectStacks("ambush")<=0)c.AddStatusEffect("ambush",1);
            yield break;
        }
    }
    // Actual end-of-floor-combat stage: one tick each turn after floor combat.
    // Damage comes first; healing is based on HP actually lost, not the nominal stacks.
    public sealed class StatusEffectTrainersLeechState : StatusEffectState
    {
        public override bool TestTrigger(InputTriggerParams input,OutputTriggerParams output,ICoreGameManagers core)
        {
            CharacterState victim=GetAssociatedCharacter();
            return victim!=null&&victim.IsAlive&&victim.GetStatusEffectStacks(GetStatusId())>0;
        }
        protected override IEnumerator OnTriggered(InputTriggerParams input,OutputTriggerParams output,ICoreGameManagers core)
        {
            CharacterState victim=GetAssociatedCharacter();
            if(victim==null||!victim.IsAlive)yield break;
            int stacks=victim.GetStatusEffectStacks(GetStatusId());
            if(stacks<=0)yield break;
            int before=TrainersFinalized.CurrentHealth(victim);
            // Armor bypass: direct HP loss needs confirmation against the installed game build.
            // Applying normal damage here would incorrectly allow Armor to absorb Leech.
            int reduced;
            if(stacks>=before)
            {
                // Take the final HP through native combat to dispatch death correctly.
                // Remove defenses for this final one-HP hit only: Leech bypasses Armor.
                yield return TrainersFinalizedHP.Kill(core,victim);
                reduced=victim.IsAlive?TrainersFinalized.CurrentHealth(victim):0;
            }
            else reduced=TrainersFinalizedHP.SetHP(victim,before-stacks);
            int actual=Math.Max(0,before-reduced);
            CharacterState caster=TrainersLeechOwnership.Get(victim);
            if(actual>0&&caster!=null&&caster.IsAlive)TrainersBloodstoreHeal.Heal(caster,actual);
            yield break;
        }
    }
    internal static class TrainersFinalizedHP
    {
        internal static IEnumerator Kill(ICoreGameManagers core,CharacterState target)
        {
            if(target==null||!target.IsAlive)yield break;
            foreach(string shield in new[]{"armor","damage shield","spell shield"})
            {
                int amount=target.GetStatusEffectStacks(shield);
                if(amount>0)target.RemoveStatusEffect(shield,amount,false);
            }
            SetHP(target,1);
            yield return core.GetCombatManager().ApplyDamageToTarget(1,target,
                new CombatManager.ApplyDamageToTargetParameters { damageType=Damage.Type.Poison });
        }
        internal static int SetHP(CharacterState c,int wanted)
        {
            if(c==null)return 0;
            var t=c.GetType();
            var f=System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
            var m=t.GetMethod("SetHealth",f,null,new[]{typeof(int),typeof(int)},null);
            if(m!=null)try{m.Invoke(c,new object[]{wanted,c.GetMaxHP()});return TrainersFinalized.CurrentHealth(c);}catch{}
            // Fail safely rather than silently damaging Armor via ApplyDamageToTarget.
            return TrainersFinalized.CurrentHealth(c);
        }
    }
    public sealed class StatusEffectTrainersPaybackState : StatusEffectState
    {
        public override bool TestTrigger(InputTriggerParams input,OutputTriggerParams output,ICoreGameManagers core)
        {
            CharacterState c=GetAssociatedCharacter();
            return c!=null&&c.GetStatusEffectStacks(GetStatusId())>0&&input.attacker==c;
        }
        protected override IEnumerator OnTriggered(InputTriggerParams input,OutputTriggerParams output,ICoreGameManagers core)
        {
            CharacterState c=GetAssociatedCharacter();
            if(c!=null&&c.GetStatusEffectStacks(GetStatusId())>0)
                c.RemoveStatusEffect(GetStatusId(),c.GetStatusEffectStacks(GetStatusId()),false);
            yield break;
        }
    }
    public sealed class StatusEffectTrainersStoneformState : StatusEffectState
    {
        private sealed class UseMarker{public bool Used;}
        private static readonly ConditionalWeakTable<CharacterState,UseMarker> Used=new ConditionalWeakTable<CharacterState,UseMarker>();
        public override bool TestTrigger(InputTriggerParams input,OutputTriggerParams output,ICoreGameManagers core)
        {
            CharacterState c=GetAssociatedCharacter();
            if(c==null||!c.IsAlive||c.GetStatusEffectStacks(GetStatusId())<=0)return false;
            if(TrainersFinalized.CurrentHealth(c)*5>=c.GetMaxHP())return false;
            return !Used.GetOrCreateValue(c).Used;
        }
        protected override IEnumerator OnTriggered(InputTriggerParams input,OutputTriggerParams output,ICoreGameManagers core)
        {
            CharacterState c=GetAssociatedCharacter();
            if(c==null||!c.IsAlive)yield break;
            var use=Used.GetOrCreateValue(c);
            if(use.Used||TrainersFinalized.CurrentHealth(c)*5>=c.GetMaxHP())yield break;
            use.Used=true;
            int level=Math.Max(1,c.GetStatusEffectStacks(TrainersFinalized.Level));
            c.AddStatusEffect("armor",5*level);
            c.AddStatusEffect("rooted",1);
            yield break;
        }
    }
}
