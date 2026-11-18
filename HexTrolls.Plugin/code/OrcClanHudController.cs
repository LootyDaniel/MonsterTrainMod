using Conductor.TrackedValues;
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

namespace HexTrolls.Plugin.code
{
    public class OrcClanHudInteractiveHandler : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public delegate int CostGetter();

        public Action OnClick;
        public CostGetter GetCurrentCost;

        private RectTransform _targetVisual;
        private GameObject _tooltipObj;
        private TextMeshProUGUI _tooltipText;
        private bool _isHovered;

        private readonly Vector3 _normalScale = Vector3.one;
        private readonly Vector3 _hoverScale = new Vector3(1.12f, 1.12f, 1f);

        public void Initialize(RectTransform visualTarget)
        {
            _targetVisual = visualTarget != null ? visualTarget : (RectTransform)GetComponent(typeof(RectTransform));
            EnsureTooltip();
        }

        private void EnsureTooltip()
        {
            if (_tooltipObj != null)
                return;

            _tooltipObj = new GameObject("OrcHudHoverTooltip", typeof(RectTransform), typeof(Image));
            _tooltipObj.transform.SetParent(transform, false);

            var bg = (Image)_tooltipObj.GetComponent(typeof(Image));
            bg.color = new Color(0.08f, 0.06f, 0.10f, 0.94f);
            bg.raycastTarget = false;

            var tipRect = (RectTransform)_tooltipObj.GetComponent(typeof(RectTransform));
            tipRect.anchorMin = new Vector2(1f, 0.5f);
            tipRect.anchorMax = new Vector2(1f, 0.5f);
            tipRect.pivot = new Vector2(0f, 0.5f);
            tipRect.anchoredPosition = new Vector2(14f, -18f);
            tipRect.sizeDelta = new Vector2(245f, 78f);

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

        private void UpdateTooltipText()
        {
            if (_tooltipText == null)
                return;

            int cost = GetCurrentCost != null ? GetCurrentCost() : 5;
            _tooltipText.text =
                $"<b><color=#FFD740>Warchief Command</color></b>\n" +
                $"Spend <b><color=#FFD740>{cost} Gold</color></b>: Deal 20 damage to the front enemy.";
        }

        private void Update()
        {
            if (_targetVisual != null)
            {
                Vector3 desired = _isHovered ? _hoverScale : _normalScale;
                _targetVisual.localScale = Vector3.Lerp(_targetVisual.localScale, desired, Time.unscaledDeltaTime * 14f);
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _isHovered = true;
            SoundManager.PlaySfxSignal?.Dispatch("UI_Hover");

            if (_tooltipObj != null)
            {
                UpdateTooltipText();
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
                if (_targetVisual != null)
                {
                    _targetVisual.localScale = new Vector3(0.94f, 0.94f, 1f);
                }

                OnClick?.Invoke();
                UpdateTooltipText();
            }
        }
    }

    public static class OrcClanHudController
    {
        private const int BaseGoldCost = 5;
        private static int _currentGoldCost = BaseGoldCost;
        private static string _lastRunId = string.Empty;

        private static bool _hasCachedBaseX = false;
        private static float _baseX = 0f;

        private static CardData _hudAbilityCardData;
        private static SimpleGlobalTrackedValueHandler _trackedHandler;
        private static TextMeshProUGUI _costLabel;

        public static void InitializeData(IRegister<CardData> cardRegister, SimpleGlobalTrackedValueHandler handler)
        {
            _trackedHandler = handler;

            string cardId = MyPluginInfo.PLUGIN_GUID.GetId(TemplateConstants.Card, "OrcClanHudAbilityCard");
            cardRegister.TryGetValue(cardId, out _hudAbilityCardData);

            ResetCostForNewRun();
        }

        public static void ResetCostForNewRun()
        {
            _currentGoldCost = BaseGoldCost;
            SyncHudDisplay();
        }

        private static void SyncHudDisplay()
        {
            if (_trackedHandler != null)
            {
                _trackedHandler.SetValue(null, _currentGoldCost);
            }

            if (_costLabel != null)
            {
                _costLabel.text = _currentGoldCost.ToString();

                var saveManager = AllGameManagers.Instance?.GetSaveManager();
                if (saveManager != null)
                {
                    _costLabel.color = saveManager.GetGold() >= _currentGoldCost
                        ? new Color(1f, 0.88f, 0.35f, 1f)
                        : new Color(1f, 0.35f, 0.35f, 1f);
                }
            }
        }

        public static void AttachHudButton()
        {
            var saveManager = AllGameManagers.Instance?.GetSaveManager();
            if (saveManager != null)
            {
                string currentRunId = saveManager.GetRunId();
                if (currentRunId != _lastRunId)
                {
                    _lastRunId = currentRunId;
                    ResetCostForNewRun();
                }
            }

            var hud = HudManager.GetHUD(MyPluginInfo.PLUGIN_GUID, "OrcClanCustomMechanic");
            if (hud == null)
            {
                Plugin.Logger.LogWarning("OrcClanCustomMechanic was not found when combat started.");
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

                    if(n.IndexOf("Hoard", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Forge", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        found = true;
                        shiftAmount = -165;
                    }

                    if(n.IndexOf("Moon", StringComparison.OrdinalIgnoreCase) >= 0)
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

            RectTransform gearVisualRect = null;
            foreach (Transform child in hudObj.transform)
            {
                if (child.name.IndexOf("Background", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    child.name.IndexOf("Banner", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    child.name == "OrcHudHoverTooltip")
                    continue;

                var childRect = (RectTransform)child.GetComponent(typeof(RectTransform));
                if (childRect != null)
                {
                    childRect.anchoredPosition = new Vector2(0f, -26f);
                    gearVisualRect = childRect;
                }
            }
            // Option B: Render behind Dragon's Hoard when paired so the golden egg stays visible on top,
            // while our purple banner extends neatly underneath it with our gear icon visible below
            if (primaryClanHudRect != null)
            {
                hudObj.transform.SetAsFirstSibling();
            }
            else
            {
                hudObj.transform.SetAsLastSibling();
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
            var interactive = (OrcClanHudInteractiveHandler)hudObj.GetComponent(typeof(OrcClanHudInteractiveHandler));
            if (interactive == null)
            {
                interactive = (OrcClanHudInteractiveHandler)hudObj.AddComponent(typeof(OrcClanHudInteractiveHandler));
            }
            interactive.Initialize(gearVisualRect);
            interactive.GetCurrentCost = () => _currentGoldCost;
            interactive.OnClick = OnHudClicked;
            SyncHudDisplay();
        }
        private static void OnHudClicked()
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
            if (saveManager.GetGold() < _currentGoldCost || _hudAbilityCardData == null)
            {
                SoundManager.PlaySfxSignal?.Dispatch("UI_PlayInvalidCard");
                return;
            }
            int selectedRoomIndex = roomManager.GetSelectedRoom();
            RoomState targetRoom = roomManager.GetRoom(selectedRoomIndex);
            if (targetRoom == null)
                return;
            var targets = new List<CharacterState>();
            targetRoom.AddCharactersToList(targets, Team.Type.Heroes);
            if (targets.Count == 0)
            {
                SoundManager.PlaySfxSignal?.Dispatch("UI_PlayInvalidCard");
                return;
            }
            int paidCost = _currentGoldCost;
            saveManager.AdjustGold(-paidCost);
            _currentGoldCost *= 2;
            SyncHudDisplay();
            SoundManager.PlaySfxSignal?.Dispatch("UI_CoinsSmall");
            SoundManager.PlaySfxSignal?.Dispatch("UI_PlayCard");
            var effectParams = new CardEffectParams
            {
                selectedRoom = selectedRoomIndex,
                targets = targets
            };
            var coreManagers = gameManagers.GetCoreManagers();
            var systemManagers = gameManagers.GetSystemManagers();
            combatManager.StartCoroutine(ExecuteCardEffects(effectParams, coreManagers, systemManagers));
        }
        private static IEnumerator ExecuteCardEffects(
        CardEffectParams effectParams,
        ICoreGameManagers coreManagers,
        ISystemManagers systemManagers)
        {
            foreach (var effectData in _hudAbilityCardData.GetEffects())
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
    //[HarmonyPatch(typeof(CombatManager), "StartCombat")]
    //public static class Patch_CombatManager_StartCombat_Hud
    //{
    //    static void Postfix()
    //    {
    //        OrcClanHudController.AttachHudButton();
    //    }
    //}
}