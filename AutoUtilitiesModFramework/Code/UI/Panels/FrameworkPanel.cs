using System;
using System.Collections.Generic;
using Game.Constants;
using Game.Systems.UIUX;
using Game.UI;
using Game.Utils;
using KL.Pool;
using KL.UI;
using KL.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AutoUtilitiesModFramework;

/// <summary>Configuration used when a <see cref="FrameworkPanel"/> is created.</summary>
public sealed class FrameworkPanelOptions
{
    /// <summary>Stable consumer-unique ID, such as <c>MyMod.Settings</c>.</summary>
    public string Id { get; set; }
    /// <summary>Text displayed in the panel title bar.</summary>
    public string Title { get; set; }
    /// <summary>Smallest size allowed by the resize handle.</summary>
    public Vector2 MinSize { get; set; } = new Vector2(320f, 220f);
    /// <summary>Largest size allowed by the resize handle.</summary>
    public Vector2 MaxSize { get; set; } = new Vector2(1400f, 1000f);
    /// <summary>Retains position and size while this panel instance lives.</summary>
    public bool RememberGeometry { get; set; } = true;
    /// <summary>Optional callback invoked after a player-requested close.</summary>
    public Action OnClosed { get; set; }
}

/// <summary>Identifies one tab and its displayed label.</summary>
public sealed class PanelTabDefinition
{
    /// <summary>Stable value stored by the consumer as its active tab.</summary>
    public string Id { get; set; }
    /// <summary>Player-facing tab label.</summary>
    public string Label { get; set; }
}

/// <summary>
/// Owns a native Stardeus popup, its lifetimes, content rebuilding, resizing,
/// and optional position/size retention.
/// </summary>
public sealed class FrameworkPanel : IDisposable
{
    private readonly FrameworkPanelOptions options;
    private readonly Action<PanelContentContext> buildContent;
    private readonly List<GameObject> ownedContent = new List<GameObject>();
    private Lifetime panelLifetime;
    private Lifetime contentLifetime;
    private WPanel widget;
    private FreeResizeHandle frameworkResizeHandle;
    private FrameworkPanelContentHeightFitter frameworkContentHeightFitter;
    private Behaviour nativeResizeHandle;
    private Behaviour nativeHeightAdjuster;
    private Vector2? rememberedPosition;
    private Vector2 rememberedSize;
    private bool refreshScheduled;
    private bool refreshPending;
    private bool rebuilding;
    private int panelGeneration;

    /// <summary>Whether the panel currently has a native popup instance.</summary>
    public bool IsOpen => widget != null;
    /// <summary>The live popup rectangle, or <see langword="null"/> when closed.</summary>
    public RectTransform Rect => widget?.Rect;

    /// <summary>Creates a reusable panel controller; it does not open immediately.</summary>
    /// <param name="options">Identity, title, resize bounds, and close behavior.</param>
    /// <param name="buildContent">
    /// Called on show and refresh. Recreate all rows from current consumer state.
    /// </param>
    /// <example><code>
    /// panel = new FrameworkPanel(
    ///     new FrameworkPanelOptions { Id = "MyMod.Settings", Title = "My Mod" },
    ///     ui =&gt; {
    ///         ui.AddHeader("Controls");
    ///         ui.AddToggle(icon, "Enabled", enabled ? "On" : "Off", enabled,
    ///             value =&gt; { enabled = value; panel.Refresh(); widget.Refresh(); });
    ///     });
    /// </code></example>
    public FrameworkPanel(FrameworkPanelOptions options,
        Action<PanelContentContext> buildContent)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.buildContent = buildContent
            ?? throw new ArgumentNullException(nameof(buildContent));
        if (string.IsNullOrWhiteSpace(options.Id))
            throw new ArgumentException("Panel ID is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Title))
            throw new ArgumentException("Panel title is required.", nameof(options));
    }

    /// <summary>Opens a closed panel or closes an open panel.</summary>
    public void Toggle()
    {
        if (IsOpen) Close(); else Show();
    }

