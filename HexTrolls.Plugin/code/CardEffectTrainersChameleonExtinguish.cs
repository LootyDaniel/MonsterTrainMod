using System.Collections;
using TrainworksReloaded.Core.Interfaces;

namespace HexTrolls.Plugin.code
{
    /// <summary>
    /// Chameleon Form 3 Extinguish: deals 5 damage per Electric Charge stack held
    /// by the dying Chameleon to every enemy on its floor. Charges are not consumed.
    /// </summary>
    public sealed class CardEffectTrainersChameleonExtinguish : CardEffectBase
    {
        private const string Charge = "hextrolls.plugin_electric_charge";

        public override bool TestEffect(CardEffectState effect, CardEffectParams args, ICoreGameManagers core)
        {
            // Extinguish happens on death, so the source is intentionally NOT tested for IsAlive.
            return args != null && args.selfTarget != null
                && args.selfTarget.GetStatusEffectStacks(Charge) > 0
                && args.targets != null && args.targets.Count > 0;
        }

        public override IEnumerator ApplyEffect(CardEffectState effect, CardEffectParams args,
            ICoreGameManagers core, ISystemManagers systems)
        {
            if (!TestEffect(effect, args, core)) yield break;
            int stacks = args.selfTarget.GetStatusEffectStacks(Charge);
            int damage = 5 * stacks;
            var combat = core?.GetCombatManager();
            if (combat == null) yield break;
            foreach (CharacterState enemy in args.targets)
            {
                if (enemy == null || !enemy.IsAlive) continue;
                yield return combat.ApplyDamageToTarget(damage, enemy,
                    new CombatManager.ApplyDamageToTargetParameters
                    {
                        damageType = Damage.Type.Default
                    });
            }
        }

        public override PropDescriptions CreateEditorInspectorDescriptions() => new PropDescriptions();
    }
}
