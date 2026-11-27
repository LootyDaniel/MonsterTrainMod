using System.Collections;
using TrainworksReloaded.Core.Interfaces;
namespace HexTrolls.Plugin.code
{
    /// <summary>On Resolve: add exactly one Electric Charge if caster still has Stealth.</summary>
    public sealed class CardEffectTrainersChameleonChargeIfStealthed : CardEffectBase
    {
        private const string Charge = "hextrolls.plugin_electric_charge";
        public override bool TestEffect(CardEffectState effect, CardEffectParams args, ICoreGameManagers core)
        {
            return args != null && args.selfTarget != null && args.selfTarget.IsAlive &&
                   args.selfTarget.GetStatusEffectStacks("stealth") > 0;
        }
        public override IEnumerator ApplyEffect(CardEffectState effect, CardEffectParams args,
            ICoreGameManagers core, ISystemManagers systems)
        {
            if (TestEffect(effect,args,core)) args.selfTarget.AddStatusEffect(Charge, 1);
            yield break;
        }
        public override PropDescriptions CreateEditorInspectorDescriptions() => new PropDescriptions();
    }
}
