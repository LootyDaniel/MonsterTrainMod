using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace HexTrolls.Plugin.code
{
    public sealed class DealDamageBasedOnCasterAttack : CardEffectBase
    {
        public override bool CanPlayAfterBossDead => false;
        public override bool CanApplyInPreviewMode => true;

        public override bool TestEffect(CardEffectState cardEffectState, CardEffectParams cardEffectParams, ICoreGameManagers coreGameManagers)
        {
            return true;
        }
        //public override int GetEffectTooltipDamage(CardEffectState cardEffectState, CardEffectParams cardEffectParams)
        //{
        //    return cardEffectParams.selfTarget.GetAttackDamage();
        //}
        public override IEnumerator ApplyEffect(CardEffectState cardEffectState, CardEffectParams cardEffectParams, ICoreGameManagers coreGameManagers, ISystemManagers sysManagers)
        {
            CharacterState user = cardEffectParams.selfTarget;
            if (user == null || cardEffectParams.targets.Count == 0)
            {
                yield break;
            }

            int damageAmount = user.GetAttackDamage();
            if (damageAmount <= 0)
            {
                yield break;
            }

            CombatManager combatManager = coreGameManagers.GetCombatManager();
            for (int i = 0; i < cardEffectParams.targets.Count; i++)
            {
                CharacterState target = cardEffectParams.targets[i];
                if (target != null && target.IsAlive)
                {
                    yield return combatManager.ApplyDamageToTarget(
                        damageAmount,
                        target,
                        new CombatManager.ApplyDamageToTargetParameters
                        {
                            playedCard = cardEffectParams.playedCard,
                            finalEffectInSequence = cardEffectParams.finalEffectInSequence,
                            relicState = cardEffectParams.sourceRelic,
                            selfTarget = user,
                            appliedVfx = cardEffectState.GetAppliedVFX()
                        });
                }
            }
        }

        public override PropDescriptions CreateEditorInspectorDescriptions()
        {
            return new PropDescriptions { };
        }
    }
}
