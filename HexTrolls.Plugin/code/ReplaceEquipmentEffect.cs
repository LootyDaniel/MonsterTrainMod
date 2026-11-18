using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace HexTrolls.Plugin.code
{
    public sealed class ReplaceEquipmentEffect : CardEffectBase
    {
        public override bool CanPlayAfterBossDead => false;
        public override bool CanApplyInPreviewMode => false;

        public override bool TestEffect(
            CardEffectState cardEffectState,
            CardEffectParams cardEffectParams,
            ICoreGameManagers coreGameManagers)
        {
            return cardEffectParams.targets != null && cardEffectParams.targets.Count > 0;
        }
                
        public override IEnumerator ApplyEffect(
            CardEffectState cardEffectState,
            CardEffectParams cardEffectParams,
            ICoreGameManagers coreGameManagers,
            ISystemManagers sysManagers)
        {
            foreach (CharacterState target in cardEffectParams.targets)
            {
                if (target == null || !target.IsAlive)
                    continue;

                // 1. Strip Grafted status and remove any existing equipment via CharacterState.RemoveEquipment
                var equipmentList = target.GetEquipment();
                if (equipmentList != null && equipmentList.Count > 0)
                {
                    CardState oldEquip = equipmentList[0];
                    target.RemoveStatusEffect("unit_grafted_equipment", 99, false);
                    yield return target.RemoveEquipment(coreGameManagers, oldEquip);
                }
            }

            // 2. Let the game's native CardEffectGrantEquipmentFromPool create & attach the new equipment cleanly
            var grantEffect = new CardEffectGrantEquipmentFromPool();
            yield return grantEffect.ApplyEffect(cardEffectState, cardEffectParams, coreGameManagers, sysManagers);
        }

        public override PropDescriptions CreateEditorInspectorDescriptions()
        {
            return new PropDescriptions { };
        }
    }
}
