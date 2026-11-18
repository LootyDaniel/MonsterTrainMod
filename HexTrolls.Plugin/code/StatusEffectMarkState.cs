using ShinyShoe;
using System.Collections;
using UnityEngine;
using static CombatManager;

namespace HexTrolls.Plugin
{
    public class StatusEffectMarkState : StatusEffectState
    {
        public override bool TestTrigger(InputTriggerParams inputTriggerParams, OutputTriggerParams outputTriggerParams, ICoreGameManagers coreGameManagers)
        {
            var unit = GetAssociatedCharacter();

            return unit != null
                   && !unit.IsAlive
                   && unit.GetStatusEffectStacks(GetStatusId()) > 0;
        }

        protected override IEnumerator OnTriggered(InputTriggerParams inputTriggerParams, OutputTriggerParams outputTriggerParams, ICoreGameManagers coreGameManagers)
        {
            CharacterState dyingUnit = GetAssociatedCharacter();
            int stacks = dyingUnit.GetStatusEffectStacks(GetStatusId());

            if (dyingUnit.IsAlive || stacks == 0)
                yield return 0;


            CharacterState killer = GetKiller(inputTriggerParams);
            if (killer != null && killer.IsAlive)
            { 
                killer.BuffDamage(stacks);
            }
            yield return coreGameManagers.GetSaveManager().AdjustGold(stacks);
        }

        private CharacterState GetKiller(InputTriggerParams inputTriggerParams)
        {
            return inputTriggerParams.attacker;
        }
    }
}