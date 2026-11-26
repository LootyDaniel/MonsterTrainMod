using Conductor.UI;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HexTrolls.Plugin.code
{
    /// <summary>
    /// A Unity LayoutGroup dedicated to the Trainers roster. The game's
    /// ClassMechanics layout sees ONE correctly sized group, while this group
    /// arranges six Conductor HUD objects in an alternating zigzag.
    /// No HUD is marked ignoreLayout and no update-loop position fight occurs.
    /// </summary>
    public sealed class TrainersRosterZigZagLayout : LayoutGroup
    {
        // Increase the previous v3.5 HUD layout by 33%, retaining the 70px top offset.
        // Scale column spacing and row spacing with the buttons to avoid collisions.
        internal const float HudSizeMultiplier = 1.33f;
        private const float TopOffset = 70f;
        internal const float Width = 132f * HudSizeMultiplier;
        internal const float Height = TopOffset + (268f - TopOffset) * HudSizeMultiplier;
        private const float CellWidth = 58.5f * HudSizeMultiplier;
        private const float CellHeight = 50.25f * HudSizeMultiplier;
        private const float SecondColumnX = 60f * HudSizeMultiplier;
        private const float RowPitch = 58f * HudSizeMultiplier;
        private const float RightColumnYOffset = 28f * HudSizeMultiplier;

        public override void CalculateLayoutInputHorizontal()
        {
            base.CalculateLayoutInputHorizontal();
            SetLayoutInputForAxis(Width, Width, Width, 0);
        }

        public override void CalculateLayoutInputVertical()
        {
            SetLayoutInputForAxis(Height, Height, Height, 1);
        }

        public override void SetLayoutHorizontal()
        {
            for (int i = 0; i < rectChildren.Count; ++i)
            {
                float x = (i % 2 == 0) ? 0f : SecondColumnX;
                SetChildAlongAxis(rectChildren[i], 0, x, CellWidth);
            }
        }

        public override void SetLayoutVertical()
        {
            for (int i = 0; i < rectChildren.Count; ++i)
            {
                float y = TopOffset + (i / 2) * RowPitch +
                          ((i % 2 == 0) ? 0f : RightColumnYOffset);
                SetChildAlongAxis(rectChildren[i], 1, y, CellHeight);
            }
        }
    }

    /// <summary>
    /// Added by Conductor to each native Trainers roster HUD.
    /// Once all six are visible, groups them in a layout element that the
    /// surrounding ClassMechanics layout cannot collapse or hide.
    /// </summary>
    public class TrainersRosterSlotHud : ClassMechanicHud
    {
        // Enlarge Conductor's HUD background and native icons by 33% from v3.5.
        // Previous scale was 0.6375; new value = 0.847875.
        // The stagger and original 70px top offset are preserved.
        private const float ArtworkScale = 0.6375f * TrainersRosterZigZagLayout.HudSizeMultiplier;
        private static RectTransform rosterContainer;
        private static RectTransform rosterContent;
        private static int checkedFrame = -1;
        private static bool alignmentLogged;

        // Used only if the upper HUD is absent during a scene transition.
        // As soon as its down arrow is available, alignment uses its actual
        // world-space centre instead of relying on an arbitrary pixel offset.
        private const float RightShiftFallback = 40f;

        private void LateUpdate()
        {
            // CRITICAL: The roster controller's normal setup, click hitboxes,
            // selection, labels, and in-combat actions all depend on this call.
            // V3.6 rendered the HUD but never invoked this update path.
            // The controller throttles initialization and visual refresh.
            TrainersNativeRosterController.OnLiveHudFrame();

            // The replacement TrainersSmallIcon is square (47x47). Conductor's
            // small-HUD icon RectTransform is not square, so the sprite must
            // preserve its own aspect ratio. Do NOT resize the HUD root or
            // change its hitbox: that previously broke slot interaction.
            PreserveTrainersIconAspect();

            // The click handler animates this root in Update(); maintain the
            // user-approved 33%-enlarged artwork size without editing hitboxes.
            if (transform.localScale.x != ArtworkScale ||
                transform.localScale.y != ArtworkScale)
                transform.localScale = new Vector3(ArtworkScale, ArtworkScale, 1f);

            if (checkedFrame == Time.frameCount) return;
            checkedFrame = Time.frameCount;

            if (rosterContainer == null || !transform.IsChildOf(rosterContainer))
                TryCreateRosterLayout();

            // Move only the dedicated content, NOT the parent layout element:
            // parent ClassMechanics layout would overwrite a root position.
            AlignRosterUnderUpperArrow();
        }

        // Correct only our custom roster icon, never the combat character
        // portraits, numeric labels, background, or transparent click hitbox.
        // Run on each active frame because Conductor can rebuild icon images
        // during UI refreshes or between map and combat screens.
        private bool loggedAspectFix;

        private void PreserveTrainersIconAspect()
        {
            foreach (var image in GetComponentsInChildren<Image>(true))
            {
                if (image == null) continue;
                Sprite sprite = image.overrideSprite != null
                    ? image.overrideSprite : image.sprite;
                if (sprite == null) continue;

                string spriteName = sprite.name ?? "";
                string textureName = sprite.texture != null
                    ? (sprite.texture.name ?? "") : "";

                bool isRosterIcon =
                    spriteName.IndexOf("TrainersRosterSmallIcon",
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    spriteName.IndexOf("TrainersSmallIcon",
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    textureName.IndexOf("TrainersSmallIcon",
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    // Fallback for atlases that rename the imported sprite.
                    (Mathf.Abs(sprite.rect.width - 47f) < .5f &&
                     Mathf.Abs(sprite.rect.height - 47f) < .5f);

                if (!isRosterIcon) continue;

                if (!image.preserveAspect)
                    image.preserveAspect = true;

                if (!loggedAspectFix)
                {
                    loggedAspectFix = true;
                    Debug.Log("[TrainersRosterLayout] v3.8 preserveAspect applied to " +
                              name + " / " + spriteName +
                              " (source " + sprite.rect.width + "x" +
                              sprite.rect.height + ").");
                }
            }
        }

        private static void TryCreateRosterLayout()
        {
            // During scene changes the roster may not have six active icons.
            // Leave Conductor's original display untouched until it does.
            var all = UnityEngine.Object.FindObjectsOfType<TrainersRosterSlotHud>(true);
            var huds = new List<TrainersRosterSlotHud>(6);
            foreach (var hud in all)
            {
                if (hud != null && hud.isActiveAndEnabled &&
                    hud.gameObject.activeInHierarchy &&
                    hud.transform is RectTransform)
                {
                    huds.Add(hud);
                }
            }
            if (huds.Count != 6) return;

            Transform host = huds[0].transform.parent;
            if (host == null || !(host is RectTransform)) return;
            foreach (var hud in huds)
            {
                if (hud.transform.parent != host) return;
            }

            // In the live Conductor HUD all six share ClassMechanics.
            // Preserve their native order, even if the object names differ.
            huds.Sort((a, b) =>
            {
                int aSlot = ParseSlot(a.name);
                int bSlot = ParseSlot(b.name);
                if (aSlot != 0 && bSlot != 0 && aSlot != bSlot)
                    return aSlot.CompareTo(bSlot);
                return a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex());
            });

            int insertIndex = int.MaxValue;
            foreach (var hud in huds)
                insertIndex = Math.Min(insertIndex, hud.transform.GetSiblingIndex());

            GameObject container = null;
            try
            {
                container = new GameObject("TrainersRosterZigZag", typeof(RectTransform));
                var root = container.GetComponent<RectTransform>();
                root.SetParent(host, false);
                root.SetSiblingIndex(insertIndex);
                root.anchorMin = new Vector2(0f, 1f);
                root.anchorMax = new Vector2(0f, 1f);
                root.pivot = new Vector2(0f, 1f);
                root.sizeDelta = new Vector2(TrainersRosterZigZagLayout.Width,
                    TrainersRosterZigZagLayout.Height);

                // Critical: the parent VerticalLayoutGroup must keep ONE item
                // with a real height. v2 marked all six items ignoreLayout,
                // which collapsed the display and made them disappear.
                var size = container.AddComponent<LayoutElement>();
                size.minWidth = size.preferredWidth = TrainersRosterZigZagLayout.Width;
                size.minHeight = size.preferredHeight = TrainersRosterZigZagLayout.Height;
                size.flexibleWidth = 0f;
                size.flexibleHeight = 0f;
                size.ignoreLayout = false;

                // A separate inner group allows horizontal centering without
                // fighting ClassMechanics' VerticalLayoutGroup or changing the
                // six original HUD GameObjects (and their pointer handlers).
                var inside = new GameObject("TrainersRosterButtons", typeof(RectTransform));
                var content = inside.GetComponent<RectTransform>();
                content.SetParent(root, false);
                content.anchorMin = new Vector2(0f, 1f);
                content.anchorMax = new Vector2(0f, 1f);
                content.pivot = new Vector2(0f, 1f);
                content.anchoredPosition = new Vector2(RightShiftFallback, 0f);
                content.sizeDelta = new Vector2(TrainersRosterZigZagLayout.Width,
                    TrainersRosterZigZagLayout.Height);
                var zigzag = inside.AddComponent<TrainersRosterZigZagLayout>();
                zigzag.childAlignment = TextAnchor.UpperLeft;

                foreach (var hud in huds)
                {
                    var item = hud.transform as RectTransform;
                    var oldLayout = hud.GetComponent<LayoutElement>();
                    if (oldLayout != null) oldLayout.ignoreLayout = false;
                    item.SetParent(content, false);
                    // Existing controller owns the interaction and click areas.
                }

                rosterContainer = root;
                rosterContent = content;
                alignmentLogged = false;
                LayoutRebuilder.MarkLayoutForRebuild(root);
                LayoutRebuilder.MarkLayoutForRebuild(content);
                LayoutRebuilder.MarkLayoutForRebuild(host as RectTransform);
                Debug.Log("[TrainersRosterLayout] v3.8 click handlers intact; " +
                    "HUD +33%, portraits 2x, 70px top offset, " +
                    "centering under upper HUD arrow; parent=" + host.name);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TrainersRosterLayout] v3.7 could not create zigzag: " + ex);
                // Don't destroy a partially built container with HUDs inside.
                // Return every HUD to its original Conductor parent first.
                if (container != null)
                {
                    foreach (var hud in huds)
                    {
                        if (hud != null && hud.transform.parent == container.transform)
                            hud.transform.SetParent(host, false);
                    }
                    UnityEngine.Object.Destroy(container);
                }
            }
        }

        // Find the centre of the actual down-arrow graphic in the HUD above
        // the Trainers buttons. A real RectTransform is used, so the alignment
        // works across Canvas scale factors and display resolutions.
        private static RectTransform FindUpperHudArrow(out string source)
        {
            source = "";
            var upper = HudManager.GetHUD(MyPluginInfo.PLUGIN_GUID, "MercenaryMechanic");
            if (upper == null || upper.gameObject == null ||
                !upper.gameObject.activeInHierarchy)
                return null;

            RectTransform top = upper.transform as RectTransform;
            if (top == null) return null;
            RectTransform best = null;
            int bestScore = -1;
            foreach (var rect in upper.GetComponentsInChildren<RectTransform>(true))
            {
                if (rect == null || !rect.gameObject.activeInHierarchy ||
                    rect.GetComponent<Graphic>() == null) continue;
                string n = rect.name.ToLowerInvariant();
                int score = -1;
                if (n.Contains("downarrow") || n.Contains("arrowdown") ||
                    n.Contains("down_arrow") || n.Contains("arrow_down")) score = 4;
                else if (n.Contains("arrow")) score = 3;
                else if (n.Contains("chevron") || n.Contains("caret")) score = 2;
                else if (n.Contains("expand") || n.Contains("dropdown")) score = 1;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = rect;
                }
            }
            if (best != null)
            {
                source = "arrow: " + best.name;
                return best;
            }
            // The arrow graphic can have an autogenerated Unity name. In that
            // case centre on the upper HUD itself, not a guessed screen point.
            source = "upper HUD centre (arrow name unavailable)";
            return top;
        }

        private static void AlignRosterUnderUpperArrow()
        {
            if (rosterContainer == null || rosterContent == null) return;

            string source;
            RectTransform reference = FindUpperHudArrow(out source);
            float newX = RightShiftFallback;
            if (reference != null)
            {
                // Left and right columns are symmetric: the visual centre of
                // the stagger is halfway between the first left edge and the
                // last right edge, regardless of the child icon scale.
                float groupCentreX = (60f + 58.5f) *
                                     TrainersRosterZigZagLayout.HudSizeMultiplier * 0.5f;
                Vector3 arrowWorldCentre = reference.TransformPoint(reference.rect.center);
                float arrowLocalX = rosterContainer.InverseTransformPoint(arrowWorldCentre).x;
                newX = arrowLocalX - groupCentreX;
            }
            var position = rosterContent.anchoredPosition;
            if (Mathf.Abs(position.x - newX) > 0.3f)
                rosterContent.anchoredPosition = new Vector2(newX, 0f);

            if (!alignmentLogged)
            {
                alignmentLogged = true;
                Debug.Log("[TrainersRosterLayout] v3.8 horizontal alignment: " +
                          (reference == null ? "fallback +40 px" : source) +
                          "; inner offset X=" + newX.ToString("F1"));
            }
        }

        private static int ParseSlot(string name)
        {
            if (String.IsNullOrEmpty(name)) return 0;
            const string prefix = "TrainersRosterHud";
            int start = name.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return 0;
            start += prefix.Length;
            int result = 0;
            while (start < name.Length && Char.IsDigit(name[start]))
            {
                result = result * 10 + (name[start] - '0');
                ++start;
            }
            return result >= 1 && result <= 6 ? result : 0;
        }
    }
}