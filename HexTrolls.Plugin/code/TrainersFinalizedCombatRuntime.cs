using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TrainworksReloaded.Core;
using TrainworksReloaded.Core.Interfaces;

namespace HexTrolls.Plugin.code
{
    // The previous status implementations were only tooltip markers.
    // This runtime supplies the damage computations that marker statuses cannot perform.
    // Harmony targets are found by signature to avoid assuming a single game build.
    public static class TrainersFinalizedCombatRuntime
    {
        private static bool installed;
        public static void Install()
        {
            if(installed)return;
            installed=true;
            var harmony=new Harmony("HexTrolls.Plugin.TrainersFinalizedCombat.v1");
            const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
            try
            {
                var attack=typeof(CharacterState).GetMethods(flags).FirstOrDefault(m=>
                    m.Name=="GetAttackDamage"&&m.ReturnType==typeof(int)&&m.GetParameters().Length==0);
                if(attack!=null)
                    harmony.Patch(attack,postfix:new HarmonyMethod(typeof(TrainersFinalizedCombatRuntime),nameof(AfterAttackRead)));
                else Plugin.Logger.LogWarning("[TrainersFinalized] Missing attack-read hook; Payback/Shadow Walker are inactive.");

                var damage=typeof(CombatManager).GetMethods(flags).FirstOrDefault(m=>
                    m.Name=="ApplyDamageToTarget" && typeof(IEnumerator).IsAssignableFrom(m.ReturnType)
                    && m.GetParameters().Length>=3
                    && m.GetParameters()[0].ParameterType==typeof(int)
                    && m.GetParameters()[1].ParameterType==typeof(CharacterState)
                    && m.GetParameters()[2].ParameterType==typeof(CombatManager.ApplyDamageToTargetParameters));
                if(damage!=null)
                {
                    harmony.Patch(damage,
                        prefix:new HarmonyMethod(typeof(TrainersFinalizedCombatRuntime),nameof(BeforeDirectDamage)),
                        postfix:new HarmonyMethod(typeof(TrainersFinalizedCombatRuntime),nameof(AfterDirectDamage)));
                    Plugin.Logger.LogInfo("[TrainersFinalized] Freeze direct-damage patch installed.");
                }
                else Plugin.Logger.LogWarning("[TrainersFinalized] Missing ApplyDamageToTarget signature; Freeze damage bonus is inactive.");
            }
            catch(Exception e){Plugin.Logger.LogError("[TrainersFinalized] Unable to install mechanics runtime: "+e);}
        }

        private static void AfterAttackRead(CharacterState __instance, ref int __result)
        {
            if(__instance==null||__result<=0)return;
            int payback=__instance.GetStatusEffectStacks(TrainersFinalized.Payback);
            if(payback>0)__result=(int)Math.Min(int.MaxValue,Math.Floor(__result*(1.0+payback*0.5)));
            if(__instance.GetStatusEffectStacks(TrainersFinalized.Walker)>0
                && __instance.GetStatusEffectStacks("stealth")>0)
                __result=(int)Math.Min(int.MaxValue,Math.Floor(__result*1.2));
        }

        private static void BeforeDirectDamage(ref int __0, CharacterState __1,
            CombatManager.ApplyDamageToTargetParameters __2,out bool __state)
        {
            __state=false;
            if(__1==null)return;
            string damageType=__2.damageType.ToString();
            // Only attacks and spells, not status damage or triggered non-spell damage.
            bool normalAttack=damageType.IndexOf("DirectAttack",StringComparison.OrdinalIgnoreCase)>=0;
            bool directSpell=damageType.IndexOf("Spell",StringComparison.OrdinalIgnoreCase)>=0 || __2.playedCard!=null;
            if(!normalAttack&&!directSpell)return;
            CharacterState attacker=__2.selfTarget;
            if(normalAttack&&attacker!=null)
            {
                string incoming=TrainersParriotPersistence.DamageType(attacker);
                if(incoming!=null)
                {
                    // Adaptive Element changes Parriot BEFORE the incoming hit is calculated.
                    if(TrainersParriotPersistence.IsParriot(__1))
                    {
                        try
                        {
                            var core=Railend.GetContainer().GetInstance<ICoreGameManagers>();
                            TrainersParriotPersistence.Adapt(__1,incoming,core);
                        }
                        catch(Exception ex){Plugin.Logger.LogWarning("[TrainersParriot] Adaptive persist failed: "+ex.Message);}
                    }
                    string defender=TrainersParriotPersistence.TypeOf(__1);
                    TrainersElement attackElement, defenseElement;
                    if(defender!=null&&TrainersElementalRules.TryParse(incoming,out attackElement)
                        &&TrainersElementalRules.TryParse(defender,out defenseElement)
                        &&TrainersElementalRules.IsSuperEffective(attackElement,defenseElement))
                        __0=(int)Math.Min(int.MaxValue, (long)__0*2);
                }
            }
            int freeze=__1.GetStatusEffectStacks(TrainersFinalized.Freeze);
            if(freeze<=0||__0<=0)return;
            bool breaker=normalAttack&&attacker!=null&&attacker.GetStatusEffectStacks(TrainersFinalized.Breaker)>0;
            double perStack=breaker?0.4:0.2;
            __0=(int)Math.Min(int.MaxValue,Math.Floor(__0*(1+freeze*perStack)));
            __state=true;
        }
        private static void AfterDirectDamage(ref IEnumerator __result, CharacterState __1, bool __state)
        {
            if(__state && __result!=null)__result=AfterDirectDamageCompletes(__result,__1);
        }
        private static IEnumerator AfterDirectDamageCompletes(IEnumerator original,CharacterState victim)
        {
            while(original.MoveNext())yield return original.Current;
            if(victim!=null && victim.IsAlive && victim.GetStatusEffectStacks(TrainersFinalized.Freeze)>0)
                victim.RemoveStatusEffect(TrainersFinalized.Freeze,1,false);
        }
    }
}
