using System;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Systems.ScrollableResearchCards
{
    internal sealed class GridMetrics
    {
        private readonly Vector2 cell;
        private readonly Vector2 step;
        private readonly float iconScale;
        private readonly RectOffset padding;

        private GridMetrics(GridLayoutGroup layout, float width)
        {
            cell = layout.cellSize;
            step = layout.cellSize + layout.spacing;
            padding = layout.padding;
            float right = padding.left + (ResearchUnlockPane.Columns - 1) * step.x + cell.x;
            float shrink = Mathf.Max(0, right + ResearchUnlockPane.Gutter - width) / ResearchUnlockPane.Columns;
            step.x -= shrink;
            iconScale = 1 - shrink / cell.x;
        }

        internal float RowHeight => step.y;

        internal static GridMetrics Read(RectTransform grid)
        {
            var layout = grid.GetComponent<GridLayoutGroup>();
            if (!layout || layout.startCorner != GridLayoutGroup.Corner.UpperLeft ||
                layout.startAxis != GridLayoutGroup.Axis.Horizontal ||
                layout.childAlignment != TextAnchor.UpperLeft ||
                (layout.constraint != GridLayoutGroup.Constraint.Flexible &&
                 (layout.constraint != GridLayoutGroup.Constraint.FixedColumnCount ||
                  layout.constraintCount != ResearchUnlockPane.Columns)))
                throw new InvalidOperationException("the native unlock grid layout is unrecognized");

            float width = grid.rect.width;
            float pitch = layout.cellSize.x + layout.spacing.x;
            if (layout.cellSize.x <= ResearchUnlockPane.Gutter || layout.cellSize.y <= 0 ||
                layout.spacing.x < 0 || layout.spacing.y < 0 ||
                float.IsNaN(width) || float.IsInfinity(width))
                throw new InvalidOperationException("the native unlock grid has invalid dimensions");

            int columns = layout.constraint == GridLayoutGroup.Constraint.FixedColumnCount
                ? layout.constraintCount
                : Mathf.FloorToInt((width - layout.padding.horizontal + layout.spacing.x + 0.001f) / pitch);
            float fourColumnWidth = layout.padding.left + 3 * pitch + layout.cellSize.x;
            if (columns != ResearchUnlockPane.Columns || width < fourColumnWidth)
                throw new InvalidOperationException("the native unlock grid cannot fit four columns");

            return new GridMetrics(layout, width);
        }

        internal void Place(RectTransform icon, int index)
        {
            icon.anchorMin = icon.anchorMax = icon.pivot = new Vector2(0, 1);
            icon.sizeDelta = cell * iconScale;
            icon.anchoredPosition = new Vector2(
                padding.left + index % ResearchUnlockPane.Columns * step.x,
                -padding.top - index / ResearchUnlockPane.Columns * step.y - cell.y * (1 - iconScale) / 2);
        }

        internal void PlaceTrack(RectTransform track)
        {
            track.anchorMin = new Vector2(1, 0);
            track.anchorMax = new Vector2(1, 1);
            track.offsetMin = new Vector2(-ResearchUnlockPane.Gutter, padding.bottom);
            track.offsetMax = new Vector2(-2, -padding.top);
        }
    }
}

