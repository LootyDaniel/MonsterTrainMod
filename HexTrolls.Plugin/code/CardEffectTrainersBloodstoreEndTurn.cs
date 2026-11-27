using System.Collections;
using TrainworksReloaded.Core.Interfaces;
namespace HexTrolls.Plugin.code
{
    public sealed class CardEffectTrainersBloodstoreEndTurn : CardEffectBase
    {
        public override bool TestEffect(CardEffectState effect, CardEffectParams args, ICoreGameManagers core) =>
            args != null && args.selfTarget != null && args.selfTarget.IsAlive &&
            args.selfTarget.GetStatusEffectStacks(TrainersBloodstoreHeal.StatusId) > 0;
        public override IEnumerator ApplyEffect(CardEffectState effect, CardEffectParams args, ICoreGameManagers core, ISystemManagers systems)
        {
            if (!TestEffect(effect,args,core)) yield break;
            TrainersBloodstoreHeal.Heal(args.selfTarget,5);
            args.selfTarget.RemoveStatusEffect(TrainersBloodstoreHeal.StatusId,1,false);
            yield break;
        }
        public override PropDescriptions CreateEditorInspectorDescriptions() => new PropDescriptions();
    }
}
