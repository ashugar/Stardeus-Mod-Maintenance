using System;
using UnityEngine;

namespace AutoUtilitiesModFramework;

/// <summary>Describes one consumer-owned top-bar widget.</summary>
public sealed class TopBarWidgetRegistration
{
    /// <summary>
    /// Globally unique stable ID. Recommended format: ConsumerModId.WidgetName.
    /// </summary>
    public string Id { get; set; }

    /// <summary>
    /// Supplies the current Stardeus <c>GraphicsId</c> used by the icon button.
    /// Called again by <see cref="ITopBarWidgetHandle.Refresh"/>.
    /// </summary>
    public Func<string> Icon { get; set; }

    /// <summary>
    /// Supplies the current icon tint. May be omitted to use white. A common
    /// convention is <c>() =&gt; enabled ? Color.white : Color.gray</c>.
    /// </summary>
    public Func<Color> Tint { get; set; }

    /// <summary>
    /// Supplies the current permanent tooltip text. Refreshing also updates a
    /// tooltip that is already visible, without deliberately closing it.
    /// </summary>
    public Func<string> Tooltip { get; set; }

    /// <summary>
    /// Optional short text rendered beneath the icon, such as a live pending
    /// count. Omit this provider to retain the existing icon-only widget path.
    /// Called again by <see cref="ITopBarWidgetHandle.Refresh"/>.
    /// </summary>
    /// <example><code>
    /// BadgeText = () =&gt; pendingCount.ToString()
    /// </code></example>
    public Func<string> BadgeText { get; set; }

    /// <summary>
    /// Optional badge-text color. Defaults to white and has no effect when
    /// <see cref="BadgeText"/> is omitted.
    /// </summary>
    public Func<Color> BadgeTextColor { get; set; }

    /// <summary>
    /// Action invoked when the player clicks the widget. Usually calls
    /// <c>FrameworkPanel.Toggle()</c> or changes state then refreshes the handle.
    /// </summary>
    public Action OnClick { get; set; }

    /// <summary>Preferred width in UI units. Values below 32 are clamped.</summary>
    public float Width { get; set; } = 32f;

    /// <summary>
    /// Lower values are created first. Cooperative positioning still respects
    /// compatible widgets created by other mods.
    /// </summary>
    public int Order { get; set; }
}

/// <summary>Consumer-owned handle for refreshing or removing a widget.</summary>
public interface ITopBarWidgetHandle : IDisposable
{
    /// <summary>The stable ID supplied during registration.</summary>
    string Id { get; }

    /// <summary>
    /// Whether the native widget currently exists. Registration can precede
    /// game UI creation, so <see langword="false"/> is not necessarily an error.
    /// </summary>
    bool IsCreated { get; }

    /// <summary>
    /// Re-evaluates the icon, tint, and tooltip providers. Call after consumer
    /// state changes; creation remains managed by the framework.
    /// </summary>
    /// <example><code>
    /// enabled = !enabled;
    /// widget?.Refresh();
    /// </code></example>
    void Refresh();
}
