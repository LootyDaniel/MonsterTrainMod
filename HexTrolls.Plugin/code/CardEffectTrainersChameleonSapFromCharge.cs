using System.Collections;
using TrainworksReloaded.Core.Interfaces;
namespace HexTrolls.Plugin.code
{
    /// <summary>Activated Charged Sap: spend exactly one charge, then Sap the front enemy.</summary>
    public sealed class CardEffectTrainersChameleonSapFromCharge : CardEffectBase
    {
        private const string Charge = "hextrolls.plugin_electric_charge";
        public override bool TestEffect(CardEffectState effect, CardEffectParams args, ICoreGameManagers core)
        {
            return args != null && args.selfTarget != null && args.selfTarget.IsAlive &&
                   args.selfTarget.GetStatusEffectStacks(Charge) >= 1 && args.targets != null && args.targets.Count > 0;
        }
        public override IEnumerator ApplyEffect(CardEffectState effect, CardEffectParams args, ICoreGameManagers core, ISystemManagers systems)
        {
            if (!TestEffect(effect,args,core)) yield break;
            CharacterState enemy = null;
            foreach (CharacterState target in args.targets) if (target != null && target.IsAlive) { enemy = target; break; }
            if (enemy == null) yield break;
            args.selfTarget.RemoveStatusEffect(Charge, 1, false);
            enemy.AddStatusEffect("debuff", 2);
            yield break;
        }
        public override PropDescriptions CreateEditorInspectorDescriptions() => new PropDescriptions();
    }
}
