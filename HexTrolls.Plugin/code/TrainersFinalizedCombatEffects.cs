using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using TrainworksReloaded.Core.Interfaces;

namespace HexTrolls.Plugin.code
{
    internal static class TrainersFinalized
    {
        internal const string Leech = "hextrolls.plugin_trainer_leech";
        internal const string Snap = "hextrolls.plugin_trainer_snap";
        internal const string Tesla = "hextrolls.plugin_trainer_tesla_charge";
        internal const string Walker = "hextrolls.plugin_trainer_shadow_walker";
        internal const string Freeze = "hextrolls.plugin_trainer_freeze";
        internal const string Breaker = "hextrolls.plugin_trainer_freeze_breaker";
        internal const string Payback = "hextrolls.plugin_trainer_payback";
        internal const string Stone = "hextrolls.plugin_trainer_stoneform";
        internal const string Level = "hextrolls.plugin_level";
        internal static int CurrentHealth(CharacterState c)
        {
            if (c == null) return 0;
            var t = c.GetType();
            foreach (string name in new [] {"GetHP", "GetCurrentHP", "GetHealth", "GetCurrentHealth"})
            {
                var m = t.GetMethod(name, BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (m != null) try { return Convert.ToInt32(m.Invoke(c,null)); } catch { }
            }
            return c.GetMaxHP();
        }
        internal static IEnumerator DealDamage(ICoreGameManagers core, CharacterState target, int damage)
        {
            if (core == null || target == null || !target.IsAlive || damage <= 0) yield break;
            var combat = core.GetCombatManager();
            if (combat == null) yield break;
            yield return combat.ApplyDamageToTarget(damage, target,
                new CombatManager.ApplyDamageToTargetParameters { damageType = Damage.Type.Default });
        }
        internal static void Status(CharacterState c, string name, int count)
        {
            if (c != null && c.IsAlive && count > 0) c.AddStatusEffect(name, count);
        }
    }

    internal static class TrainersLeechOwnership
    {
        private sealed class Ownership { public CharacterState Caster; }
        private static readonly ConditionalWeakTable<CharacterState,Ownership> Owners = new ConditionalWeakTable<CharacterState,Ownership>();
        internal static void Set(CharacterState victim, CharacterState caster)
        {
            if (victim == null) return;
            Owners.GetOrCreateValue(victim).Caster = caster;
        }
        internal static CharacterState Get(CharacterState victim)
        {
            Ownership ownership;
            return victim != null && Owners.TryGetValue(victim,out ownership) ? ownership.Caster : null;
        }
    }

    // Selected enemy on the same floor. Reapplying replaces the healing beneficiary,
    // while the stack count remains additive. Damage is performed by the Leech status.
    public sealed class CardEffectTrainersSeeding : CardEffectBase
    {
        public override bool TestEffect(CardEffectState e, CardEffectParams a, ICoreGameManagers c) =>
            a != null && a.selfTarget != null && a.selfTarget.IsAlive && a.targets != null && a.targets.Count > 0;
        public override IEnumerator ApplyEffect(CardEffectState e, CardEffectParams a, ICoreGameManagers c, ISystemManagers s)
        {
            if (!TestEffect(e,a,c)) yield break;
            int count = e != null ? e.GetParamInt() : 0;
            if (count <= 0) yield break;
            foreach (CharacterState target in a.targets)
            {
                if (target == null || !target.IsAlive) continue;
                TrainersLeechOwnership.Set(target,a.selfTarget);
                target.AddStatusEffect(TrainersFinalized.Leech,count);
                break;
            }
            yield break;
        }
        public override PropDescriptions CreateEditorInspectorDescriptions() => new PropDescriptions();
    }