    /// <summary>Opens the panel and builds its content from current state.</summary>
    public void Show()
    {
        Close(false);
        panelGeneration++;
        panelLifetime = Lifetime.Create(options.Id + ".Panel");
        widget = UISpawn.Widget<WPanel>(panelLifetime,
            UIPrefabId.WListChoicePopup, UIPoolRoot.PopupContainer, 0);
        InstallManagedContentHeight();
        InstallFreeResize();
        widget.Rect.SetAsLastSibling();
        widget.Load(options.Title, Close);
        widget.ShowFooter(false);
        RebuildNow();

        if (options.RememberGeometry && rememberedPosition.HasValue)
        {
            widget.Rect.anchoredPosition = rememberedPosition.Value;
            widget.Rect.sizeDelta = rememberedSize;
        }
        else
        {
            UIUtils.AfterShowPopup(widget.Rect, true, 1f, true, false);
        }
    }

    /// <summary>
    /// Requests a content rebuild while preserving the popup. Requests made in
    /// the same frame are coalesced and rebuilt on the next UI frame, after the
    /// current input/button callback has finished. Safe to call directly from
    /// control callbacks. This method has no effect while closed.
    /// </summary>
    public void Refresh()
    {
        if (widget == null) return;
        refreshPending = true;
        if (refreshScheduled) return;
        refreshScheduled = true;
        int scheduledGeneration = panelGeneration;
        Coroutines.OnNextFrame(() =>
        {
            // A deferred callback can outlive a close/reopen. Never let an old
            // panel generation alter the scheduling state of the new panel.
            if (scheduledGeneration != panelGeneration) return;
            refreshScheduled = false;
            if (!refreshPending || widget == null) return;
            refreshPending = false;
            RebuildNow();
        });
    }

    /// <summary>Closes the popup and invokes <see cref="FrameworkPanelOptions.OnClosed"/>.</summary>
    public void Close() => Close(true);

    private void Close(bool notify)
    {
        panelGeneration++;
        refreshScheduled = false;
        refreshPending = false;
        if (widget != null && options.RememberGeometry)
        {
            rememberedPosition = widget.Rect.anchoredPosition;
            rememberedSize = widget.Rect.sizeDelta;
        }
        ClearContent();
        RestoreResizeHandle();
        RestoreManagedContentHeight();
        panelLifetime?.Dispose();
        panelLifetime = null;
        bool wasOpen = widget != null;
        widget = null;
        if (notify && wasOpen) options.OnClosed?.Invoke();
    }

    private void RebuildNow()
    {
        if (widget == null || rebuilding) return;
        rebuilding = true;
        try
        {
            ClearContent();
            contentLifetime = panelLifetime.CreateChild(options.Id + ".Content");
            WContentBlock block = UISpawn.Block(contentLifetime, widget.Content, 0);
            frameworkContentHeightFitter?.Bind(block);
            var context = new PanelContentContext(contentLifetime,
                block.Container, ownedContent, Refresh);
            buildContent(context);
            frameworkContentHeightFitter?.RefreshNow();
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(block.Container);
            LayoutRebuilder.ForceRebuildLayoutImmediate(widget.Content.Rect);
        }
        finally
        {
            rebuilding = false;
        }
    }

    private void ClearContent()
    {
        ReleaseOwnedTextInputFocus();

        // Restore the pooled block's native layout state before its lifetime
        // returns it to Stardeus's global pool.
        frameworkContentHeightFitter?.ClearContentBlock();

        // Custom roots can be children of a pooled WContentBlock or can contain
        // native lifetime-owned rows (for example, a vertical scroll view).
        // Detach and hide those roots first so they cannot travel into another
        // panel when the native block is returned to the global UI pool.
        foreach (GameObject item in ownedContent)
        {
            if (item == null) continue;
            item.SetActive(false);
            item.transform.SetParent(null, worldPositionStays: true);
        }

        // With the custom roots isolated, return every native widget to its
        // pool, then synchronously remove the framework-owned containers. The
        // public Refresh path ensures this never runs inside the callback of a
        // native control being returned.
        contentLifetime?.Dispose();
        contentLifetime = null;
        foreach (GameObject item in ownedContent)
        {
            if (item == null) continue;
            PreserveDespawnedPoolMembers(item);
            UnityEngine.Object.DestroyImmediate(item);
        }
        ownedContent.Clear();
    }

