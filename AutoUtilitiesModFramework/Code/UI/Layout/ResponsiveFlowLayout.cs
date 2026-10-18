using UnityEngine;
using UnityEngine.UI;

namespace AutoUtilitiesModFramework;

/// <summary>Centers fixed-size cells and reflows them as width changes.</summary>
/// <remarks>
/// Prefer <see cref="PanelContentContext.AddResponsiveFlow"/> inside a
/// framework panel. Attach this component directly only for custom UI roots.
/// Unity calls the layout methods; consumer code only configures the fields.
/// </remarks>
/// <example><code>
/// var flow = customRoot.AddComponent&lt;ResponsiveFlowLayout&gt;();
/// flow.CellSize = new Vector2(150f, 76f);
/// flow.Spacing = new Vector2(8f, 8f);
/// card.transform.SetParent(customRoot, false);
/// </code></example>
public sealed class ResponsiveFlowLayout : LayoutGroup
{
    /// <summary>Width and height assigned to every direct child.</summary>
    public Vector2 CellSize = new Vector2(124f, 69f);
    /// <summary>Horizontal and vertical gap between cells.</summary>
    public Vector2 Spacing = new Vector2(6f, 6f);

    private int columns = 1;
    private int rows;

    /// <summary>Unity layout callback; do not call directly.</summary>
    public override void CalculateLayoutInputHorizontal()
    {
        base.CalculateLayoutInputHorizontal();
        CalculateGrid();
        SetLayoutInputForAxis(padding.horizontal + CellSize.x, -1f, -1f, 0);
    }

    /// <summary>Unity layout callback; do not call directly.</summary>
    public override void CalculateLayoutInputVertical()
    {
        CalculateGrid();
        float height = padding.vertical + rows * CellSize.y
            + Mathf.Max(0, rows - 1) * Spacing.y;
        SetLayoutInputForAxis(height, height, 0f, 1);
    }

    /// <summary>Unity layout callback; do not call directly.</summary>
    public override void SetLayoutHorizontal()
    {
        CalculateGrid();
        PositionChildren();
    }

    /// <summary>Unity layout callback; do not call directly.</summary>
    public override void SetLayoutVertical()
    {
        CalculateGrid();
        PositionChildren();
    }

    protected override void OnRectTransformDimensionsChange()
    {
        base.OnRectTransformDimensionsChange();
        if (isActiveAndEnabled)
            LayoutRebuilder.MarkLayoutForRebuild(rectTransform);
    }

    private void CalculateGrid()
    {
        float width = Mathf.Max(CellSize.x,
            rectTransform.rect.width - padding.horizontal);
        columns = Mathf.Max(1, Mathf.FloorToInt(
            (width + Spacing.x) / (CellSize.x + Spacing.x)));
        rows = rectChildren.Count == 0 ? 0
            : Mathf.CeilToInt(rectChildren.Count / (float)columns);
    }

    private void PositionChildren()
    {
        float available = rectTransform.rect.width - padding.horizontal;
        for (int row = 0; row < rows; row++)
        {
            int first = row * columns;
            int count = Mathf.Min(columns, rectChildren.Count - first);
            float rowWidth = count * CellSize.x
                + Mathf.Max(0, count - 1) * Spacing.x;
            float start = padding.left
                + Mathf.Max(0f, (available - rowWidth) * 0.5f);
            for (int column = 0; column < count; column++)
            {
                RectTransform child = rectChildren[first + column];
                SetChildAlongAxis(child, 0,
                    start + column * (CellSize.x + Spacing.x), CellSize.x);
                SetChildAlongAxis(child, 1,
                    padding.top + row * (CellSize.y + Spacing.y), CellSize.y);
            }
        }
    }
}