    public sealed class CardEffectTrainersElectricCoil : CardEffectBase
    {
        public override bool TestEffect(CardEffectState e, CardEffectParams a, ICoreGameManagers c) =>
            a != null && a.selfTarget != null && a.selfTarget.IsAlive && a.targets != null && a.targets.Count > 0;
        public override IEnumerator ApplyEffect(CardEffectState e, CardEffectParams a, ICoreGameManagers c, ISystemManagers s)
        {
            if (!TestEffect(e,a,c)) yield break;
            foreach (CharacterState target in a.targets)
            {
                if (target == null || !target.IsAlive) continue;
                yield return TrainersFinalized.DealDamage(c,target,e == null ? 15 : e.GetParamInt());
                // Do not Root/Daze anyone when the selected target died from the first hit.
                if (target.IsAlive)
                {
                    target.AddStatusEffect("rooted",1);
                    target.AddStatusEffect("dazed",1);
                    a.selfTarget.AddStatusEffect("rooted",1);
                    a.selfTarget.AddStatusEffect("dazed",1);
                    TrainersCoilState.Set(a.selfTarget,true);
                }
                else TrainersCoilState.Set(a.selfTarget,false);
                break;
            }
        }
        public override PropDescriptions CreateEditorInspectorDescriptions() => new PropDescriptions();
    }
    internal static class TrainersCoilState
    {
        private sealed class Flag { public bool OriginalTargetSurvived; }
        private static readonly ConditionalWeakTable<CharacterState,Flag> Flags = new ConditionalWeakTable<CharacterState,Flag>();
        internal static void Set(CharacterState c,bool yes) { if(c!=null)Flags.GetOrCreateValue(c).OriginalTargetSurvived=yes; }
        internal static bool Get(CharacterState c) { Flag f; return c!=null && Flags.TryGetValue(c,out f)&&f.OriginalTargetSurvived; }
    }
    public sealed class CardEffectTrainersCoilResolve : CardEffectBase
    {
        public override bool TestEffect(CardEffectState e, CardEffectParams a, ICoreGameManagers c) =>
            a != null && a.selfTarget != null && a.selfTarget.IsAlive && a.selfTarget.GetStatusEffectStacks("cooldown")>0
            && TrainersCoilState.Get(a.selfTarget) && a.targets != null;
        public override IEnumerator ApplyEffect(CardEffectState e, CardEffectParams a, ICoreGameManagers c, ISystemManagers s)
        {
            if (!TestEffect(e,a,c)) yield break;
            foreach (CharacterState target in a.targets)
                if (target!=null && target.IsAlive) yield return TrainersFinalized.DealDamage(c,target,15);
        }
        public override PropDescriptions CreateEditorInspectorDescriptions() => new PropDescriptions();
    }
    public sealed class CardEffectTrainersTeslaResolve : CardEffectBase
    {
        public override bool TestEffect(CardEffectState e, CardEffectParams a, ICoreGameManagers c) =>
            a!=null && a.selfTarget!=null && a.selfTarget.IsAlive && a.targets!=null;
        public override IEnumerator ApplyEffect(CardEffectState e, CardEffectParams a, ICoreGameManagers c, ISystemManagers s)
        {
            if (!TestEffect(e,a,c)) yield break;
            int charges=a.selfTarget.GetStatusEffectStacks(TrainersFinalized.Tesla);
            foreach (CharacterState enemy in a.targets)
                if (enemy!=null&&enemy.IsAlive) yield return TrainersFinalized.DealDamage(c,enemy,charges);
        }
        public override PropDescriptions CreateEditorInspectorDescriptions() => new PropDescriptions();
    }
    public sealed class CardEffectTrainersSnap : CardEffectBase
    {
        public override bool TestEffect(CardEffectState e, CardEffectParams a, ICoreGameManagers c) =>
            a!=null&&a.selfTarget!=null&&a.selfTarget.IsAlive&&a.selfTarget.GetStatusEffectStacks(TrainersFinalized.Snap)>0&&a.targets!=null;
        public override IEnumerator ApplyEffect(CardEffectState e,CardEffectParams a,ICoreGameManagers c,ISystemManagers s)
        {
            if (!TestEffect(e,a,c)) yield break;
            foreach(CharacterState enemy in a.targets)
            {
                if (enemy==null||!enemy.IsAlive||enemy.GetMaxHP()>=TrainersFinalized.CurrentHealth(a.selfTarget))continue;
                int healing=enemy.GetMaxHP();
                // A lethal damage call preserves the game's combat/death processing.
                // Damage shields/Armor and other damage prevention require in-game checking.
                yield return TrainersFinalizedHP.Kill(c,enemy);
                if (!enemy.IsAlive)
                {
                    a.selfTarget.RemoveStatusEffect(TrainersFinalized.Snap,1,false);
                    TrainersBloodstoreHeal.Heal(a.selfTarget,healing);
                }
                break;
            }
        }
        public override PropDescriptions CreateEditorInspectorDescriptions() => new PropDescriptions();
    }
    public sealed class CardEffectTrainersShadowVeil : CardEffectBase
    {
        public override bool TestEffect(CardEffectState e, CardEffectParams a, ICoreGameManagers c) => a!=null&&a.targets!=null;
        public override IEnumerator ApplyEffect(CardEffectState e,CardEffectParams a,ICoreGameManagers c,ISystemManagers s)
        {
            if (!TestEffect(e,a,c)) yield break;
            int stealth=e==null?1:e.GetParamInt();
            foreach (CharacterState ally in a.targets)
            {
                if (ally==null||!ally.IsAlive||ally.GetStatusEffectStacks(TrainersFinalized.Walker)<=0)continue;
                ally.AddStatusEffect("stealth",stealth);
                if(stealth==2)ally.AddStatusEffect("burst",3);
            }
            yield break;
        }
        public override PropDescriptions CreateEditorInspectorDescriptions() => new PropDescriptions();
    }


