using Conductor.UI;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using TrainworksReloaded.Base;
using TrainworksReloaded.Base.Extensions;
using TrainworksReloaded.Core;
using TrainworksReloaded.Core.Interfaces;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using static MonoMod.Cil.RuntimeILReferenceBag.FastDelegateInvokers;

namespace HexTrolls.Plugin.code
{
    public class MercenaryContractData
    {
        public string ClanName;
        public string BannerSpriteId;
        public string CardId;

        public Sprite CachedBannerSprite;
        public CardData CachedCardData;
    }

    public class MercenaryHudHoverAndClick : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public delegate int CostGetter();
        public delegate bool BoolGetter();
        public delegate string StringGetter();

        public Action OnClick;
        public CostGetter GetCurrentCost;
        public BoolGetter IsUsedThisBattle;
        public StringGetter GetClanName;

        private RectTransform _visualTarget;
        private GameObject _tooltipObj;
        private TextMeshProUGUI _tooltipText;
        private bool _isHovered;

        private readonly Vector3 _normalScale = Vector3.one;
        private readonly Vector3 _hoverScale = new Vector3(1.12f, 1.12f, 1f);

        public void Initialize(RectTransform visualTarget)
        {
            _visualTarget = visualTarget != null ? visualTarget : (RectTransform)GetComponent(typeof(RectTransform));
            EnsureTooltip();
        }

        private void EnsureTooltip()
        {
            if (_tooltipObj != null)
                return;

            _tooltipObj = new GameObject("MercenaryHudTooltip", typeof(RectTransform), typeof(Image));
            _tooltipObj.transform.SetParent(transform, false);

            var bg = (Image)_tooltipObj.GetComponent(typeof(Image));
            bg.color = new Color(0.08f, 0.06f, 0.10f, 0.95f);
            bg.raycastTarget = false;

            var tipRect = (RectTransform)_tooltipObj.GetComponent(typeof(RectTransform));
            tipRect.anchorMin = new Vector2(1f, 0.5f);
            tipRect.anchorMax = new Vector2(1f, 0.5f);
            tipRect.pivot = new Vector2(0f, 0.5f);
            tipRect.anchoredPosition = new Vector2(14f, -18f);
            tipRect.sizeDelta = new Vector2(265f, 88f);

            var outline = (Outline)_tooltipObj.AddComponent(typeof(Outline));
            outline.effectColor = new Color(0.85f, 0.65f, 0.20f, 0.9f);
            outline.effectDistance = new Vector2(2f, -2f);

            var textObj = new GameObject("TooltipText", typeof(RectTransform));
            textObj.transform.SetParent(_tooltipObj.transform, false);

            var textRect = (RectTransform)textObj.GetComponent(typeof(RectTransform));
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(10f, 6f);
            textRect.offsetMax = new Vector2(-10f, -6f);

            _tooltipText = (TextMeshProUGUI)textObj.AddComponent(typeof(TextMeshProUGUI));
            _tooltipText.fontSize = 15f;
            _tooltipText.alignment = TextAlignmentOptions.MidlineLeft;
            _tooltipText.enableWordWrapping = true;
            _tooltipText.raycastTarget = false;

            _tooltipObj.SetActive(false);
        }

        public void UpdateTooltip()
        {
            if (_tooltipText == null)
                return;

            string clan = GetClanName != null ? GetClanName() : "Allied";
            int cost = GetCurrentCost != null ? GetCurrentCost() : 25;
            bool used = IsUsedThisBattle != null && IsUsedThisBattle();

            if (used)
            {
                _tooltipText.text =
                    $"<b><color=#A0A0A0>{clan} Mercenaries</color></b>\n" +
                    $"<color=#FF6B6B>Reinforcements already called this battle.</color>";
            }
            else
            {
                _tooltipText.text =
                    $"<b><color=#FFD740>{clan} Mercenaries</color></b>\n" +
                    $"Spend <b><color=#FFD740>{cost} Gold</color></b>: Summon 3 random {clan} units to this floor. (Once per battle)";
            }
        }

