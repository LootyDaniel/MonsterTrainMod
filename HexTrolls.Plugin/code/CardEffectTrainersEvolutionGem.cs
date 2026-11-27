// Gem CardEffect registered by JSON. Dispatches to the v4 native evolution bridge.
// Run only on a test save until its clone/despawn transaction is verified in-game.
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using TrainworksReloaded.Base;
using TrainworksReloaded.Core.Interfaces;
namespace HexTrolls.Plugin.code {
 public sealed class CardEffectTrainersEvolutionGem : CardEffectBase {
  public static Func<CharacterState,string,CardEffectState,CardEffectParams,ICoreGameManagers,ISystemManagers,IEnumerator> ExecuteInPlace = TrainersEvolutionGemBridge.Apply;
  public override bool CanApplyInPreviewMode => false;
  public override bool CanPlayAfterBossDead => false;
  public override PropDescriptions CreateEditorInspectorDescriptions()=>new PropDescriptions{};
  public override IEnumerator ApplyEffect(CardEffectState effect,CardEffectParams parameters,
    ICoreGameManagers core,ISystemManagers systems) {
   if(core==null||core.GetSaveManager()==null||core.GetSaveManager().PreviewMode)yield break;
   CharacterState unit=parameters?.targets?.FirstOrDefault(x=>x!=null && x.GetTeamType()==Team.Type.Monsters);
   if(unit==null||!unit.IsAlive||unit.IsDestroyed)yield break;
   string gem=GetParamStr(effect);
   if(String.IsNullOrWhiteSpace(gem)) {
    Plugin.Logger.LogError("[EvolutionGem v4] Gem effect has no param_str value.");
    yield break;
   }
   if(ExecuteInPlace==null) {
    Plugin.Logger.LogError("[EvolutionGem v4] Runtime bridge not installed.");
    yield break;
   }
   var action=ExecuteInPlace(unit,gem,effect,parameters,core,systems);
   if(action!=null)yield return action;
  }
  static string GetParamStr(object instance) {
   const BindingFlags flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
   foreach(var obj in new[]{instance,Invoke(instance,"GetSourceCardEffectData")}) {
    var val=Invoke(obj,"GetParamStr") as string;
    if(!String.IsNullOrEmpty(val))return val;
   }
   return null;
  }
  static object Invoke(object target,string name) {
   if(target==null)return null;
   try{return target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,
    null,Type.EmptyTypes,null)?.Invoke(target,null);}catch{return null;}
  }
 }
}