    internal static class TrainersFinalizedParameters
    {
        internal static string ParamStr(object effect)
        {
            if(effect==null)return "";
            const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
            try{
                var m=effect.GetType().GetMethod("GetParamStr",flags,null,Type.EmptyTypes,null);
                return m==null?"":m.Invoke(effect,null) as string ?? "";
            }catch{return "";}
        }
    }

    public sealed class CardEffectTrainersWaterCleanse : CardEffectBase
    {
        public override bool TestEffect(CardEffectState e,CardEffectParams a,ICoreGameManagers c)=>a!=null&&a.targets!=null;
        private static readonly string[] KnownNegative={"poison","debuff","rooted","dazed","pyregel","muted","silenced","scorch",TrainersFinalized.Freeze,TrainersFinalized.Leech,"hextrolls.plugin_web"};
        public override IEnumerator ApplyEffect(CardEffectState e,CardEffectParams a,ICoreGameManagers c,ISystemManagers s)
        {
            if (!TestEffect(e,a,c))yield break;
            foreach(CharacterState ally in a.targets)
            {
                if(ally==null||!ally.IsAlive)continue;
                foreach(string id in KnownNegative)
                {
                    int amount=ally.GetStatusEffectStacks(id);
                    if(amount>0)ally.RemoveStatusEffect(id,amount,false);
                }
            }
            yield break;
        }
        public override PropDescriptions CreateEditorInspectorDescriptions()=>new PropDescriptions();
    }

    // Summon and floor-movement dispatch both use this same effect function.
    public sealed class CardEffectTrainersFloorEntry : CardEffectBase
    {
        public override bool TestEffect(CardEffectState e, CardEffectParams a, ICoreGameManagers c) => a!=null&&a.targets!=null;
        public override IEnumerator ApplyEffect(CardEffectState e,CardEffectParams a,ICoreGameManagers c,ISystemManagers s)
        {
            if (!TestEffect(e,a,c)) yield break;
            string type=TrainersFinalizedParameters.ParamStr(e);
            foreach(CharacterState enemy in a.targets)
            {
                if(enemy==null||!enemy.IsAlive)continue;
                yield return TrainersFinalized.DealDamage(c,enemy,15);
                if (!enemy.IsAlive)continue;
                switch (type)
                {
                    case "fire": enemy.AddStatusEffect("pyregel",5);break;
                    case "ice": enemy.AddStatusEffect(TrainersFinalized.Freeze,2);break;
                    case "electric":break; // Daze is front-only; applied to the first survivor below.
                    case "plant": enemy.AddStatusEffect("rooted",1);break;
                    case "shadow": enemy.AddStatusEffect("decay",5);break;
                    case "light": enemy.AddStatusEffect("muted",2);break;
                }
            }
            if(type=="electric")
            {
                foreach (CharacterState enemy in a.targets)
                    if(enemy!=null&&enemy.IsAlive){enemy.AddStatusEffect("dazed",1);break;}
            }
            // Water cleansing of allies is handled separately; this effect only receives enemies.
        }
        public override PropDescriptions CreateEditorInspectorDescriptions() => new PropDescriptions();
    }
    public sealed class CardEffectTrainersFirearyEntry : CardEffectBase
    {
        public override bool TestEffect(CardEffectState e, CardEffectParams a, ICoreGameManagers c) =>
            a!=null&&a.selfTarget!=null&&a.selfTarget.IsAlive&&a.targets!=null;
        public override IEnumerator ApplyEffect(CardEffectState e,CardEffectParams a,ICoreGameManagers c,ISystemManagers s)
        {
            if (!TestEffect(e,a,c)) yield break;
            int attacks=1+Math.Max(0,a.selfTarget.GetStatusEffectStacks("multistrike"));
            for(int attack=0;attack<attacks;attack++)
            {
                // Each hit selects the current front enemy, not the original target forever.
                CharacterState enemy=null;
                foreach(CharacterState possible in a.targets)
                    if(possible!=null&&possible.IsAlive){enemy=possible;break;}
                if(enemy==null)break;
                yield return TrainersFinalized.DealDamage(c,enemy,a.selfTarget.GetAttackDamage());
                if(enemy.IsAlive)enemy.AddStatusEffect("pyregel",3);
            }
        }
        public override PropDescriptions CreateEditorInspectorDescriptions() => new PropDescriptions();
    }
}
