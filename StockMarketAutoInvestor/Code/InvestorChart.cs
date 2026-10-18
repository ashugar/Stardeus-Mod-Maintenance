using System;
using System.Collections.Generic;
using AutoUtilitiesModFramework;
using Game;
using Game.Rendering;
using Game.Systems.UIUX;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StockMarketAutoInvestor;

internal sealed class ChartSeries
{
    internal string Title;
    internal string Caption;
    internal List<RTChartNode> Nodes = new();
    internal bool Money = true;
}

// Exists only beneath an open AUMF panel. It never collects market observations.
public sealed class InvestorChart : MonoBehaviour
{
    private RawImage image;
    private TMP_Text title;
    private TMP_Text caption;
    private RTChart chart;
    private RenderTexture texture;
    private ChartSeries series;
    private bool dirty = true;
    private bool tooltip;
    private Vector2 lastPointer = new(float.NaN, float.NaN);
    private bool failed;

    internal static InvestorChart Create(RectTransform parent)
    {
        var root = new GameObject("InvestmentGraph", typeof(RectTransform));
        root.transform.SetParent(parent, false);
        var rect = (RectTransform)root.transform;
        var size = root.AddComponent<LayoutElement>();
        size.minWidth = 350; size.preferredWidth = 430; size.flexibleWidth = 1;
        var layout = root.AddComponent<VerticalLayoutGroup>();
        layout.childControlHeight = layout.childControlWidth = true;
        layout.childForceExpandHeight = false; layout.childForceExpandWidth = true;
        layout.spacing = 8;
        InvestorChart view = root.AddComponent<InvestorChart>();
        view.title = Label(rect, "Market history", 22, 54);
        var graph = new GameObject("PriceChart", typeof(RectTransform));
        graph.transform.SetParent(rect, false);
        view.image = graph.AddComponent<RawImage>();
        view.image.raycastTarget = true;
        var chartRect = (RectTransform)graph.transform;
        chartRect.pivot = new Vector2(0, 1);
        var chartSize = graph.AddComponent<LayoutElement>();
        chartSize.minHeight = 260; chartSize.preferredHeight = 360; chartSize.flexibleHeight = 1;
        view.caption = Label(rect, "Hover over an asset to display its history.", 16, 105);
        root.AddComponent<InvestorChartTheme>().View = view;
        return view;
    }

    internal static TMP_Text Label(RectTransform parent, string text, float fontSize, float height)
    {
        var go = new GameObject("Text", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var label = go.AddComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = fontSize;
        label.textWrappingMode = TextWrappingModes.Normal;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.raycastTarget = false;
        FrameworkTheme.ApplyText(label, "Unsized");
        var size = go.AddComponent<LayoutElement>();
        size.minHeight = height; size.preferredHeight = height; size.flexibleHeight = 0;
        return label;
    }

    internal void Show(ChartSeries value)
    {
        if (ReferenceEquals(series, value)) return;
        series = value;
        title.text = value.Title;
        caption.text = value.Caption;
        dirty = true;
        Render(); // Hover uses the existing chart immediately, without rebuilding the panel.
    }

    internal void Invalidate() { Release(); dirty = true; }
    private void OnRectTransformDimensionsChange() => dirty = true;

    private void LateUpdate()
    {
        if (failed || image == null) return;
        if (texture == null || texture.width != Mathf.RoundToInt(image.rectTransform.rect.width)
            || texture.height != Mathf.RoundToInt(image.rectTransform.rect.height)) dirty = true;
        if (dirty) Render();
        if (chart == null || texture == null) return;
        Vector2 pointer = The.Bindings.Mouse.ScreenPosition;
        var first = The.Bindings.Mouse.FirstUnderCursor;
        bool over = first != null && first.transform.IsChildOf(image.transform);
        if (!over) { HideTooltip(); return; }
        if (tooltip && pointer == lastPointer) return;
        lastPointer = pointer;
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(image.rectTransform, pointer, null, out Vector2 local))
        {
            local += new Vector2(0, texture.height);
            string text = chart.TooltipAt(local);
            if (string.IsNullOrEmpty(text)) { HideTooltip(); return; }
            The.SysSig.ShowTooltip.Send(text, true);
            tooltip = true;
        }
    }

