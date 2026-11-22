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
        private const float Width = 132f;
        private const float Height = 268f; // Original height + 70px downward offset
        private const float CellWidth = 58.5f; // 75% of v3 width
        private const float CellHeight = 50.25f; // 75% of v3 height
        private const float SecondColumnX = 60f;
        private const float RowPitch = 58f;
        private const float RightColumnYOffset = 28f;
        private const float TopOffset = 70f; // Move all six HUDs down without changing their stagger

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
        // Scale the whole HUD GameObject (background and icon sprites) to 63.75%.
        // The six layout positions are all shifted down 70 pixels.
        private const float ArtworkScale = 0.6375f; // 15% smaller than 0.75 (0.75 * 0.85)
        private static RectTransform rosterContainer;
        private static int checkedFrame = -1;

        private void LateUpdate()
        {
            // The visible Conductor HUD reliably exists after Trainworks registers
            // its data. Attach click handlers and initialize permanent slot markers.
            TrainersNativeRosterController.OnLiveHudFrame();

            // Apply the approved artwork scale before the canvas is rendered.
            // LayoutGroup child sizes alone do NOT shrink Conductor sprites.
            if (transform.localScale.x != ArtworkScale ||
                transform.localScale.y != ArtworkScale)
                transform.localScale = new Vector3(ArtworkScale, ArtworkScale, 1f);

            // Each of the six HUD components has LateUpdate; run discovery once.
            if (checkedFrame == Time.frameCount) return;
            checkedFrame = Time.frameCount;

            if (rosterContainer != null && transform.IsChildOf(rosterContainer))
                return;

            TryCreateRosterLayout();
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
                root.sizeDelta = new Vector2(132f, 268f);

                // Critical: the parent VerticalLayoutGroup must keep ONE item
                // with a real height. v2 marked all six items ignoreLayout,
                // which collapsed the display and made them disappear.
                var size = container.AddComponent<LayoutElement>();
                size.minWidth = size.preferredWidth = 132f;
                size.minHeight = size.preferredHeight = 268f;
                size.flexibleWidth = 0f;
                size.flexibleHeight = 0f;
                size.ignoreLayout = false;

                var zigzag = container.AddComponent<TrainersRosterZigZagLayout>();
                zigzag.childAlignment = TextAnchor.UpperLeft;

                foreach (var hud in huds)
                {
                    var item = hud.transform as RectTransform;
                    var oldLayout = hud.GetComponent<LayoutElement>();
                    if (oldLayout != null) oldLayout.ignoreLayout = false;
                    item.SetParent(root, false);
                    // Existing controller manages the hover/normal icon scale.
                    // The zigzag group manages size and position.
                }

                rosterContainer = root;
                LayoutRebuilder.MarkLayoutForRebuild(root);
                LayoutRebuilder.MarkLayoutForRebuild(host as RectTransform);
                Debug.Log("[TrainersRosterLayout] v3.5 artwork at 63.75%, shifted down 70px zigzag ACTIVE: " +
                    "6 visible HUDs; 132x268 container; 63.75% entire HUD sprite scale; top offset=70; parent=" + host.name);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TrainersRosterLayout] v3.5 could not create zigzag: " + ex);
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
