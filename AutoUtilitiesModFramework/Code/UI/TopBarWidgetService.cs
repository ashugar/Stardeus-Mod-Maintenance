using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game;
using Game.Systems.UIUX;
using Game.UI;
using KL.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AutoUtilitiesModFramework;

internal static class TopBarWidgetService
{
    private static readonly FieldInfo PermanentTipIsShowingField =
        typeof(UIPermanentTip).GetField("isShowing",
            BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo ButtonIconImageField =
        typeof(WButtonIcon).GetField("image",
            BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo ElectricityWidgetField =
        typeof(TopBarWidget).GetField("electricityWidget",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly Dictionary<string, WidgetHandle> Widgets =
        new Dictionary<string, WidgetHandle>(StringComparer.Ordinal);
    private static WidgetHandle[] orderedWidgets = Array.Empty<WidgetHandle>();
    private static ElectricityMiniChartWidget fallbackElectricity;
    private static bool layoutDirty = true;

    internal static ITopBarWidgetHandle Register(
        TopBarWidgetRegistration registration)
    {
        Validate(registration);
        if (Widgets.ContainsKey(registration.Id))
        {
            throw new InvalidOperationException(
                "A top-bar widget is already registered with ID '"
                + registration.Id + "'.");
        }

        var handle = new WidgetHandle(registration);
        Widgets.Add(registration.Id, handle);
        RebuildOrderedSnapshot();
        TopBarWidgetSys.RequestEnsure();
        return handle;
    }

    /// <summary>
    /// Returns an existing handle without changing its registration. This is
    /// used by framework-owned widgets whose registration can be re-entered by
    /// the synchronous ensure request made from <see cref="Register"/>.
    /// </summary>
    internal static ITopBarWidgetHandle FindRegistered(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return Widgets.TryGetValue(id, out WidgetHandle handle)
            ? handle
            : null;
    }

    internal static void EnsureAll(Game.Data.GameState state)
    {
        WidgetHandle[] snapshot = orderedWidgets;
        if (state == null || snapshot.Length == 0) return;

        WidgetRegistry registry = state.UIQuery.GetWidgetRegistry.Ask();
        TopRightWidget topRight = registry?.TopRight;
        TopBarWidget topBar = registry?.TopBar;
        ElectricityMiniChartWidget electricity = topBar == null
            ? null : WidgetHandle.FindElectricity(topBar);
        Transform parent = electricity?.transform.parent;
        if (parent == null) return;

        WButtonIcon source = null;
        bool sourceUnavailable = false;
        for (int i = 0; i < snapshot.Length; i++)
        {
            WidgetHandle handle = snapshot[i];
            if (handle.IsCreatedUnder(parent)) continue;

            if (source == null && !sourceUnavailable)
            {
                source = FindSourceButton(topRight);
                sourceUnavailable = source == null;
            }
            if (source == null) continue;
            if (handle.EnsureCreated(parent, electricity, source))
                layoutDirty = true;
        }

        ReflowIfNeeded(snapshot, parent, electricity);
    }

    private static WButtonIcon FindSourceButton(TopRightWidget topRight)
    {
        if (topRight == null) return null;
        WButtonIcon[] buttons =
            topRight.GetComponentsInChildren<WButtonIcon>(true);
        return buttons == null || buttons.Length == 0
            ? null : buttons[buttons.Length - 1];
    }

    private static void ReflowIfNeeded(WidgetHandle[] snapshot,
        Transform parent, ElectricityMiniChartWidget electricity)
    {
        if (!(electricity.transform is RectTransform electricityRect))
            return;

        if (parent.GetComponent<LayoutGroup>() != null)
        {
            if (layoutDirty
                || !HasRegistrationSiblingOrder(snapshot, parent,
                    electricityRect))
            {
                RestoreRegistrationSiblingOrder(snapshot, parent,
                    electricityRect);
            }
            layoutDirty = false;
            return;
        }

        if (!layoutDirty
            && !NeedsManualReflow(snapshot, parent, electricityRect))
        {
            return;
        }

        RestoreRegistrationSiblingOrder(snapshot, parent, electricityRect);

        // AUMF widgets are inserted immediately before electricity. Walk the
        // cached registration order from right to left so the native widget can
        // expand or contract without rebuilding a filtered/sorted array.
        const float gap = 8f;
        const float tolerance = 0.1f;
        float rightEdge = WidgetHandle.LeftEdge(electricityRect) - gap;
        float centerY = electricityRect.anchoredPosition.y
            + (0.5f - electricityRect.pivot.y)
            * electricityRect.rect.height;

        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            WidgetHandle handle = snapshot[i];
            if (!handle.CanReflowUnder(parent)) continue;
            RectTransform rect = handle.ButtonRect;
            float width = handle.Width;
            Vector2 desiredPosition = new Vector2(
                rightEdge - width * 0.5f, centerY);
            Vector2 desiredSize = new Vector2(
                width, electricityRect.rect.height);

            rect.anchorMin = electricityRect.anchorMin;
            rect.anchorMax = electricityRect.anchorMax;
            rect.pivot = new Vector2(0.5f, 0.5f);
            if ((rect.anchoredPosition - desiredPosition).sqrMagnitude
                > tolerance * tolerance)
            {
                rect.anchoredPosition = desiredPosition;
            }
            if ((rect.sizeDelta - desiredSize).sqrMagnitude
                > tolerance * tolerance)
            {
                rect.sizeDelta = desiredSize;
            }
            rightEdge -= width + gap;
        }

        layoutDirty = false;
    }

    private static bool NeedsManualReflow(WidgetHandle[] snapshot,
        Transform parent, RectTransform electricityRect)
    {
        if (!HasRegistrationSiblingOrder(snapshot, parent, electricityRect))
            return true;

        const float gap = 8f;
        const float toleranceSquared = 0.01f;
        float rightEdge = WidgetHandle.LeftEdge(electricityRect) - gap;
        float centerY = electricityRect.anchoredPosition.y
            + (0.5f - electricityRect.pivot.y)
            * electricityRect.rect.height;

        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            WidgetHandle handle = snapshot[i];
            if (!handle.CanReflowUnder(parent)) continue;

            RectTransform rect = handle.ButtonRect;
            float width = handle.Width;
            Vector2 desiredPosition = new Vector2(
                rightEdge - width * 0.5f, centerY);
            Vector2 desiredSize = new Vector2(
                width, electricityRect.rect.height);

            if ((rect.anchorMin - electricityRect.anchorMin).sqrMagnitude
                    > toleranceSquared
                || (rect.anchorMax - electricityRect.anchorMax).sqrMagnitude
                    > toleranceSquared
                || (rect.pivot - new Vector2(0.5f, 0.5f)).sqrMagnitude
                    > toleranceSquared
                || (rect.anchoredPosition - desiredPosition).sqrMagnitude
                    > toleranceSquared
                || (rect.sizeDelta - desiredSize).sqrMagnitude
                    > toleranceSquared)
            {
                return true;
            }

            rightEdge -= width + gap;
        }

        return false;
    }

    private static bool HasRegistrationSiblingOrder(WidgetHandle[] snapshot,
        Transform parent, RectTransform electricityRect)
    {
        int electricityIndex = electricityRect.GetSiblingIndex();
        int previousIndex = -1;
        for (int i = 0; i < snapshot.Length; i++)
        {
            WidgetHandle handle = snapshot[i];
            if (!handle.IsCreatedUnder(parent)) continue;
            int siblingIndex = handle.SiblingIndex;
            if (siblingIndex <= previousIndex
                || siblingIndex >= electricityIndex)
            {
                return false;
            }
            previousIndex = siblingIndex;
        }
        return true;
    }

    private static void RestoreRegistrationSiblingOrder(
        WidgetHandle[] snapshot, Transform parent,
        RectTransform electricityRect)
    {
        int previousIndex = -1;
        for (int i = 0; i < snapshot.Length; i++)
        {
            WidgetHandle handle = snapshot[i];
            if (!handle.IsCreatedUnder(parent)) continue;

            int siblingIndex = handle.SiblingIndex;
            int electricityIndex = electricityRect.GetSiblingIndex();
            if (siblingIndex >= electricityIndex)
            {
                // Moving from after electricity to its current index inserts
                // the widget immediately before electricity.
                handle.ButtonRect.SetSiblingIndex(electricityIndex);
                siblingIndex = handle.SiblingIndex;
            }
            if (siblingIndex <= previousIndex)
            {
                // The widget is before a registration that should precede it.
                // Removing it shifts that previous widget left by one, so the
                // previous absolute index becomes the correct insertion point.
                handle.ButtonRect.SetSiblingIndex(previousIndex);
                siblingIndex = handle.SiblingIndex;
            }
            previousIndex = siblingIndex;
        }
    }

    internal static void DestroyCreatedWidgets()
    {
        WidgetHandle[] snapshot = orderedWidgets;
        for (int i = 0; i < snapshot.Length; i++)
        {
            snapshot[i].DestroyWidget();
        }
        fallbackElectricity = null;
        layoutDirty = true;
    }

    private static void Remove(WidgetHandle handle)
    {
        if (!Widgets.TryGetValue(handle.Id, out WidgetHandle current)
            || !ReferenceEquals(current, handle))
        {
            return;
        }
        Widgets.Remove(handle.Id);
        RebuildOrderedSnapshot();
    }

    private static void RebuildOrderedSnapshot()
    {
        orderedWidgets = Widgets.Values
            .OrderBy(item => item.Registration.Order)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        layoutDirty = true;
    }

    private static void Validate(TopBarWidgetRegistration registration)
    {
        if (registration == null)
            throw new ArgumentNullException(nameof(registration));
        if (string.IsNullOrWhiteSpace(registration.Id))
            throw new ArgumentException("Widget ID is required.",
                nameof(registration));
        if (registration.Icon == null)
            throw new ArgumentException("Widget icon provider is required.",
                nameof(registration));
        if (registration.OnClick == null)
            throw new ArgumentException("Widget click action is required.",
                nameof(registration));
    }

    private sealed class WidgetHandle : ITopBarWidgetHandle
    {
        internal readonly TopBarWidgetRegistration Registration;
        private WButtonIcon button;
        private UIPermanentTip tip;
        private TextMeshProUGUI badgeText;
        private Image badgeIcon;
        private Image nativeBadgeIcon;
        private bool disposed;

        internal WidgetHandle(TopBarWidgetRegistration registration)
        {
            Registration = registration;
        }

        public string Id => Registration.Id;
        public bool IsCreated => button != null;
        internal RectTransform ButtonRect => button?.Rect;
        internal int SiblingIndex => button == null
            ? -1 : button.Rect.GetSiblingIndex();
        internal float Width => Mathf.Max(32f, Registration.Width);

        internal bool IsCreatedUnder(Transform parent) =>
            !disposed && button != null && button.transform.parent == parent;

        internal bool CanReflowUnder(Transform parent) =>
            IsCreatedUnder(parent) && button.gameObject.activeInHierarchy;

        internal bool EnsureCreated(Transform parent,
            ElectricityMiniChartWidget electricity, WButtonIcon source)
        {
            if (disposed || parent == null || electricity == null
                || source == null)
            {
                return false;
            }
            if (IsCreatedUnder(parent)) return false;

            // Core can rebuild the top-bar hierarchy while leaving an old
            // widget alive briefly. Replace that stale clone under the newly
            // authoritative electricity-widget parent.
            DestroyWidget();
            button = UnityEngine.Object.Instantiate(source, parent);
            button.name = SanitizeName(Id) + "TopWidget";
            button.gameObject.SetActive(true);
            button.Rect.localScale = Vector3.one;
            button.Rect.sizeDelta = source.Rect.sizeDelta;
            button.Rect.SetSiblingIndex(electricity.transform.GetSiblingIndex());

            float width = Mathf.Max(32f, Registration.Width);
            LayoutElement layout = button.GetComponent<LayoutElement>()
                ?? button.gameObject.AddComponent<LayoutElement>();
            layout.minWidth = width;
            layout.preferredWidth = width;
            layout.flexibleWidth = 0f;

            if (parent.GetComponent<LayoutGroup>() == null)
            {
                PositionWithoutLayout(parent, electricity, button.Rect, width);
            }

            tip = button.GetComponent<UIPermanentTip>()
                ?? button.gameObject.AddComponent<UIPermanentTip>();
            button.Load(SafeIcon(), Click);
            EnsureBadgeText();
            Refresh();
            return true;
        }

        public void Refresh()
        {
            if (disposed || button == null) return;
            try
            {
                button.SetImage(SafeIcon());
                Color tint = Registration.Tint?.Invoke() ?? Color.white;
                button.SetTint(tint);
                RefreshBadgeIcon(tint);
                RefreshBadgeText();
                RefreshTooltip(Registration.Tooltip?.Invoke() ?? string.Empty);
            }
            catch (Exception ex)
            {
                D.Err("AutoUtilities widget refresh failed for {0}: {1}",
                    Id, ex);
            }
        }

        private void RefreshTooltip(string text)
        {
            if (tip == null) return;

            // SetTip updates the text used on the next hover, but Stardeus's
            // permanent tooltip retains the already-rendered text while it is
            // visible. Re-enter the currently shown tooltip so click-driven
            // state changes are visible immediately.
            bool wasShowing = PermanentTipIsShowingField?.GetValue(tip)
                is bool value && value;
            tip.SetTip(text);
            if (!wasShowing) return;

            // Use the same signal through which Stardeus displays its active
            // tooltip. This replaces the rendered text in place without a
            // hide/show cycle or visible flicker.
            The.SysSig.ShowTooltip.Send(text, true);
        }

        private void EnsureBadgeText()
        {
            if (Registration.BadgeText == null || badgeText != null) return;

            nativeBadgeIcon =
                ButtonIconImageField?.GetValue(button) as Image;
            if (nativeBadgeIcon != null)
            {
                var iconObject = new GameObject("AutoUtilities_BadgeIcon");
                iconObject.transform.SetParent(button.Rect, false);
                RectTransform iconRect =
                    iconObject.AddComponent<RectTransform>();
                iconRect.anchorMin = new Vector2(0.5f, 0.5f);
                iconRect.anchorMax = new Vector2(0.5f, 0.5f);
                iconRect.pivot = new Vector2(0.5f, 0.5f);
                iconRect.anchoredPosition = new Vector2(0f, 9f);
                iconRect.sizeDelta = new Vector2(26f, 26f);
                badgeIcon = iconObject.AddComponent<Image>();
                badgeIcon.preserveAspect = true;
                badgeIcon.raycastTarget = false;
                badgeIcon.sprite = nativeBadgeIcon.sprite;
                badgeIcon.material = nativeBadgeIcon.material;
                nativeBadgeIcon.enabled = false;
            }

            var badgeObject = new GameObject("AutoUtilities_BadgeText");
            badgeObject.transform.SetParent(button.Rect, false);
            RectTransform rect = badgeObject.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(0f, -14f);
            rect.sizeDelta = new Vector2(42f, 16f);
            rect.SetAsLastSibling();

            badgeText = badgeObject.AddComponent<TextMeshProUGUI>();
            badgeText.alignment = TextAlignmentOptions.Center;
            badgeText.fontSize = 14f;
            badgeText.fontStyle = FontStyles.Bold;
            badgeText.textWrappingMode = TextWrappingModes.NoWrap;
            badgeText.overflowMode = TextOverflowModes.Overflow;
            badgeText.raycastTarget = false;
        }

        private void RefreshBadgeIcon(Color tint)
        {
            if (Registration.BadgeText == null || button == null) return;
            EnsureBadgeText();
            if (nativeBadgeIcon == null || badgeIcon == null) return;
            nativeBadgeIcon.enabled = false;
            badgeIcon.sprite = nativeBadgeIcon.sprite;
            badgeIcon.material = nativeBadgeIcon.material;
            badgeIcon.color = tint;
            badgeIcon.gameObject.SetActive(badgeIcon.sprite != null);
        }

        private void RefreshBadgeText()
        {
            if (Registration.BadgeText == null) return;
            EnsureBadgeText();
            if (badgeText == null) return;
            badgeText.text = Registration.BadgeText() ?? string.Empty;
            badgeText.color = Registration.BadgeTextColor?.Invoke()
                ?? Color.white;
            badgeText.gameObject.SetActive(
                !string.IsNullOrEmpty(badgeText.text));
        }

        private string SafeIcon()
        {
            string icon = Registration.Icon();
            if (string.IsNullOrWhiteSpace(icon))
                throw new InvalidOperationException(
                    "Widget '" + Id + "' returned an empty icon ID.");
            return icon;
        }

        private void Click()
        {
            try
            {
                Registration.OnClick();
                Refresh();
            }
            catch (Exception ex)
            {
                D.Err("AutoUtilities widget click failed for {0}: {1}",
                    Id, ex);
            }
        }

        internal void DestroyWidget()
        {
            if (button != null)
                UnityEngine.Object.Destroy(button.gameObject);
            button = null;
            tip = null;
            badgeText = null;
            badgeIcon = null;
            nativeBadgeIcon = null;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            DestroyWidget();
            Remove(this);
        }

        internal static ElectricityMiniChartWidget FindElectricity(
            TopBarWidget topBar)
        {
            var electricity =
                ElectricityWidgetField?.GetValue(topBar)
                    as ElectricityMiniChartWidget;
            if (electricity != null) return electricity;

            if (fallbackElectricity != null
                && fallbackElectricity.transform.parent != null
                && fallbackElectricity.transform.IsChildOf(topBar.transform))
            {
                return fallbackElectricity;
            }

            ElectricityMiniChartWidget[] candidates =
                UnityEngine.Object.FindObjectsByType<ElectricityMiniChartWidget>(
                    FindObjectsInactive.Include);
            for (int i = 0; i < candidates.Length; i++)
            {
                ElectricityMiniChartWidget candidate = candidates[i];
                if (candidate == null || candidate.transform.parent == null
                    || !candidate.transform.IsChildOf(topBar.transform))
                {
                    continue;
                }
                fallbackElectricity = candidate;
                return candidate;
            }
            return null;
        }

        private static void PositionWithoutLayout(Transform parent,
            ElectricityMiniChartWidget electricity, RectTransform widget,
            float width)
        {
            var electricityRect = electricity.transform as RectTransform;
            if (electricityRect == null) return;

            const float gap = 8f;
            widget.anchorMin = electricityRect.anchorMin;
            widget.anchorMax = electricityRect.anchorMax;
            widget.pivot = new Vector2(0.5f, 0.5f);
            widget.sizeDelta = new Vector2(width, electricityRect.rect.height);
            float leftEdge = LeftEdge(electricityRect);
            int electricityIndex = electricityRect.GetSiblingIndex();

            for (int i = 0; i < electricityIndex; i++)
            {
                if (!(parent.GetChild(i) is RectTransform sibling)
                    || sibling == widget
                    || !sibling.gameObject.activeInHierarchy
                    || !VerticallyOverlaps(sibling, electricityRect))
                    continue;
                leftEdge = Mathf.Min(leftEdge, LeftEdge(sibling));
            }

            float centerY = electricityRect.anchoredPosition.y
                + (0.5f - electricityRect.pivot.y)
                * electricityRect.rect.height;
            widget.anchoredPosition = new Vector2(
                leftEdge - gap - width * 0.5f, centerY);
        }

        internal static float LeftEdge(RectTransform rect) =>
            rect.anchoredPosition.x - rect.pivot.x * rect.rect.width;

        private static bool VerticallyOverlaps(RectTransform candidate,
            RectTransform electricity)
        {
            float candidateCenter = candidate.anchoredPosition.y
                + (0.5f - candidate.pivot.y) * candidate.rect.height;
            float electricityCenter = electricity.anchoredPosition.y
                + (0.5f - electricity.pivot.y) * electricity.rect.height;
            return Mathf.Abs(candidateCenter - electricityCenter)
                <= (candidate.rect.height + electricity.rect.height) * 0.5f;
        }

        private static string SanitizeName(string value)
        {
            return new string(value.Where(char.IsLetterOrDigit).ToArray());
        }
    }
}
