// Testable route resolver. No unsafe unit-destruction calls are performed here.
using System;
using System.Collections.Generic;
namespace HexTrolls.Plugin.code {
 public enum TrainersGem { Fire, Ice, Electric, Water, Plant, Moon }
 public enum TrainersMoonPhase { NewMoon, FullMoon }
 public interface ITrainersEvolutionTransaction {
  // Return false if we cannot resolve the precise run-deck card instance for the deployed unit.
  bool HasExactDeckCard(string cardInstanceKey);
  bool IsOnFloor(string cardInstanceKey);
  // Must preserve position, owner, occupied capacity, and living unit; no Extinguish/Harvest/death triggers.
  // The implementation must be atomic and roll back both sides if either step fails.
  bool TryReplaceDeckAndBattleUnitOneForOne(string cardInstanceKey,string destinationCardId,
   bool preserveLevelExp,bool preserveUpgrades,bool preserveRoster,bool suppressDeathTriggers);
 }
 public static class TrainersEvolutionMechanics {
  public const string BaseWhelp="TrainersPocketMonDragonWhelpForm1Card";
  public const string BaseParriot="TrainersPocketMonParriotForm1Card";
  // This is set ONLY after user defines Parriot's evolved creature card. Do not guess it.
  public static string ParriotEvolutionCardId;
  public static readonly IDictionary<string,string> Next=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  static TrainersEvolutionMechanics() {
   Next["TrainersPocketMonBloodWormForm1Card"]="TrainersPocketMonBloodWormForm2Card";
   Next["TrainersPocketMonChameleonForm1Card"]="TrainersPocketMonChameleonForm2Card";
   Next["TrainersPocketMonChameleonForm2Card"]="TrainersPocketMonChameleonForm3Card";
   Next["TrainersPocketMonCuttlfishForm1Card"]="TrainersPocketMonCuttlfishForm2Card";
   Next["TrainersPocketMonCuttlfishForm2Card"]="TrainersPocketMonCuttlfishForm3Card";
   Next["TrainersPocketMonDragonMothForm1Card"]="TrainersPocketMonDragonMothForm2Card";
   Next["TrainersPocketMonDragonMothForm2Card"]="TrainersPocketMonDragonMothForm3Card";
   Next["TrainersPocketMonForgeSnailForm1Card"]="TrainersPocketMonForgeSnailForm2Card";
   Next["TrainersPocketMonForgeSnailForm2Card"]="TrainersPocketMonForgeSnailForm3Card";
   Next["TrainersPocketMonFrossetForm1Card"]="TrainersPocketMonFrossetForm2Card";
   Next["TrainersPocketMonFrossetForm2Card"]="TrainersPocketMonFrossetForm3Card";
   Next["TrainersPocketMonGarhoundForm1Card"]="TrainersPocketMonGarhoundForm2Card";
   Next["TrainersPocketMonGarhoundForm2Card"]="TrainersPocketMonGarhoundForm3Card";
   Next["TrainersPocketMonHyenaForm1Card"]="TrainersPocketMonHyenaForm2Card";
   Next["TrainersPocketMonJourmancoilForm1Card"]="TrainersPocketMonJourmancoilForm2Card";
   Next["TrainersPocketMonLeafshrewForm1Card"]="TrainersPocketMonLeafshrewForm2Card";
   Next["TrainersPocketMonMagmothForm1Card"]="TrainersPocketMonMagmothForm2Card";
   Next["TrainersPocketMonManshrimpForm1Card"]="TrainersPocketMonManshrimpForm2Card";
   Next["TrainersPocketMonManshrimpForm2Card"]="TrainersPocketMonManshrimpForm3Card";
   Next["TrainersPocketMonSnapsaurusForm1Card"]="TrainersPocketMonSnapsaurusForm2Card";
   Next["TrainersPocketMonSnapsaurusForm2Card"]="TrainersPocketMonSnapsaurusForm3Card";
   Next["TrainersPocketMonTeslasaurusForm1Card"]="TrainersPocketMonTeslasaurusForm2Card";
   Next["TrainersPocketMonTeslasaurusForm2Card"]="TrainersPocketMonTeslasaurusForm3Card";
   Next["TrainersPocketMonUmbrabeakForm1Card"]="TrainersPocketMonUmbrabeakForm2Card";
   Next["TrainersPocketMonUmbrabeakForm2Card"]="TrainersPocketMonUmbrabeakForm3Card";
   Next["TrainersPocketMonVampirantForm1Card"]="TrainersPocketMonVampirantForm2Card";
   Next["TrainersPocketMonVampirantForm2Card"]="TrainersPocketMonVampirantForm3Card";
   Next["TrainersPocketMonYetiForm1Card"]="TrainersPocketMonYetiForm2Card";
   Next["TrainersPocketMonYetiForm2Card"]="TrainersPocketMonYetiForm3Card";
  }
  public static void RegisterRoute(string from,string to) { if(!String.IsNullOrEmpty(from)&&!String.IsNullOrEmpty(to))Next[from]=to; }
  public static string ChooseDestination(string sourceCardId,TrainersElement currentType,
   TrainersGem gem,TrainersMoonPhase moon) {
   if(String.Equals(sourceCardId,BaseWhelp,StringComparison.OrdinalIgnoreCase)) {
    if(gem==TrainersGem.Moon)return moon==TrainersMoonPhase.NewMoon
     ?"TrainersPocketMonDragonShadowForm1Card":"TrainersPocketMonDragonLightForm1Card";
    return "TrainersPocketMonDragon"+gem+"Form1Card";
   }
   if(gem==TrainersGem.Moon)return null; // Only whelp Moon branch defined so far.
   if(!String.Equals(gem.ToString(),currentType.ToString(),StringComparison.OrdinalIgnoreCase))return null;
   if(String.Equals(sourceCardId,BaseParriot,StringComparison.OrdinalIgnoreCase))return ParriotEvolutionCardId;
   return Next.TryGetValue(sourceCardId,out var destination)?destination:null;
  }
  public static bool TryEvolve(string cardInstanceKey,string sourceCardId,TrainersElement currentType,
   TrainersGem gem,TrainersMoonPhase moon,ITrainersEvolutionTransaction transaction) {
   if(String.IsNullOrEmpty(cardInstanceKey)||transaction==null||!transaction.HasExactDeckCard(cardInstanceKey))return false;
   var target=ChooseDestination(sourceCardId,currentType,gem,moon);
   if(String.IsNullOrEmpty(target))return false;
   return transaction.TryReplaceDeckAndBattleUnitOneForOne(cardInstanceKey,target,true,true,true,true);
  }
 }
}
