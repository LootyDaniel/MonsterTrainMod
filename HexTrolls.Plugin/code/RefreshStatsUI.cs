using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI.Extensions;

namespace HexTrolls.Plugin.code
{
    public class RefreshStatsUI : CardEffectBase
    {
        public override bool TestEffect(CardEffectState cardEffectState, CardEffectParams cardEffectParams, ICoreGameManagers coreGameManagers)
        {
            return true;
        }

        public override IEnumerator ApplyEffect(CardEffectState cardEffectState, CardEffectParams cardEffectParams, ICoreGameManagers coreGameManagers, ISystemManagers sysManagers)
        {
            Debug.Log("Attempting "+ cardEffectParams.targets[0].GetUnitAbilityCooldown());
            if (cardEffectParams != null)
            {
                Debug.Log("Refreshed");
                //cardEffectParams.targets[0].RefreshStatsVisibility();
                cardEffectParams.targets[0].GetUnitAbilityCardState().ResetPlayCount();
            }
            Debug.Log("Result " + cardEffectParams.targets[0].GetUnitAbilityCooldown());

            yield return 0;
        }

        public override PropDescriptions CreateEditorInspectorDescriptions()
        {
            return new PropDescriptions { };
        }
    }
}