    private void ReleaseOwnedTextInputFocus()
    {
        TMP_InputField field = Game.Ready.TextInputFocusField;
        RectTransform content = widget?.Content?.Rect;
        if (field == null || content == null
            || !field.transform.IsChildOf(content))
        {
            return;
        }

        // Core normally releases Ready.TextInputFocus from
        // UITextInputFocus.Update after TMP loses focus. A deferred panel
        // rebuild can return the field to the pool before that Update runs;
        // UITextInputFocus.OnDisable then clears only its local state. Release
        // this panel's field while it is still active so Core never retains an
        // inactive pooled input as the global text-focus owner.
        if (field.isFocused)
        {
            UIActivator.EnsureDeselected(field.gameObject);
        }
        if (Game.Ready.TextInputFocusField == field)
        {
            Game.Ready.SetTextInputFocus(true, field,
                "AUMF FrameworkPanel content teardown");
        }
    }

    private static void PreserveDespawnedPoolMembers(GameObject ownedRoot)
    {
        // PrefabPool.Despawn marks a native widget as available but does not
        // guarantee that it is immediately reparented. If it remains beneath
        // a custom scroll/flow root, destroying that root also destroys the
        // pool's registered GameObject. Move only top-level pooled roots to a
        // permanent UI container after lifetime disposal and before removing
        // the framework-owned hierarchy.
        PrefabPool.PoolMember[] members =
            ownedRoot.GetComponentsInChildren<PrefabPool.PoolMember>(true);
        foreach (PrefabPool.PoolMember member in members)
        {
            if (member == null) continue;
            Transform ancestor = member.transform.parent;
            bool nestedUnderPoolMember = false;
            while (ancestor != null && ancestor != ownedRoot.transform)
            {
                if (ancestor.GetComponent<PrefabPool.PoolMember>() != null)
                {
                    nestedUnderPoolMember = true;
                    break;
                }
                ancestor = ancestor.parent;
            }
            if (!nestedUnderPoolMember)
                member.transform.SetParent(UIPoolRoot.Container,
                    worldPositionStays: true);
        }
    }

    private void InstallFreeResize()
    {
        Transform handle = UIComponentFactory.FindChildRecursive(
            widget.transform, "ResizeHandle");
        if (handle == null) return;

        // WListChoicePopup is pooled globally. A framework component left on
        // this object is inherited by whichever popup opens next, including a
        // different consumer with different bounds. Remove stale components
        // left by earlier AUMF builds before installing this panel's handler.
        foreach (FreeResizeHandle stale in
            handle.GetComponents<FreeResizeHandle>())
        {
            if (stale != null)
                UnityEngine.Object.DestroyImmediate(stale);
        }

        foreach (Component component in handle.GetComponents<Component>())
        {
            if (component != null
                && component.GetType().FullName == "Game.UI.UIResizeHandle")
            {
                // The native handler belongs to the pooled prefab. Disable it
                // only while AUMF supplies free resizing, then restore it before
                // the popup is returned to Stardeus's pool.
                nativeResizeHandle = component as Behaviour;
                if (nativeResizeHandle != null)
                    nativeResizeHandle.enabled = false;
            }
        }
        frameworkResizeHandle =
            handle.gameObject.AddComponent<FreeResizeHandle>();
        frameworkResizeHandle.MinSize = options.MinSize;
        frameworkResizeHandle.MaxSize = options.MaxSize;
        frameworkResizeHandle.SizeChanged =
            _ => frameworkContentHeightFitter?.RefreshNow();
        Component adjuster = widget.GetComponent("UIHeightAdjuster");
        nativeHeightAdjuster = adjuster as Behaviour;
        if (nativeHeightAdjuster != null)
            nativeHeightAdjuster.enabled = false;
    }

