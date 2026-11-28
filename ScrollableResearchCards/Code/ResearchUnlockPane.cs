using System;
using Game.UI;
using UnityEngine;

namespace Game.Systems.ScrollableResearchCards
{
    internal sealed class ResearchUnlockPane : MonoBehaviour, IReactsToScroll
    {
        internal const int Columns = 4;
        internal const int VisibleRows = 3;
        internal const int Capacity = Columns * VisibleRows;
        internal const float Gutter = 8;

        private CanvasGroup[] icons;
        private RectTransform content;
        private RectTransform thumb;
        private CanvasGroup card;
        private UnityEngine.UI.Graphic cardBackground;
        private float rowHeight;
        private int row;

        internal void Configure(CanvasGroup[] icons, RectTransform content, RectTransform thumb,
            CanvasGroup card, UnityEngine.UI.Graphic cardBackground, float rowHeight)
        {
            this.icons = icons;
            this.content = content;
            this.thumb = thumb;
            this.card = card;
            this.cardBackground = cardBackground;
            this.rowHeight = rowHeight;
            ShowRow(0);
        }

        public bool PassThroughZoomOnScroll => !CanScroll;

        private bool CanScroll => isActiveAndEnabled && icons != null && card && card.blocksRaycasts &&
            cardBackground && cardBackground.raycastTarget;

        public bool Scroll(float delta)
        {
            if (!CanScroll || delta == 0 || float.IsNaN(delta))
                return false;

            int next = Mathf.Clamp(row + (delta < 0 ? 1 : -1), 0, LastRow(icons.Length));
            if (next == row)
                return false;

            ShowRow(next);
            The.SysSig.HideTooltip.Send();
            A.S.UIQuery.HideCodexTooltip.Ask();
            return true;
        }

        internal static int LastRow(int count) => Math.Max(0, (count - 1) / Columns - (VisibleRows - 1));

        private void ShowRow(int next)
        {
            int start = next * Columns;
            int end = Math.Min(start + Capacity, icons.Length);
            for (int i = 0; i < icons.Length; i++)
            {
                bool visible = i >= start && i < end;
                icons[i].alpha = visible ? 1 : 0;
                icons[i].blocksRaycasts = visible;
            }

            row = next;
            content.anchoredPosition = new Vector2(0, row * rowHeight);
            float totalRows = LastRow(icons.Length) + VisibleRows;
            thumb.anchorMin = new Vector2(0, 1 - (row + VisibleRows) / totalRows);
            thumb.anchorMax = new Vector2(1, 1 - row / totalRows);
            thumb.offsetMin = Vector2.zero;
            thumb.offsetMax = Vector2.zero;
        }
    }
}

