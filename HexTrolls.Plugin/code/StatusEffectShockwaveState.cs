using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace HexTrolls.Plugin.code
{
    public class StatusEffectShockwaveState : StatusEffectState
    {
        private bool isProcessingShockwave = false;

        public override bool TestTrigger(
            InputTriggerParams inputTriggerParams,
            OutputTriggerParams outputTriggerParams,
            ICoreGameManagers coreGameManagers)
        {
            if (isProcessingShockwave)
                return false;

            CharacterState self = GetAssociatedCharacter();
            CharacterState hitUnit = inputTriggerParams.attacked;

            if (self == null || hitUnit == null || hitUnit == self)
                return false;

            // Only trigger when THIS unit is the attacker hitting an enemy
            if (inputTriggerParams.attacker != self)
                return false;

            if (hitUnit.GetTeamType() == self.GetTeamType())
                return false;

            return self.GetAttackDamage() > 0;
        }

        protected override IEnumerator OnTriggered(
            InputTriggerParams inputTriggerParams,
            OutputTriggerParams outputTriggerParams,
            ICoreGameManagers coreGameManagers)
        {
            CharacterState self = GetAssociatedCharacter();
            CharacterState hitUnit = inputTriggerParams.attacked;
            RoomState room = self?.GetCurrentRoom() ?? hitUnit?.GetCurrentRoom();

            if (room == null || self == null)
                yield break;

            // Strictly target the opposing team
            Team.Type enemyTeam = (self.GetTeamType() == Team.Type.Monsters) ? Team.Type.Heroes : Team.Type.Monsters;
            List<CharacterState> enemyUnits = new List<CharacterState>();
            room.AddCharactersToList(enemyUnits, enemyTeam);

            int hitIndex = enemyUnits.IndexOf(hitUnit);
            // If hitUnit died from the primary blow and left the list (hitIndex == -1), start at index 0
            int startIndex = (hitIndex >= 0) ? hitIndex + 1 : 0;

            if (startIndex >= enemyUnits.Count)
                yield break;

            isProcessingShockwave = true;

            // Use the attacker's combat attack damage since inputTriggerParams.damage is 0 in post_attacking
            int baseAttackDamage = self.GetAttackDamage();
            int currentShockwaveDamage = Mathf.FloorToInt(baseAttackDamage * 0.5f);
            CombatManager combatManager = coreGameManagers.GetCombatManager();

            for (int i = startIndex; i < enemyUnits.Count; i++)
            {
                if (currentShockwaveDamage <= 0)
                    break;

                CharacterState nextEnemy = enemyUnits[i];
                if (nextEnemy == null || !nextEnemy.IsAlive)
                    continue;

                yield return combatManager.ApplyDamageToTarget(
                    currentShockwaveDamage,
                    nextEnemy,
                    new CombatManager.ApplyDamageToTargetParameters
                    {
                        damageType = Damage.Type.DirectAttack
                    }
                );

                // Halve the damage again for the next unit behind it
                currentShockwaveDamage = Mathf.FloorToInt(currentShockwaveDamage * 0.5f);
            }

            isProcessingShockwave = false;
        }
    }
}

