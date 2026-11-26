// Trainers champions: three-card PocketMon draft when a Level II path is chosen.
// VERSION 1, designed for Monster Train 2 + TrainworksReloaded 0.7.34.
// Works alongside TrainersChampionCompanionRewards.cs v2 (Level I rewards).
// One C# handler is shared by all SIX Level II upgrade IDs; Red and Blue
// retain their distinct HP/Attack bonuses. No changes to the other tiers.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using TrainworksReloaded.Base;
using TrainworksReloaded.Core;
using TrainworksReloaded.Core.Interfaces;
using UnityEngine;
using UnityEngine.UI;

namespace HexTrolls.Plugin.code
{
    [HarmonyPatch]
    internal static class TrainersChampionTierIIDraft
    {
        private sealed class TierIIPath
        {
            internal readonly string UpgradeKey;
            internal readonly string ChampionKey;
            internal TierIIPath(string upgrade, string champion)
            {
                UpgradeKey = upgrade;
                ChampionKey = champion;
            }
        }

        private sealed class Offer
        {
            internal CardState Champion;
            internal SaveManager Save;
            internal string UpgradeKey;
            internal CardData[] Choices;
            internal float ReadyAt;
        }

        // Names are matched against REGISTERED UPGRADE RUNTIME IDS, NOT raw
        // names (the bug that prevented original Level I rewards from firing).
        private static readonly TierIIPath[] Paths =
        {
            new TierIIPath("TrainersRedPathII", "TrainersRedChampionCard"),
            new TierIIPath("TrainersRedTeslaPathII", "TrainersRedChampionCard"),
            new TierIIPath("TrainersRedFrostPathII", "TrainersRedChampionCard"),
            new TierIIPath("TrainersBluePathII", "TrainersBlueChampionCard"),
            new TierIIPath("TrainersBlueCuttlefishPathII", "TrainersBlueChampionCard"),
            new TierIIPath("TrainersBlueManshrimpPathII", "TrainersBlueChampionCard")
        };

        // One draft choice from a random set of 3 DISTINCT Form-1 species.
        // Every Form-1 unit card already starts at Level 5 in the Trainers
        // roster JSON, using the Level marker + four LevelStatBonus upgrades.
        private static readonly string[] PocketMonFamilies =
        {
            "Cuttlfish", "Hyena", "BloodWorm", "Snapsaurus",
            "Teslasaurus", "Garhound", "Fireary", "Frosset",
            "Umbrabeak", "Magmoth", "Chilload", "DragonMoth",
            "Leafshrew", "Manshrimp", "Jourmancoil", "Vampirant"
        };

        private static readonly Dictionary<CardState, HashSet<string>> Offered =
            new Dictionary<CardState, HashSet<string>>();
        private static readonly Queue<Offer> Pending = new Queue<Offer>();
        private static readonly System.Random Randomizer = new System.Random();
        private static GameObject runtime;
        private static GameObject modal;
        private static Offer openOffer;
        private static CardUI cachedCardPrefab;
        private static TextMeshProUGUI feedback;
        private static float nextRetry;

        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            int count = 0;
            foreach (var method in typeof(CardState).GetMethods(BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (method.Name != "ApplyPermanentUpgrade") continue;
                var args = method.GetParameters();
                if (args.Length < 2 || args[0].ParameterType != typeof(CardUpgradeState) ||
                    args[1].ParameterType != typeof(SaveManager)) continue;
                ++count;
                Info("Hooking CardState." + method.Name + "(" +
                     String.Join(",", args.Select(a => a.ParameterType.Name).ToArray()) + ")");
                yield return method;
            }
            if (count == 0) Warn("No compatible CardState.ApplyPermanentUpgrade found; Level II draft unavailable.");
        }

        [HarmonyPostfix]
        private static void Postfix(CardState __instance, CardUpgradeState __0, SaveManager __1)
        {
            try { OnUpgrade(__instance, __0, __1); }
            catch (Exception ex) { Warn("Postfix exception: " + ex); }
        }

