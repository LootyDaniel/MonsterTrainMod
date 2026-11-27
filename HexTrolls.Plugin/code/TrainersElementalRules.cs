// Standalone, testable effectiveness rules; add to the existing HexTrolls project.
// Runtime CombatManager/Harmony integration must call MultiplyDamage; this file does NOT patch combat by itself.
using System;
using System.Collections.Generic;
namespace HexTrolls.Plugin.code {
 public enum TrainersElement { Normal, Fire, Ice, Electric, Water, Plant, Shadow, Light }
 public static class TrainersElementalRules {
  private static readonly Dictionary<TrainersElement,HashSet<TrainersElement>> Strong =
   new Dictionary<TrainersElement,HashSet<TrainersElement>> {
    {TrainersElement.Fire,new HashSet<TrainersElement>{TrainersElement.Plant,TrainersElement.Ice}},
    {TrainersElement.Ice,new HashSet<TrainersElement>{TrainersElement.Water,TrainersElement.Plant}},
    {TrainersElement.Electric,new HashSet<TrainersElement>{TrainersElement.Water,TrainersElement.Normal}},
    {TrainersElement.Water,new HashSet<TrainersElement>{TrainersElement.Fire,TrainersElement.Shadow}},
    {TrainersElement.Plant,new HashSet<TrainersElement>{TrainersElement.Water,TrainersElement.Light}},
    {TrainersElement.Shadow,new HashSet<TrainersElement>{TrainersElement.Fire,TrainersElement.Electric}},
    {TrainersElement.Light,new HashSet<TrainersElement>{TrainersElement.Shadow,TrainersElement.Ice}},
    {TrainersElement.Normal,new HashSet<TrainersElement>{TrainersElement.Light}}
   };
  public static bool IsSuperEffective(TrainersElement damage, TrainersElement defense) {
   return Strong.TryGetValue(damage,out var targets) && targets.Contains(defense);
  }
  public static int MultiplyDamage(int baseDamage, TrainersElement damage, TrainersElement defense) {
   if(baseDamage<=0)return baseDamage;
   long answer = (long)baseDamage * (IsSuperEffective(damage, defense)?2:1);
   return (int)Math.Min(int.MaxValue,answer);
  }
  public static string StatusId(TrainersElement type) => "hextrolls.plugin_trainer_type_"+type.ToString().ToLowerInvariant();
  public static string DamageKeyword(TrainersElement type) => type+" Damage";
  public static bool TryParse(string raw,out TrainersElement type) {
   type=TrainersElement.Normal;
   if(String.IsNullOrWhiteSpace(raw))return false;
   var s=raw.Trim();
   if(s.EndsWith(" Damage",StringComparison.OrdinalIgnoreCase))s=s.Substring(0,s.Length-7);
   return Enum.TryParse<TrainersElement>(s,true,out type);
  }
 }
}
