// Connect to the already-existing hidden Lunarmancer cycle; don't calculate real-world moon phases.
namespace HexTrolls.Plugin.code {
 public interface ITrainersMoonPhaseProvider {
  TrainersMoonPhase GetCurrentHiddenMoonPhase();
 }
 public static class TrainersMoonGemBridge {
  public static string WhelpMoonDestination(ITrainersMoonPhaseProvider existingLunarmancerCycle) {
   if(existingLunarmancerCycle==null)return null;
   return existingLunarmancerCycle.GetCurrentHiddenMoonPhase()==TrainersMoonPhase.NewMoon
    ?"TrainersPocketMonDragonShadowForm1Card":"TrainersPocketMonDragonLightForm1Card";
  }
 }
}
