using System.Collections;
using System.Linq;
using UnityEngine;

namespace HexTrolls.Plugin.code
{
    public sealed class CopyUnitEquipmentPermanentEffect : CardEffectBase
    {
        public override bool CanPlayAfterBossDead => false;
        public override bool CanApplyInPreviewMode => true;

        public override PropDescriptions CreateEditorInspectorDescriptions()
        {
            return new PropDescriptions { };
        }

        private static bool HasAnyEquipment(CharacterState t)
        {
            if (t == null || !t.IsAlive)
                return false;

            var equip = t.GetEquipment();
            if (equip != null && equip.Count > 0)
                return true;

            return t.HasStatusEffect("unit_grafted_equipment");
        }

        public override bool TestEffect(
            CardEffectState cardEffectState,
            CardEffectParams cardEffectParams,
            ICoreGameManagers coreGameManagers)
        {
            if (cardEffectParams == null || cardEffectParams.targets == null || cardEffectParams.targets.Count == 0)
                return true;

            return cardEffectParams.targets.Any(HasAnyEquipment);
        }

        public override IEnumerator ApplyEffect(
            CardEffectState cardEffectState,
            CardEffectParams cardEffectParams,
            ICoreGameManagers coreGameManagers,
            ISystemManagers sysManagers)
        {
            var saveManager = coreGameManagers?.GetSaveManager();
            if (saveManager == null || saveManager.PreviewMode)
                yield break;

            if (cardEffectParams == null || cardEffectParams.targets == null || cardEffectParams.targets.Count == 0)
                yield break;

            CharacterState target = cardEffectParams.targets.FirstOrDefault(HasAnyEquipment);
            if (target == null)
                yield break;

            var equipmentList = target.GetEquipment();
            if (equipmentList == null || equipmentList.Count == 0)
                yield break;

            CardState equippedCard = equipmentList.FirstOrDefault();
            if (equippedCard == null)
                yield break;

            string rawId = equippedCard.GetCardDataID();
            var allGameData = coreGameManagers.GetAllGameData();
            CardData equipmentData = allGameData?.FindCardData(rawId)
                                     ?? allGameData?.FindCardDataByName(rawId);

            if (equipmentData == null)
                yield break;

            int bonusAttack = cardEffectState != null ? cardEffectState.GetParamInt() : 0;

            var cardManager = coreGameManagers.GetCardManager();
            if (cardManager != null)
            {
                CardState createdCard = cardManager.AddNewCard(equipmentData, CardPile.HandPile, false, true);

                if (createdCard != null && bonusAttack > 0)
                {
                    var upgradeState = new CardUpgradeState();
                    upgradeState.SetAttackDamage(bonusAttack);

                    createdCard.GetCardStateModifiers().AddUpgrade(upgradeState);
                    createdCard.UpdateCardBodyText();
                }
            }

            yield break;
        }
    }
}
