using System.Collections;
using TrainworksReloaded.Core.Interfaces;

namespace HexTrolls.Plugin.code
{
    /// <summary>
    /// Frost Skin Resolve runs AFTER the existing native Armor +5 effect.
    /// Reads the holder's CURRENT Armor and applies floor(Armor / 5) Frostbite
    /// to every living enemy on the same floor. Never touches allies.
    /// </summary>
    public sealed class CardEffectTrainersFrostSkinResolve : CardEffectBase
    {
        public override bool TestEffect(CardEffectState effect, CardEffectParams args, ICoreGameManagers core)
        {
            return args != null && args.selfTarget != null && args.selfTarget.IsAlive
                && args.targets != null && args.targets.Count > 0;
        }

        public override IEnumerator ApplyEffect(CardEffectState effect, CardEffectParams args,
            ICoreGameManagers core, ISystemManagers systems)
        {
            if (!TestEffect(effect, args, core)) yield break;
            int armor = args.selfTarget.GetStatusEffectStacks("armor");
            int frostbite = armor / 5;
            if (frostbite <= 0) yield break;
            foreach (CharacterState enemy in args.targets)
            {
                if (enemy != null && enemy.IsAlive)
                    enemy.AddStatusEffect("poison", frostbite);
            }
            yield break;
        }

        public override PropDescriptions CreateEditorInspectorDescriptions() => new PropDescriptions();
    }
}
