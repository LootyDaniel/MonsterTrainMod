// Register this class in the existing plugin assembly before loading TrainersElementalTypes.json.
// The 8 elemental statuses are informational; game combat code must call TrainersElementalRules.
using System.Collections;
using TrainworksReloaded.Core.Interfaces;
namespace HexTrolls.Plugin.code {
 public sealed class StatusEffectTrainerElementTypeState : StatusEffectState {
  public override bool TestTrigger(InputTriggerParams input,OutputTriggerParams output,ICoreGameManagers core) { return false; }
  protected override IEnumerator OnTriggered(InputTriggerParams input,OutputTriggerParams output,ICoreGameManagers core) { yield break; }
 }
}
