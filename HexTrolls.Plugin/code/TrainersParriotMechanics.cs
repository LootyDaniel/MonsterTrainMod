// Game-agnostic core of Parriot's two finalized abilities.
// The existing HexTrolls plugin must implement the port using its actual CharacterState/CardState/save hooks.
using System;
using System.Collections.Generic;
namespace HexTrolls.Plugin.code {
 public interface ITrainersElementUnit {
  bool IsParriot {get;}
  bool IsAlive {get;}
  string PermanentCardInstanceKey {get;}
  int Floor {get;}
  TrainersElement CurrentType {get;set;}
  TrainersElement NormalAttackDamage {get;set;}
 }
 public interface ITrainersElementPersistence {
  // Persist BOTH the type and auto-attack keyword on the exact card instance, not just combat CharacterState.
  void SaveElementForCard(string permanentCardInstanceKey,TrainersElement type,TrainersElement attackType);
 }
 public static class TrainersParriotMechanics {
  public const int ElementalConversionCooldown=3;
  public static int OnIncomingAttackBeforeDamage(ITrainersElementUnit defender,
   TrainersElement incomingAttackElement,int baseDamage,ITrainersElementPersistence save) {
   if(defender==null)throw new ArgumentNullException(nameof(defender));
   if(defender.IsParriot)ReplaceAndSave(defender,incomingAttackElement,save);
   return TrainersElementalRules.MultiplyDamage(baseDamage,incomingAttackElement,defender.CurrentType);
  }
  public static void ReplaceAndSave(ITrainersElementUnit unit,TrainersElement element,ITrainersElementPersistence save) {
   if(unit==null||!unit.IsAlive)return;
   unit.CurrentType=element;
   unit.NormalAttackDamage=element;
   if(!String.IsNullOrEmpty(unit.PermanentCardInstanceKey))
    save?.SaveElementForCard(unit.PermanentCardInstanceKey,element,element);
  }
  public static int ConvertAlliesOnFloor(ITrainersElementUnit parriot,IEnumerable<ITrainersElementUnit> allies,
    ITrainersElementPersistence save) {
   if(parriot==null||!parriot.IsParriot||!parriot.IsAlive)throw new ArgumentException("A living Parriot is required");
   int count=0;
   foreach(var ally in allies) {
    if(ally==null||!ally.IsAlive||ally.Floor!=parriot.Floor)continue;
    ReplaceAndSave(ally,parriot.CurrentType,save);count++;
   }
   return count;
  }
 }
}