        private void Update()
        {
            if (_visualTarget != null)
            {
                bool used = IsUsedThisBattle != null && IsUsedThisBattle();
                Vector3 desired = (_isHovered && !used) ? _hoverScale : _normalScale;
                _visualTarget.localScale = Vector3.Lerp(_visualTarget.localScale, desired, Time.unscaledDeltaTime * 14f);
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _isHovered = true;
            SoundManager.PlaySfxSignal?.Dispatch("UI_Hover");

            if (_tooltipObj != null)
            {
                UpdateTooltip();
                _tooltipObj.transform.SetAsLastSibling();
                _tooltipObj.SetActive(true);
            }
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _isHovered = false;
            if (_tooltipObj != null)
            {
                _tooltipObj.SetActive(false);
            }
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left)
            {
                if (_visualTarget != null)
                {
                    _visualTarget.localScale = new Vector3(0.94f, 0.94f, 1f);
                }

                OnClick?.Invoke();
                UpdateTooltip();
            }
        }
    }

    public static class MercenaryHudController
    {
        private const int BaseGoldCost = 25;
        private const int CostIncreasePerUse = 25;

        private static int _currentGoldCost = BaseGoldCost;
        private static bool _usedThisBattle = false;
        private static string _lastRunId = string.Empty;

        private static bool _hasCachedBaseX = false;
        private static float _baseX = 0f;

        private static TextMeshProUGUI _costLabel;
        private static Image _iconImage;
        private static MercenaryContractData _currentBattleContract;

        private static readonly List<MercenaryContractData> _allClanContracts = new List<MercenaryContractData>
        {
            new MercenaryContractData { ClanName = "Banished", BannerSpriteId = "ContractBanner_Banished", CardId = "ContractAbility_Banished" },
            new MercenaryContractData { ClanName = "Pyreborne", BannerSpriteId = "ContractBanner_Pyreborne", CardId = "ContractAbility_Pyreborne" },
            new MercenaryContractData { ClanName = "Luna Coven", BannerSpriteId = "ContractBanner_LunaCoven", CardId = "ContractAbility_LunaCoven" },
            new MercenaryContractData { ClanName = "Underlegion", BannerSpriteId = "ContractBanner_Underlegion", CardId = "ContractAbility_Underlegion" },
            new MercenaryContractData { ClanName = "Lazarus League", BannerSpriteId = "ContractBanner_LazarusLeague", CardId = "ContractAbility_LazarusLeague" },
            new MercenaryContractData { ClanName = "Hellhorned", BannerSpriteId = "ContractBanner_Hellhorned", CardId = "ContractAbility_Hellhorned" },
            new MercenaryContractData { ClanName = "Awoken", BannerSpriteId = "ContractBanner_Awoken", CardId = "ContractAbility_Awoken" },
            new MercenaryContractData { ClanName = "Stygian Guard", BannerSpriteId = "ContractBanner_Stygian", CardId = "ContractAbility_Stygian" },
            new MercenaryContractData { ClanName = "Umbra", BannerSpriteId = "ContractBanner_Umbra", CardId = "ContractAbility_Umbra" },
            new MercenaryContractData { ClanName = "Melting Remnant", BannerSpriteId = "ContractBanner_Remnant", CardId = "ContractAbility_Remnant" },
            new MercenaryContractData { ClanName = "Wurmkin", BannerSpriteId = "ContractBanner_Wurmkin", CardId = "ContractAbility_Wurmkin" },
            new MercenaryContractData { ClanName = "Railforged", BannerSpriteId = "ContractBanner_Railforged", CardId = "ContractAbility_Railforged" }
        };

        // Tracks which clans have not yet been rolled during the current run
        private static readonly List<MercenaryContractData> _remainingClansThisRun = new List<MercenaryContractData>();

        public static void InitializeData(IRegister<CardData> cardRegister, IRegister<Sprite> spriteRegister)
        {
            foreach (var contract in _allClanContracts)
            {
                string fullCardId = MyPluginInfo.PLUGIN_GUID.GetId(TemplateConstants.Card, contract.CardId);
                cardRegister.TryGetValue(fullCardId, out contract.CachedCardData);

                string fullSpriteId = MyPluginInfo.PLUGIN_GUID.GetId(TemplateConstants.Sprite, contract.BannerSpriteId);
                spriteRegister.TryGetValue(fullSpriteId, out contract.CachedBannerSprite);
            }

            ResetForNewRun();
        }

        private static void ResetForNewRun()
        {
            _currentGoldCost = BaseGoldCost;
            _usedThisBattle = false;

            _remainingClansThisRun.Clear();
            _remainingClansThisRun.AddRange(_allClanContracts);
        }

        private static void RollClanForBattle()
        {
            _usedThisBattle = false;

            // Refill pool only if every clan in the list has already been used in this run
            if (_remainingClansThisRun.Count == 0)
            {
                _remainingClansThisRun.AddRange(_allClanContracts);
            }

            int randomIndex = UnityEngine.Random.Range(0, _remainingClansThisRun.Count);
            _currentBattleContract = _remainingClansThisRun[randomIndex];
            _remainingClansThisRun.RemoveAt(randomIndex);

            Plugin.Logger.LogInfo($"Mercenary battle contract rolled: {_currentBattleContract.ClanName} (Cost: {_currentGoldCost} Gold)");
        }
        private static void SyncHudDisplay()
        {
            var saveManager = AllGameManagers.Instance?.GetSaveManager();
            int currentGold = saveManager != null ? saveManager.GetGold() : 0;
            if (_costLabel != null)
            {
                if (_usedThisBattle)
                {
                    _costLabel.text = "X";
                    _costLabel.color = new Color(0.6f, 0.6f, 0.6f, 0.85f);
                }
                else
                {
                    _costLabel.text = _currentGoldCost.ToString();
                    _costLabel.color = currentGold >= _currentGoldCost
                    ? new Color(1f, 0.88f, 0.35f, 1f)
                    : new Color(1f, 0.35f, 0.35f, 1f);
                }
            }
            if (_iconImage != null)
            {
                _iconImage.color = _usedThisBattle
                ? new Color(0.45f, 0.45f, 0.45f, 0.8f)
                : Color.white;
            }
        }
        public static void OnCombatStarted()
        {
            var saveManager = AllGameManagers.Instance?.GetSaveManager();
            if (saveManager != null)
            {
                string currentRunId = saveManager.GetRunId();
                if (currentRunId != _lastRunId)
                {
                    _lastRunId = currentRunId;
                    ResetForNewRun();
                }
            }
            // Pick a new non-repeating clan at the start of every battle
            RollClanForBattle();
            var hud = HudManager.GetHUD(MyPluginInfo.PLUGIN_GUID, "MercenaryMechanic");
            if (hud == null)
            {
                Plugin.Logger.LogWarning("MercenaryMechanic HUD was not found when combat started.");
                return;
            }
            GameObject hudObj = hud.gameObject;
            var layout = (LayoutElement)hudObj.GetComponent(typeof(LayoutElement));
            if (layout == null)
            {
                layout = (LayoutElement)hudObj.AddComponent(typeof(LayoutElement));
            }
            layout.ignoreLayout = true;
            var shiftAmount = 0;
            // Detect Dragon's Hoard / Moon Phase / Forge so we stack neatly below if paired
            RectTransform primaryClanHudRect = null;
            Transform searchRoot = hudObj.transform.parent?.parent ?? hudObj.transform.parent;
            if (searchRoot != null)
            {
                foreach (Transform t in searchRoot.GetComponentsInChildren(typeof(Transform), false))
                {
                    if (t == hudObj.transform || t.IsChildOf(hudObj.transform) || hudObj.transform.IsChildOf(t))
                        continue;
                    string n = t.name;
                    bool found = false;

                    if (n.IndexOf("Hoard", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Forge", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        found = true;
                        shiftAmount = -165;
                    }

                    if (n.IndexOf("Moon", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        found = true;
                        shiftAmount = -240;
                    }
                    if (found)
                    {
                        primaryClanHudRect = (RectTransform)t.GetComponent(typeof(RectTransform));
                        break;
                    }
                }
            }
            var rect = (RectTransform)hudObj.GetComponent(typeof(RectTransform));
            if (rect != null)
            {
                rect.localScale = Vector3.one;
                if (rect.sizeDelta.x < 60f || rect.sizeDelta.y < 60f)
                {
                    rect.sizeDelta = new Vector2(90f, 80f);
                }
                if (primaryClanHudRect != null)
                {
                    rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, shiftAmount);
                    rect.position = new Vector3(primaryClanHudRect.position.x, rect.position.y, rect.position.z);
                }
                else
                {
                    if (!_hasCachedBaseX)
                    {
                        _baseX = rect.anchoredPosition.x;
                        _hasCachedBaseX = true;
                    }
                    rect.anchoredPosition = new Vector2(_baseX - 60f, -60f);
                }
            }
            if (primaryClanHudRect != null)
            {
                hudObj.transform.SetAsFirstSibling();
            }
            else
            {
                hudObj.transform.SetAsLastSibling();
            }
            // Swap the banner sprite to the rolled clan's banner and center the inner icon
            RectTransform iconVisualRect = null;
            _iconImage = null;
            foreach (Transform child in hudObj.transform)
            {
                if (child.name == "MercenaryHudTooltip")
                    continue;
                var childImg = (Image)child.GetComponent(typeof(Image));
                if (child.name.IndexOf("Background", StringComparison.OrdinalIgnoreCase) >= 0 ||
                child.name.IndexOf("Banner", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (childImg != null && _currentBattleContract?.CachedBannerSprite != null)
                    {
                        childImg.sprite = _currentBattleContract.CachedBannerSprite;
                    }
                }
                else
                {
                    var childRect = (RectTransform)child.GetComponent(typeof(RectTransform));
                    if (childRect != null)
                    {
                        childRect.anchoredPosition = new Vector2(0f, -26f);
                        iconVisualRect = childRect;
                    }
                    if (childImg != null)
                    {
                        _iconImage = childImg;
                    }
                }
            }
            var hitBox = (Graphic)hudObj.GetComponent(typeof(Graphic));
            if (hitBox == null)
            {
                var img = (Image)hudObj.AddComponent(typeof(Image));
                img.color = new Color(0f, 0f, 0f, 0f);
                hitBox = img;
            }
            hitBox.raycastTarget = true;
            _costLabel = (TextMeshProUGUI)hudObj.GetComponentInChildren(typeof(TextMeshProUGUI), true);
            if (_costLabel != null)
            {
                _costLabel.gameObject.SetActive(true);
                _costLabel.transform.SetAsLastSibling();
                _costLabel.raycastTarget = false;
            }
            foreach (Graphic childGraphic in hudObj.GetComponentsInChildren(typeof(Graphic), true))
            {
                if (childGraphic.gameObject != hudObj)
                {
                    childGraphic.raycastTarget = false;
                }
            }
            var interactive = (MercenaryHudHoverAndClick)hudObj.GetComponent(typeof(MercenaryHudHoverAndClick));
            if (interactive == null)
            {
                interactive = (MercenaryHudHoverAndClick)hudObj.AddComponent(typeof(MercenaryHudHoverAndClick));
            }
            interactive.Initialize(iconVisualRect);
            interactive.GetCurrentCost = () => _currentGoldCost;
            interactive.IsUsedThisBattle = () => _usedThisBattle;
            interactive.GetClanName = () => _currentBattleContract != null ? _currentBattleContract.ClanName : "Mercenary";
            interactive.OnClick = OnMercenaryHudClicked;
            SyncHudDisplay();
        }
        private static void OnMercenaryHudClicked()
        {
            var gameManagers = AllGameManagers.Instance;
            var combatManager = gameManagers?.GetCombatManager();
            var cardManager = gameManagers?.GetCardManager();
            var roomManager = gameManagers?.GetRoomManager();
            var saveManager = gameManagers?.GetSaveManager();
            if (combatManager == null || cardManager == null || roomManager == null || saveManager == null)
                return;
            if (!combatManager.GetIsRunningCombat() || combatManager.GetCombatPhase() != CombatManager.Phase.MonsterTurn)
                return;
            // Block if already called this battle, not enough gold, or missing card data
            if (_usedThisBattle ||
            _currentBattleContract == null ||
            _currentBattleContract.CachedCardData == null ||
            saveManager.GetGold() < _currentGoldCost)
            {
                SoundManager.PlaySfxSignal?.Dispatch("UI_PlayInvalidCard");
                return;
            }
            int selectedRoomIndex = roomManager.GetSelectedRoom();
            RoomState targetRoom = roomManager.GetRoom(selectedRoomIndex);
            if (targetRoom == null)
                return;
            // Deduct current gold cost, lock for this battle, and increase next cost by +50 (twice 25)
            saveManager.AdjustGold(-_currentGoldCost);
            _usedThisBattle = true;
            _currentGoldCost += CostIncreasePerUse;
            SyncHudDisplay();
            SoundManager.PlaySfxSignal?.Dispatch("UI_CoinsSmall");
            SoundManager.PlaySfxSignal?.Dispatch("UI_PlayCard");
            var targets = new List<CharacterState>();
            targetRoom.AddCharactersToList(targets, Team.Type.Monsters);
            var effectParams = new CardEffectParams
            {
                selectedRoom = selectedRoomIndex,
                targets = targets
            };
            var coreManagers = gameManagers.GetCoreManagers();
            var systemManagers = gameManagers.GetSystemManagers();
            combatManager.StartCoroutine(ExecuteMercenarySpawn(_currentBattleContract.CachedCardData, effectParams, coreManagers, systemManagers));
        }
        private static IEnumerator ExecuteMercenarySpawn(
        CardData cardToCast,
        CardEffectParams effectParams,
        ICoreGameManagers coreManagers,
        ISystemManagers systemManagers)
        {
            foreach (var effectData in cardToCast.GetEffects())
            {
                var effectState = new CardEffectState();
                effectState.Setup(effectData, null);
                var cardEffect = effectState.GetCardEffect();
                if (cardEffect != null && cardEffect.TestEffect(effectState, effectParams, coreManagers))
                {
                    yield return cardEffect.ApplyEffect(effectState, effectParams, coreManagers, systemManagers);
                }
            }
        }
    }
    [HarmonyPatch(typeof(CombatManager), "StartCombat")]
    public static class Patch_CombatManager_StartCombat_MercenaryHud
    {
        static void Postfix()
        {
            MercenaryHudController.OnCombatStarted();
        }
    }
}