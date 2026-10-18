using System;
using Game.Data;

namespace AutoUtilitiesModFramework;

/// <summary>Describes one optional, user-assignable keyboard shortcut.</summary>
public sealed class ShortcutRegistration
{
    /// <summary>Consumer ID previously passed to TryRegisterConsumer.</summary>
    public string ConsumerId { get; set; }
    /// <summary>Stable shortcut ID, unique within the consumer.</summary>
    public string Id { get; set; }
    /// <summary>Player-facing action name, such as "Open Auto Factory".</summary>
    public string DisplayName { get; set; }
    /// <summary>Unique global preference key used to persist the assignment.</summary>
    public string PreferenceKey { get; set; }
    /// <summary>Current game state, used to protect vanilla overlay shortcuts.</summary>
    public GameState GameState { get; set; }
    /// <summary>Action invoked once when the assigned key is pressed safely.</summary>
    public Action OnPressed { get; set; }
}

/// <summary>Consumer-owned control and status surface for one shortcut.</summary>
public interface IShortcutHandle : IDisposable
{
    bool IsCapturing { get; }
    bool HasShortcut { get; }
    string ShortcutPath { get; }
    string DisplayName { get; }
    string StatusMessage { get; }
    event Action StateChanged;
    void BeginCapture();
    void CancelCapture();
    void Clear();
}