    private void RestoreResizeHandle()
    {
        // This component is panel-instance state attached to a globally pooled
        // object. It must not survive panel close or carry these bounds into a
        // later AUMF or vanilla popup.
        if (frameworkResizeHandle != null)
        {
            frameworkResizeHandle.SizeChanged = null;
            UnityEngine.Object.DestroyImmediate(frameworkResizeHandle);
        }
        frameworkResizeHandle = null;

        if (nativeResizeHandle != null)
            nativeResizeHandle.enabled = true;
        nativeResizeHandle = null;

        if (nativeHeightAdjuster != null)
            nativeHeightAdjuster.enabled = true;
        nativeHeightAdjuster = null;
    }

    private void InstallManagedContentHeight()
    {
        // WListChoicePopup is pooled globally. Remove any stale framework
        // fitter before binding this panel instance to the native viewport.
        foreach (FrameworkPanelContentHeightFitter stale in
            widget.GetComponents<FrameworkPanelContentHeightFitter>())
        {
            if (stale != null)
                UnityEngine.Object.DestroyImmediate(stale);
        }

        frameworkContentHeightFitter =
            widget.gameObject.AddComponent<FrameworkPanelContentHeightFitter>();
        frameworkContentHeightFitter.Initialize(widget.Scroll?.viewport);
    }

    private void RestoreManagedContentHeight()
    {
        if (frameworkContentHeightFitter != null)
            UnityEngine.Object.DestroyImmediate(frameworkContentHeightFitter);
        frameworkContentHeightFitter = null;
    }

    /// <summary>
    /// Releases the panel without raising the close callback. Call from the
    /// consumer system's <c>Unload()</c> method.
    /// </summary>
    public void Dispose() => Close(false);
}

/// <summary>Convenience builders available during a panel content rebuild.</summary>
public sealed class PanelContentContext
{
    private readonly List<GameObject> owned;
    private readonly Action rebuild;
    private int row;

    /// <summary>Lifetime for native widgets created during this rebuild.</summary>
    public Lifetime Lifetime { get; }
    /// <summary>Parent for custom content created during this rebuild.</summary>
    public RectTransform Container { get; }
    /// <summary>Next sibling row index that a custom builder should use.</summary>
    public int NextRow => row;

    internal PanelContentContext(Lifetime lifetime, RectTransform container,
        List<GameObject> owned, Action rebuild)
    {
        Lifetime = lifetime;
        Container = container;
        this.owned = owned;
        this.rebuild = rebuild;
    }

    /// <summary>Adds a native section header and advances the row.</summary>
    /// <example><code>ui.AddHeader("Automation");</code></example>
    public WText AddHeader(string text)
    {
        WText result = UISpawn.Widget<WText>(Lifetime,
            UIPrefabId.WContent2Header, Container, row++);
        result.Load(text);
        // WContent2Header is pooled globally. An ordinary header can inherit a
        // decorator component from a prior collapsible use, so explicitly
        // clear and hide that decoration every time AddHeader reuses one.
        result.GetComponent<CollapsibleHeaderDecorator>()
            ?.ResetConfiguration();
        return result;
    }

    /// <summary>
    /// Adds a native expandable/collapsible section header and advances the
    /// normal panel row. The consumer owns the expanded state and decides
    /// which following rows to render.
    /// </summary>
    /// <remarks>
    /// Available beginning with AUMF 1.3.0. The callback must store the new
    /// state. AUMF automatically requests its deferred, coalesced panel rebuild
    /// after the callback returns, so consumers must not schedule another
    /// refresh for the same click.
    /// </remarks>
    /// <example><code>
    /// ui.AddCollapsibleHeader("Advanced", advancedExpanded,
    ///     value =&gt; advancedExpanded = value,
    ///     advancedExpanded ? "Collapse Advanced" : "Expand Advanced");
    /// if (advancedExpanded) ui.AddInfo(icon, "Mode", currentMode);
    /// </code></example>
    public WText AddCollapsibleHeader(string text, bool expanded,
        Action<bool> changed, string tooltip = null, bool interactable = true)
    {
        return AddCollapsibleHeader(Container, row++, text, expanded, changed,
            tooltip, interactable);
    }