        private static void OnUpgrade(CardState champion, CardUpgradeState upgrade, SaveManager save)
        {
            if (champion == null || upgrade == null || save == null || save.PreviewMode ||
                !champion.IsChampionCard()) return;
            string runtimeId = upgrade.GetCardUpgradeDataId();
            if (String.IsNullOrEmpty(runtimeId)) return;
            var container = Railend.GetContainer();
            if (container == null) return;
            var upgrades = container.GetInstance<IRegister<CardUpgradeData>>();
            var cards = container.GetInstance<IRegister<CardData>>();
            if (upgrades == null || cards == null) return;

            foreach (var path in Paths)
            {
                var upg = FindUpgrade(upgrades, path.UpgradeKey);
                if (upg == null || !SameId(runtimeId, upg.GetID(), path.UpgradeKey)) continue;
                var championCard = FindCard(cards, path.ChampionKey);
                if (championCard == null || !SameId(champion.GetCardDataID(),
                         championCard.GetID(), path.ChampionKey))
                {
                    Warn("Level II path matched, but champion mismatch: " + path.UpgradeKey);
                    return;
                }
                HashSet<string> seen;
                if (!Offered.TryGetValue(champion, out seen))
                {
                    seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Offered[champion] = seen;
                }
                if (seen.Contains(path.UpgradeKey))
                {
                    Info("Draft already offered for " + path.UpgradeKey + "; ignoring duplicate callback.");
                    return;
                }

                // Do not consume the event until all three valid card assets
                // are found. No draft can grant a missing/unregistered card.
                var all = new List<CardData>();
                foreach (var family in PocketMonFamilies)
                {
                    var card = FindCard(cards, "TrainersPocketMon" + family + "Form1Card");
                    if (card != null) all.Add(card);
                }
                if (all.Count < 3)
                {
                    Warn("Draft cannot open: only " + all.Count +
                         " Form-1 PocketMon cards are registered.");
                    return;
                }
                var chosen = all.OrderBy(c => Randomizer.Next()).Take(3).ToArray();
                seen.Add(path.UpgradeKey);
                Pending.Enqueue(new Offer
                {
                    Champion = champion,
                    Save = save,
                    UpgradeKey = path.UpgradeKey,
                    Choices = chosen,
                    ReadyAt = Time.unscaledTime + .5f
                });
                Info("QUEUED Level II draft via " + path.UpgradeKey +
                     ": " + String.Join(" / ", chosen.Select(c => c.GetName()).ToArray()) +
                     ". One card will be kept.");
                EnsureRuntime();
                return;
            }
        }

        private static void EnsureRuntime()
        {
            if (runtime != null) return;
            runtime = new GameObject("TrainersChampionTierIIDraftController");
            UnityEngine.Object.DontDestroyOnLoad(runtime);
            runtime.AddComponent<TrainersChampionTierIIDraftBehaviour>();
            Info("Draft UI controller created (three options; choose exactly one).");
        }

        internal static void Tick()
        {
            if (modal != null) return;
            if (Time.unscaledTime < nextRetry || Pending.Count == 0) return;
            var offer = Pending.Peek();
            if (Time.unscaledTime < offer.ReadyAt) return;
            var all = AllGameManagers.Instance;
            var save = all == null ? null : all.GetSaveManager();
            if (save == null || save.PreviewMode) return;
            if (!object.ReferenceEquals(save, offer.Save))
            {
                // The user abandoned the old run while a draft was open.
                Warn("Discarding pending draft from an earlier run.");
                Pending.Dequeue();
                return;
            }
            var combat = all.GetCombatManager();
            if (combat != null && combat.GetIsRunningCombat()) return;
            try { OpenDraft(offer); }
            catch (Exception ex)
            {
                Warn("Could not open draft UI (retrying): " + ex);
                if (modal != null) UnityEngine.Object.Destroy(modal);
                modal = null;
                nextRetry = Time.unscaledTime + 2f;
            }
        }