    private void Render()
    {
        if (failed || image == null || series == null || !isActiveAndEnabled) return;
        int width = Mathf.RoundToInt(image.rectTransform.rect.width);
        int height = Mathf.RoundToInt(image.rectTransform.rect.height);
        if (width < 2 || height < 2) return;
        try
        {
            if (texture == null || texture.width != width || texture.height != height)
            {
                Release();
                texture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                image.texture = texture;
                chart = new RTChart(image.gameObject, texture,
                    The.UITheme.GetColor("ChartBackground", this), The.UITheme.GetColor("ChartGradient", this));
            }
            if (series.Nodes.Count == 0)
            {
                chart.Unload();
                var previous = RenderTexture.active;
                RenderTexture.active = texture;
                GL.Clear(true, true, The.UITheme.GetColor("ChartBackground", this));
                RenderTexture.active = previous;
            }
            else
            {
                chart.SetXDays(true);
                bool money = series.Money;
                chart.TooltipFunc = (xy, value, minX, minY, maxX, maxY, nodes) =>
                    nodes.Count == 1 ? $"Market day {Mathf.FloorToInt(value.x)}\n"
                        + (money ? Game.Data.Economy.Wallet.ToValueString(nodes[0].ValueAt(xy.x), false)
                            : nodes[0].ValueAt(xy.x).ToString("0.000")) : null;
                chart.Render(series.Nodes, true, value => money
                    ? Game.Data.Economy.Wallet.ToValueString(value) : value.ToString("0.000"));
            }
            dirty = false;
            lastPointer = new Vector2(float.NaN, float.NaN);
        }
        catch (Exception ex)
        {
            failed = true;
            caption.text = "Chart unavailable. See Player.log.";
            Release();
            KL.Utils.D.LogEx(ex, "StockMarketAutoInvestor: chart rendering failed.");
        }
    }

    private void HideTooltip()
    {
        if (!tooltip) return;
        tooltip = false;
        The.SysSig.HideTooltip.Send();
    }
    private void Release()
    {
        HideTooltip();
        chart?.Unload(); chart = null;
        if (image != null) image.texture = null;
        if (texture != null) { RenderTexture.ReleaseTemporary(texture); texture = null; }
    }
    private void OnDisable() => Release();
    private void OnDestroy() => Release();
}

public sealed class InvestorChartTheme : UIThemedImage
{
    internal InvestorChart View;
    protected override void ApplyTheme() => View?.Invalidate();
}

// Only the displayed panel has a watcher. Closing it destroys this component.
public sealed class InvestorPanelWatch : MonoBehaviour
{
    internal StockMarketAutoInvestorSys System;
    internal Action Refresh;
    internal Action ScrollHover;
    internal Action RefreshValues;
    internal Func<string> WatchedAsset;
    internal bool ScrollDirty;
    private int day = int.MinValue;
    private int revision = -1;
    private int research;
    private string lastAsset;
    private int positions;
    private long shares;
    private double cost;
    private float nextHoldingsCheck;

    internal void Prime()
    {
        day = System.State?.Market?.Day ?? -1;
        revision = System.Revision;
        research = System.ResearchMask;
        positions = System.State?.Portfolio?.Positions?.Count ?? 0;
        lastAsset = WatchedAsset?.Invoke();
        ReadHolding(out shares, out cost);
    }

    private void LateUpdate()
    {
        if (System == null) return;
        if (day != (System.State?.Market?.Day ?? -1) || revision != System.Revision
            || positions != (System.State?.Portfolio?.Positions?.Count ?? 0))
        { Prime(); Refresh?.Invoke(); }
        if (Time.unscaledTime >= nextHoldingsCheck)
        {
            nextHoldingsCheck = Time.unscaledTime + .25f;
            RefreshValues?.Invoke();
            string currentAsset = WatchedAsset?.Invoke();
            bool changedAsset = currentAsset != lastAsset;
            lastAsset = currentAsset;
            ReadHolding(out long currentShares, out double currentCost);
            bool changedHoldings = !changedAsset && (currentShares != shares || currentCost != cost);
            shares = currentShares; cost = currentCost;
            int currentResearch = System.ResearchMask;
            if (changedHoldings || research != currentResearch)
            { research = currentResearch; Refresh?.Invoke(); }
        }
        if (ScrollDirty) { ScrollDirty = false; ScrollHover?.Invoke(); }
    }

    private void ReadHolding(out long count, out double basis)
    {
        var asset = System.Find(WatchedAsset?.Invoke());
        count = 0; basis = 0;
        if (asset != null) System.Holdings(asset.IdH, out count, out basis, out _);
    }
}