    /// <summary>
    /// Adds a native expandable/collapsible section header to an explicit
    /// parent, such as <see cref="VerticalScrollViewResult.Content"/>.
    /// </summary>
    /// <param name="parent">Parent that receives the native pooled header.</param>
    /// <param name="siblingIndex">Sibling index within <paramref name="parent"/>.</param>
    /// <param name="text">Player-facing section title.</param>
    /// <param name="expanded">Current consumer-owned expanded state.</param>
    /// <param name="changed">Stores the new state selected by the player.</param>
    /// <param name="tooltip">Optional whole-header tooltip.</param>
    /// <param name="interactable">
    /// Whether the player may change the state. Use <see langword="false"/>
    /// when a consumer temporarily forces a section open, such as during search.
    /// </param>
    /// <returns>The native pooled header text control.</returns>
    /// <remarks>
    /// Available beginning with AUMF 1.3.0. This overload does not advance the
    /// normal panel row counter. AUMF decorates the native WContent2Header with
    /// a reusable disclosure indicator and whole-row click target. The
    /// decorator clears its callback, tooltip, raycast target, and indicator
    /// when the pooled header is disabled. The enclosing panel is rebuilt on
    /// the next UI frame after <paramref name="changed"/> returns.
    /// </remarks>
    /// <example><code>
    /// int row = 0;
    /// ui.AddCollapsibleHeader(scroll.Content, row++, "Weapons", expanded,
    ///     value =&gt; expanded = value, "Show or hide Weapons");
    /// </code></example>
    public WText AddCollapsibleHeader(RectTransform parent,
        int siblingIndex, string text, bool expanded, Action<bool> changed,
        string tooltip = null, bool interactable = true)
    {
        if (parent == null)
            throw new ArgumentNullException(nameof(parent));
        if (changed == null)
            throw new ArgumentNullException(nameof(changed));

        WText result = UISpawn.Widget<WText>(Lifetime,
            UIPrefabId.WContent2Header, parent, siblingIndex);
        result.Load(text);
        CollapsibleHeaderDecorator decorator =
            result.GetComponent<CollapsibleHeaderDecorator>()
            ?? result.gameObject.AddComponent<CollapsibleHeaderDecorator>();
        decorator.Configure(result, expanded, () =>
        {
            try
            {
                changed(!expanded);
            }
            finally
            {
                // Refresh is deferred to the next frame, so the native pooled
                // row is never disposed from inside its own click callback.
                rebuild();
            }
        }, tooltip, interactable);
        return result;
    }

    /// <summary>Adds a read-only icon, label, and value row.</summary>
    /// <example><code>ui.AddInfo(infoIcon, "Targets", targetCount.ToString());</code></example>
    public WIconText2 AddInfo(string icon, string label, string value,
        string tooltip = null)
    {
        WIconText2 result = UISpawn.Widget<WIconText2>(Lifetime,
            UIPrefabId.WContent2Text2, Container, row++);
        result.Load(icon, label, value);
        UIComponentFactory.SetTip(result, tooltip);
        const string hitAreaName = "AutoUtilities_InfoTooltipHitArea";
        Transform existingHitArea = result.transform.Find(hitAreaName);
        if (string.IsNullOrEmpty(tooltip))
        {
            // WIconText2 is pooled by the game. A row that previously had a
            // framework tooltip may later be reused for a row without one.
            if (existingHitArea != null)
                existingHitArea.gameObject.SetActive(false);
        }
        else
        {
            // Native WIconText2 labels and values do not participate in UI
            // raycasts; only the icon reliably receives pointer events. A
            // transparent, full-row hit area makes the tooltip available over
            // the displayed label/value without changing the native visuals.
            // AddInfo rows are read-only, so this cannot cover an interactive
            // control. Buttons, toggles, and inputs use their own builders.
            // Reuse the framework hit area when this pooled native row returns
            // during a later rebuild. Creating another child every time would
            // accumulate overlapping raycast targets and hover callbacks.
            GameObject hitAreaObject;
            if (existingHitArea == null)
            {
                hitAreaObject = new GameObject(hitAreaName);
                hitAreaObject.transform.SetParent(result.transform, false);
            }
            else
            {
                hitAreaObject = existingHitArea.gameObject;
                hitAreaObject.SetActive(true);
            }
            RectTransform hitArea =
                hitAreaObject.GetComponent<RectTransform>()
                ?? hitAreaObject.AddComponent<RectTransform>();
            hitArea.anchorMin = Vector2.zero;
            hitArea.anchorMax = Vector2.one;
            hitArea.offsetMin = Vector2.zero;
            hitArea.offsetMax = Vector2.zero;
            hitArea.SetAsLastSibling();
            var hitImage = hitAreaObject.GetComponent<Image>()
                ?? hitAreaObject.AddComponent<Image>();
            // A tiny nonzero alpha keeps the object registered with Unity's
            // GraphicRaycaster while remaining visually indistinguishable.
            hitImage.color = new Color(1f, 1f, 1f, 0.001f);
            hitImage.raycastTarget = true;
            var relay = hitAreaObject.GetComponent<TooltipHoverRelay>()
                ?? hitAreaObject.AddComponent<TooltipHoverRelay>();
            relay.Text = tooltip;
        }
        return result;
    }

