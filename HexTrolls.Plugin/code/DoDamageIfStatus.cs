using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace HexTrolls.Plugin.code
{
    public sealed class CardEffectCheckStatus : CardEffectBase
    {
        public override bool TestEffect(CardEffectState cardEffectState, CardEffectParams cardEffectParams, ICoreGameManagers coreGameManagers)
        {
            var statusEffects = cardEffectState.GetParamStatusEffectStackData();
            if (statusEffects == null || statusEffects.Length == 0)
                return false;

            foreach (var target in cardEffectParams.targets)
            {
                if (target.HasStatusEffect(statusEffects[0].statusId))
                    return true;
            }
            return false;
        }

        public override IEnumerator ApplyEffect(CardEffectState cardEffectState, CardEffectParams cardEffectParams, ICoreGameManagers coreGameManagers, ISystemManagers sysManagers)
        {
            yield break;
        }

        public override PropDescriptions CreateEditorInspectorDescriptions()
        {
            return new PropDescriptions { };
        }
    }
}
