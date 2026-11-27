using System;
using System.Collections;
using TrainworksReloaded.Core.Interfaces;

namespace HexTrolls.Plugin.code
{
    // Elemental Conversion changes the currently deployed allies only, including
    // neutral and multiple-Type units. It does not edit their permanent deck cards.
    // Their original/evolved identity therefore returns when redeployed or evolved.
    public sealed class CardEffectTrainersElementalConversion : CardEffectBase
    {
        static readonly string[] Types={"normal","fire","ice","electric","water","plant","shadow","light"};
        public override bool TestEffect(CardEffectState e,CardEffectParams a,ICoreGameManagers c) =>
            a!=null&&a.selfTarget!=null&&a.selfTarget.IsAlive&&a.targets!=null;
        public override IEnumerator ApplyEffect(CardEffectState e,CardEffectParams a,ICoreGameManagers c,ISystemManagers s)
        {
            if(!TestEffect(e,a,c))yield break;
            string match=null;
            foreach(string type in Types)
            {
                if(a.selfTarget.GetStatusEffectStacks("hextrolls.plugin_trainer_type_"+type)>0)
                { match=type;break; }
            }
            if(match==null)yield break;
            foreach(CharacterState ally in a.targets)
            {
                if(ally==null||!ally.IsAlive)continue;
                foreach(string type in Types)
                {
                    string sid="hextrolls.plugin_trainer_type_"+type;
                    string did="hextrolls.plugin_trainer_damage_"+type;
                    int ts=ally.GetStatusEffectStacks(sid),ds=ally.GetStatusEffectStacks(did);
                    if(ts>0)ally.RemoveStatusEffect(sid,ts,false);
                    if(ds>0)ally.RemoveStatusEffect(did,ds,false);
                }
                ally.AddStatusEffect("hextrolls.plugin_trainer_type_"+match,1);
                ally.AddStatusEffect("hextrolls.plugin_trainer_damage_"+match,1);
            }
            yield break;
        }
        public override PropDescriptions CreateEditorInspectorDescriptions()=>new PropDescriptions();
    }
}