    /// <summary>Adds a native toggle row and calls <paramref name="changed"/>.</summary>
    /// <remarks>Refresh the panel in the callback when its displayed state changed.</remarks>
    /// <example><code>
    /// ui.AddToggle(statusIcon, "Enable automation", enabled ? "On" : "Off",
    ///     enabled, value =&gt; { enabled = value; panel.Refresh(); });
    /// </code></example>
    public WToggleWithIconText2 AddToggle(string icon, string label,
        string value, bool enabled, Action<bool> changed,
        string tooltip = null)
    {
        WToggleWithIconText2 result =
            UISpawn.Widget<WToggleWithIconText2>(Lifetime,
                UIPrefabId.WContent2Text2Toggle, Container, row++);
        result.Item.Load(icon, label, value);
        result.Toggle.Load(enabled, changed);
        UIComponentFactory.SetTip(result, tooltip);
        return result;
    }

    /// <summary>Adds a native button row.</summary>
    /// <example><code>
    /// ui.AddButton(actionIcon, "Scan now", "", () =&gt; { Scan(); panel.Refresh(); });
    /// </code></example>
    public WButtonIconText2 AddButton(string icon, string label,
        string value, Action clicked, string tooltip = null,
        string prefabId = null)
    {
        WButtonIconText2 result = UISpawn.Widget<WButtonIconText2>(Lifetime,
            prefabId ?? UIPrefabId.WContent4Button, Container, row++);
        result.Load(icon, label, value, clicked);
        UIComponentFactory.SetTip(result, tooltip);
        return result;
    }

    /// <summary>
    /// Adds a native text field that commits once on Enter or loss of focus.
    /// </summary>
    /// <remarks>
    /// AUMF removes its end-edit listener and releases the consumer callback
    /// when this content lifetime ends. Other input listeners are preserved.
    /// </remarks>
    /// <example><code>
    /// ui.AddInput(infoIcon, "Enter a name", savedName,
    ///     value =&gt; { savedName = value; panel.Refresh(); });
    /// </code></example>
    public WInputWithIcon AddInput(string icon, string placeholder,
        string value, Action<string> committed,
        TMP_InputField.ContentType contentType = TMP_InputField.ContentType.Standard,
        string tooltip = null)
    {
        WInputWithIcon result = UISpawn.Widget<WInputWithIcon>(Lifetime,
            "WContent2Input", Container, row++);
        result.Icon.Load(icon);
        result.Input.Load(placeholder, value, _ => { });
        result.Input.InputField.contentType = contentType;
        PooledInputCommitBinding.Bind(result.Input, Lifetime, committed);
        UIComponentFactory.SetTip(result, tooltip);
        return result;
    }

