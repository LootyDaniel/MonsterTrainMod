using System.Collections;
using TrainworksReloaded.Core.Interfaces;
namespace HexTrolls.Plugin.code
{
    public sealed class CardEffectTrainersBloodbag : CardEffectBase
    {
        public override bool TestEffect(CardEffectState effect, CardEffectParams args, ICoreGameManagers core)
        {
            return args != null && args.selfTarget != null && args.selfTarget.IsAlive &&
                   args.selfTarget.GetStatusEffectStacks(TrainersBloodstoreHeal.StatusId) > 0 &&
                   args.targets != null && args.targets.Count > 0;
        }
        public override IEnumerator ApplyEffect(CardEffectState effect, CardEffectParams args, ICoreGameManagers core, ISystemManagers systems)
        {
            if (!TestEffect(effect,args,core)) yield break;
            CharacterState ally = null;
            foreach (CharacterState target in args.targets) if (target != null && target.IsAlive) { ally = target; break; }
            if (ally == null) yield break;
            int stacks = args.selfTarget.GetStatusEffectStacks(TrainersBloodstoreHeal.StatusId);
            if (stacks <= 0) yield break;
            args.selfTarget.RemoveStatusEffect(TrainersBloodstoreHeal.StatusId,stacks,false);
            TrainersBloodstoreHeal.Heal(ally,stacks*3);
            yield break;
        }
        public override PropDescriptions CreateEditorInspectorDescriptions() => new PropDescriptions();
    }
}
