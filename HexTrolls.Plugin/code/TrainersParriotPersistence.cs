using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TrainworksReloaded.Core;
using TrainworksReloaded.Core.Interfaces;

namespace HexTrolls.Plugin.code
{
    // Stores the chosen Type on the exact permanent Parriot card as a card-upgrade
    // marker, not on an ephemeral battlefield unit. The spawn trigger restores it.
    public static class TrainersParriotPersistence
    {
        const string Marker="HexTrolls.Plugin-Upgrade-TrainersParriotElement";
        const string Type="hextrolls.plugin_trainer_type_";
        const string Damage="hextrolls.plugin_trainer_damage_";
        internal static readonly string[] Elements={"normal","fire","ice","electric","water","plant","shadow","light"};

        internal static bool IsParriot(CharacterState unit)
        {
            if(unit==null)return false;
            string name=unit.GetSourceCharacterData()?.name ?? "";
            return name.IndexOf("TrainersPocketMonParriotForm1Character",StringComparison.OrdinalIgnoreCase)>=0;
        }
        internal static string DamageType(CharacterState unit)
        {
            if(unit==null)return null;
            foreach(string element in Elements)
                if(unit.GetStatusEffectStacks(Damage+element)>0)return element;
            return null;
        }
        internal static string TypeOf(CharacterState unit)
        {
            if(unit==null)return null;
            foreach(string element in Elements)
                if(unit.GetStatusEffectStacks(Type+element)>0)return element;
            return null;
        }
        internal static void SwitchStatuses(CharacterState unit,string element)
        {
            if(unit==null||String.IsNullOrEmpty(element))return;
            foreach(string old in Elements)
            {
                foreach(string prefix in new[]{Type,Damage})
                {
                    int n=unit.GetStatusEffectStacks(prefix+old);
                    if(n>0)unit.RemoveStatusEffect(prefix+old,n,false);
                }
            }
            unit.AddStatusEffect(Type+element,1);
            unit.AddStatusEffect(Damage+element,1);
        }
        private static CardState ExactSavedCard(CharacterState unit,ICoreGameManagers core)
        {
            var spawner=unit?.GetSpawnerCard();
            var deck=core?.GetSaveManager()?.GetDeckState();
            if(spawner==null||deck==null)return null;
            var same=deck.Where(card=>card!=null&&Object.ReferenceEquals(card,spawner)).ToList();
            if(same.Count==1)return same[0];
            var byData=deck.Where(card=>card!=null&&String.Equals(card.GetCardDataID(),spawner.GetCardDataID(),StringComparison.Ordinal)).ToList();
            return byData.Count==1?byData[0]:null;
        }
        private static string ElementFromCard(CardState card)
        {
            var upgrades=card?.GetCardStateModifiers()?.GetCardUpgrades();
            if(upgrades==null)return null;
            foreach(var upgrade in upgrades)
            {
                string id=upgrade?.GetCardUpgradeDataId();
                if(id==null||!id.StartsWith(Marker,StringComparison.OrdinalIgnoreCase))continue;
                string suffix=id.Substring(Marker.Length);
                foreach(string element in Elements)
                    if(String.Equals(suffix,element,StringComparison.OrdinalIgnoreCase))return element;
            }
            return null;
        }
        internal static void Restore(CharacterState unit,ICoreGameManagers core)
        {
            if(!IsParriot(unit))return;
            string element=ElementFromCard(ExactSavedCard(unit,core));
            if(element!=null)SwitchStatuses(unit,element);
        }
        internal static void Adapt(CharacterState unit,string element,ICoreGameManagers core)
        {
            if(!IsParriot(unit)||String.IsNullOrEmpty(element))return;
            SwitchStatuses(unit,element);
            CardState saved=ExactSavedCard(unit,core);
            if(saved==null)
            {
                Plugin.Logger.LogWarning("[TrainersParriot] Cannot identify exact deck card; adaptive Type applied for battle only.");
                return;
            }
            var mods=saved.GetCardStateModifiers();
            var upgrades=mods?.GetCardUpgrades();
            if(upgrades==null)return;
            CardUpgradeData template=null;
            try
            {
                var registry=Railend.GetContainer().GetInstance<IRegister<CardUpgradeData>>();
                registry.TryGetValue(Marker+char.ToUpperInvariant(element[0])+element.Substring(1),out template);
            }
            catch(Exception ex){Plugin.Logger.LogWarning("[TrainersParriot] Marker lookup failed: "+ex.Message);}
            if(template==null)return;
            for(int i=upgrades.Count-1;i>=0;i--)
                if((upgrades[i]?.GetCardUpgradeDataId()??"").StartsWith(Marker,StringComparison.OrdinalIgnoreCase))
                    upgrades.RemoveAt(i);
            var marker=new CardUpgradeState();
            marker.Setup(template,false,false);
            mods.AddUpgrade(marker);
            saved.UpdateCardBodyText();
        }
    }
    public sealed class CardEffectTrainersParriotRestore : CardEffectBase
    {
        public override bool TestEffect(CardEffectState e,CardEffectParams a,ICoreGameManagers c)=>a!=null&&a.selfTarget!=null;
        public override IEnumerator ApplyEffect(CardEffectState e,CardEffectParams a,ICoreGameManagers c,ISystemManagers s)
        {
            if(TestEffect(e,a,c))TrainersParriotPersistence.Restore(a.selfTarget,c);
            yield break;
        }
        public override PropDescriptions CreateEditorInspectorDescriptions()=>new PropDescriptions();
    }
}