        private static void OpenDraft(Offer offer)
        {
            openOffer = offer;
            modal = new GameObject("TrainersChampionTierIIDraftModal",
                typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(modal);
            var canvas = modal.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 25000;
            var scaler = modal.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = .5f;

            var background = AddRect(modal.transform, "ModalBackdrop", new Vector2(0f,0f),
                Vector2.zero, new Vector2(.5f,.5f), new Vector2(.5f,.5f));
            background.anchorMin = Vector2.zero;
            background.anchorMax = Vector2.one;
            background.offsetMin = Vector2.zero;
            background.offsetMax = Vector2.zero;
            var shade = background.gameObject.AddComponent<Image>();
            shade.color = new Color(0f,0f,0f,.82f);
            shade.raycastTarget = true;

            var panel = AddRect(background, "CompanionDraftPanel",new Vector2(1140f,690f),
                Vector2.zero,new Vector2(.5f,.5f),new Vector2(.5f,.5f));
            var panelImage = panel.gameObject.AddComponent<Image>();
            panelImage.color = new Color(.075f,.065f,.14f,.99f);
            panelImage.raycastTarget = true;
            var border = panel.gameObject.AddComponent<Outline>();
            border.effectColor = new Color(.86f,.58f,.18f,1f);
            border.effectDistance = new Vector2(3f, -3f);

            AddText(panel,"TRAINERS  ·  COMPANION DRAFT II",new Vector2(1030f,65f),
                new Vector2(0f,298f), 35f, TextAlignmentOptions.Center);
            AddText(panel,"Choose ONE companion. It will permanently join your deck at Level 5.",
                new Vector2(1020f,60f),new Vector2(0f,248f),24f,TextAlignmentOptions.Center);
            feedback = AddText(panel,"Click one of the three cards to keep it.",
                new Vector2(1000f,54f),new Vector2(0f,-303f),21f,TextAlignmentOptions.Center);

            var prefab = FindDeckCardPrefab();
            for (int i=0;i<offer.Choices.Length;i++)
            {
                var choice = offer.Choices[i];
                float x = (i-1)*352f;
                var tile = AddRect(panel,"Choice"+(i+1),new Vector2(320f,452f),
                    new Vector2(x,-17f), new Vector2(.5f,.5f),new Vector2(.5f,.5f));
                var tileImage = tile.gameObject.AddComponent<Image>();
                tileImage.color = new Color(.19f,.15f,.27f,1f);
                tileImage.raycastTarget = true;

                if (prefab != null)
                {
                    try { DrawCard(prefab, choice, offer.Save, tile); }
                    catch (Exception ex)
                    {
                        Warn("Card face failed to render for " + choice.GetName() +
                             " (text choice remains clickable): " + ex.Message);
                    }
                }
                AddText(tile, choice.GetName(), new Vector2(305f,42f),
                    new Vector2(0f,-180f),24f,TextAlignmentOptions.Center);
                var button = tile.gameObject.AddComponent<Button>();
                button.targetGraphic = tileImage;
                CardData selected = choice;
                button.onClick.AddListener(() => SelectCard(selected));
            }
            Info("OPENED three-card draft: " + offer.UpgradeKey);
        }

        private static void DrawCard(CardUI prefab, CardData definition, SaveManager save,
            RectTransform tile)
        {
            // Uses the EXACT working DeckScreen prefab display technique from
            // TrainersNativeRosterController.BuildPicker(). It only PREVIEWS
            // the card; it is not added to the deck unless clicked.
            var temp = new CardState(definition, save, false, false);
            temp.RefetchAssets(definition);
            var cardUI = UnityEngine.Object.Instantiate(prefab, tile, false);
            cardUI.gameObject.name = "TrainersDraftPreviewCard";
            cardUI.gameObject.SetActive(true);
            var rect = cardUI.transform as RectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(.5f,.5f);
            rect.pivot = new Vector2(.5f,.5f);
            rect.anchoredPosition = new Vector2(0f,32f);
            float w = Mathf.Max(1f, Mathf.Abs(rect.rect.width));
            float h = Mathf.Max(1f, Mathf.Abs(rect.rect.height));
            if (w <= 1f) w = Mathf.Max(1f, Mathf.Abs(rect.sizeDelta.x));
            if (h <= 1f) h = Mathf.Max(1f, Mathf.Abs(rect.sizeDelta.y));
            float scale = Mathf.Min(292f/w, 350f/h);
            if (scale <= 0f || scale > 2f) scale = .82f;
            rect.localScale = new Vector3(scale,scale,1f);
            cardUI.SetCardVisibleAndInteractable(true,null);
            SetPickerCardUiState(cardUI);
            cardUI.ApplyStateToUI(temp,null,null,null,null,save,
                save.GetMastery(temp),true,false,null,null,null);
            foreach (var g in cardUI.GetComponentsInChildren<Graphic>(true))
                g.raycastTarget = false;
            foreach (var cg in cardUI.GetComponentsInChildren<CanvasGroup>(true))
                cg.blocksRaycasts = false;
        }

        private static CardUI FindDeckCardPrefab()
        {
            if (cachedCardPrefab != null) return cachedCardPrefab;
            try
            {
                var all = AllGameManagers.Instance;
                var screens = all == null ? null : all.GetScreenManager();
                var screen = screens == null ? null : screens.GetScreen((ScreenName)3);
                if (screen != null)
                {
                    var field = AccessTools.Field(screen.GetType(),"cardPrefab") ??
                        AccessTools.Field(typeof(DeckScreen),"cardPrefab");
                    cachedCardPrefab = field == null ? null : field.GetValue(screen) as CardUI;
                    if (cachedCardPrefab != null) return cachedCardPrefab;
                }
            }
            catch (Exception ex) { Warn("Deck prefab lookup failed: " + ex.Message); }
            foreach (var c in Resources.FindObjectsOfTypeAll<CardUI>())
            {
                if (c == null) continue;
                bool isDraftCard = false;
                for (Transform parent = c.transform; parent != null; parent = parent.parent)
                    if (parent.name == "TrainersChampionTierIIDraftModal")
                    { isDraftCard = true; break; }
                if (isDraftCard) continue;
                // Prefer the original prefab, not a clone already on a scene.
                if (!c.gameObject.scene.IsValid()) { cachedCardPrefab = c; return c; }
            }
            Info("Deck CardUI prefab unavailable; using clickable text-only choices.");
            return null;
        }

        private static void SetPickerCardUiState(CardUI cardUI)
        {
            var setter = typeof(CardUI).GetMethods(BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "SetUIState" &&
                    m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType.IsEnum);
            if (setter != null)
                setter.Invoke(cardUI,new object[]{Enum.ToObject(
                    setter.GetParameters()[0].ParameterType,3)});
        }

        private static void SelectCard(CardData chosen)
        {
            if (openOffer == null || chosen == null) return;
            var save = openOffer.Save;
            if (save == null || save.PreviewMode) return;
            try
            {
                CardState added;
                if (!TrainersPocketMonCapture.TryAddPermanentCard(save,chosen,out added) || added == null)
                {
                    if (feedback != null) feedback.text = "Could not add card. Please select again.";
                    Warn("Permanent deck addition failed for " + chosen.GetName());
                    return;
                }
                var ups = added.GetCardStateModifiers();
                var initial = ups == null ? null : ups.GetCardUpgrades();
                Info("GRANTED ONE: " + chosen.GetName() + " via " +
                     openOffer.UpgradeKey + "; deckCount=" + save.GetDeckState().Count +
                     "; initialUpgrades=" + (initial == null ? -1 : initial.Count()) +
                     ". Card should be Level 5.");
                if (Pending.Count > 0) Pending.Dequeue();
                openOffer = null;
                feedback = null;
                if (modal != null) UnityEngine.Object.Destroy(modal);
                modal = null;
                nextRetry = Time.unscaledTime + .3f;
            }
            catch (Exception ex)
            {
                Warn("Choice handler error: " + ex);
                if (feedback != null) feedback.text = "Something went wrong. Check BepInEx logs.";
            }
        }

        private static RectTransform AddRect(Transform parent,string name,Vector2 size,
            Vector2 position,Vector2 anchor,Vector2 pivot)
        {
            var obj=new GameObject(name,typeof(RectTransform));
            obj.transform.SetParent(parent,false);
            var rect=obj.GetComponent<RectTransform>();
            rect.anchorMin=rect.anchorMax=anchor;
            rect.pivot=pivot;
            rect.sizeDelta=size;
            rect.anchoredPosition=position;
            return rect;
        }

        private static TextMeshProUGUI AddText(Transform parent,string value,Vector2 size,
            Vector2 position,float font,TextAlignmentOptions alignment)
        {
            var rect=AddRect(parent,"Label",size,position,
                new Vector2(.5f,.5f),new Vector2(.5f,.5f));
            var text=rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.text=value;
            text.fontSize=font;
            text.fontStyle=FontStyles.Bold;
            text.alignment=alignment;
            text.enableWordWrapping=false;
            text.overflowMode=TextOverflowModes.Ellipsis;
            text.color=Color.white;
            text.raycastTarget=false;
            return text;
        }

        private static bool SameId(string actual,string registered,string key)
        {
            return String.Equals(actual,registered,StringComparison.OrdinalIgnoreCase) ||
                String.Equals(actual,key,StringComparison.OrdinalIgnoreCase) ||
                (actual != null && actual.EndsWith("-"+key,StringComparison.OrdinalIgnoreCase));
        }
        private static CardUpgradeData FindUpgrade(IRegister<CardUpgradeData> reg,string key)
        {
            foreach (string k in Keys("Upgrade",key))
            {
                CardUpgradeData data;
                if (reg.TryGetValue(k,out data) && data != null) return data;
            }
            return null;
        }
        private static CardData FindCard(IRegister<CardData> reg,string key)
        {
            foreach (string k in Keys("Card",key))
            {
                CardData data;
                if (reg.TryGetValue(k,out data) && data != null) return data;
            }
            return null;
        }
        private static IEnumerable<string> Keys(string kind,string key)
        {
            yield return MyPluginInfo.PLUGIN_GUID + "-" + kind + "-" + key;
            yield return MyPluginInfo.PLUGIN_GUID + "-" + kind + "Data-" + key;
            yield return MyPluginInfo.PLUGIN_GUID + "-" + key;
            yield return key;
        }
        private static void Info(string message)
        {
            if (global::HexTrolls.Plugin.Plugin.Logger != null)
                global::HexTrolls.Plugin.Plugin.Logger.LogInfo("[TrainersTierIIDraft v1] "+message);
        }
        private static void Warn(string message)
        {
            if (global::HexTrolls.Plugin.Plugin.Logger != null)
                global::HexTrolls.Plugin.Plugin.Logger.LogWarning("[TrainersTierIIDraft v1] "+message);
        }
    }

    // A separate MonoBehaviour is used to open the modal AFTER the champion's
    // upgrade confirmation has finished. This avoids trying to open UI in
    // the middle of CardState.ApplyPermanentUpgrade().
    public sealed class TrainersChampionTierIIDraftBehaviour : MonoBehaviour
    {
        private void Update() { TrainersChampionTierIIDraft.Tick(); }
    }
}