    /// <summary>Adds a tab row; selection automatically rebuilds the panel.</summary>
    /// <remarks>
    /// The callback must save the selected ID. Render the selected tab's rows
    /// immediately after this call in the same content-builder callback.
    /// </remarks>
    /// <example><code>
    /// ui.AddTabs(tabs, activeTab, id =&gt; activeTab = id);
    /// if (activeTab == "controls") BuildControls(ui); else BuildStatus(ui);
    /// </code></example>
    public RectTransform AddTabs(IEnumerable<PanelTabDefinition> tabs,
        string activeId, Action<string> selected)
    {
        GameObject root = CreateOwned("AutoUtilities_TabRow");
        RectTransform rect = root.AddComponent<RectTransform>();
        var size = root.AddComponent<LayoutElement>();
        size.minHeight = 52f;
        size.preferredHeight = 52f;
        size.flexibleHeight = 0f;
        var background = root.AddComponent<Image>();
        FrameworkTheme.ApplyImage(background, "TabPanelButtons");
        background.raycastTarget = false;
        var layout = root.AddComponent<HorizontalLayoutGroup>();
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = true;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.spacing = 8f;
        layout.padding = new RectOffset(8, 8, 10, 10);

        foreach (PanelTabDefinition tab in tabs)
        {
            bool active = string.Equals(activeId, tab.Id,
                StringComparison.Ordinal);
            UIComponentFactory.CreateThemedTabButton(rect, tab.Label, active, () =>
            {
                selected(tab.Id);
                rebuild();
            });
        }
        return rect;
    }

    /// <summary>Adds a centered fixed-cell container that reflows on resize.</summary>
    /// <returns>A parent rectangle to which the consumer adds card objects.</returns>
    /// <example><code>
    /// RectTransform cards = ui.AddResponsiveFlow("Cards",
    ///     new Vector2(150f, 76f), new Vector2(8f, 8f));
    /// card.transform.SetParent(cards, false);
    /// </code></example>
    public RectTransform AddResponsiveFlow(string name, Vector2 cellSize,
        Vector2 spacing, RectOffset padding = null)
    {
        GameObject root = CreateOwned(name);
        RectTransform rect = root.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.sizeDelta = Vector2.zero;
        var flow = root.AddComponent<ResponsiveFlowLayout>();
        flow.CellSize = cellSize;
        flow.Spacing = spacing;
        flow.padding = padding ?? new RectOffset(6, 6, 6, 6);
        return rect;
    }

    /// <summary>Adds a vertical scroll area managed by this content rebuild.</summary>
    /// <remarks>
    /// Store the reported normalized position in consumer state and pass it
    /// back as <paramref name="initialPosition"/> on the next rebuild.
    /// Inside a <see cref="FrameworkPanel"/>, the managed content block follows
    /// the popup's visible viewport height. Scrolls with positive flexible
    /// height therefore consume resized space while fixed rows keep their
    /// preferred sizes. Consumer height values remain layout preferences.
    /// </remarks>
    /// <example><code>
    /// var scroll = ui.AddVerticalScroll("Log", 300f, 600f, 1f,
    ///     savedScroll, value =&gt; savedScroll = value);
    /// row.transform.SetParent(scroll.Content, false);
    /// </code></example>
    public VerticalScrollViewResult AddVerticalScroll(string name,
        float minWidth, float preferredWidth, float flexibleWidth,
        float initialPosition = 1f, Action<float> positionChanged = null)
    {
        VerticalScrollViewResult result = VerticalScrollView.Create(Container,
            name, minWidth, preferredWidth, flexibleWidth, initialPosition,
            positionChanged);
        result.Root.SetSiblingIndex(row++);
        owned.Add(result.Root.gameObject);
        return result;
    }

    /// <summary>
    /// Creates a custom row owned by this rebuild. The framework destroys it
    /// automatically on refresh or close.
    /// </summary>
    /// <example><code>
    /// GameObject customRow = ui.CreateOwned("MyCustomRow");
    /// customRow.AddComponent&lt;LayoutElement&gt;().preferredHeight = 40f;
    /// </code></example>
    public GameObject CreateOwned(string name)
    {
        var result = new GameObject(name);
        result.transform.SetParent(Container, false);
        result.transform.SetSiblingIndex(row++);
        owned.Add(result);
        return result;
    }
}
