using System;
using System.Collections.Generic;
using System.Linq;
using Game;
using Game.Constants;
using Game.Systems;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;

namespace AutoUtilitiesModFramework;

internal static class ShortcutService
{
    private static readonly Dictionary<string, ShortcutHandle> Handles =
        new Dictionary<string, ShortcutHandle>(StringComparer.OrdinalIgnoreCase);
    private static ShortcutHandle[] handleSnapshot =
        Array.Empty<ShortcutHandle>();
    private static ShortcutRunner runner;

    internal static IShortcutHandle Register(ShortcutRegistration registration)
    {
        Validate(registration);
        string key = registration.ConsumerId.Trim() + ":" + registration.Id.Trim();
        if (Handles.ContainsKey(key))
            throw new InvalidOperationException("A shortcut with ID '" + key + "' is already registered.");
        if (Handles.Values.Any(h => string.Equals(h.PreferenceKey,
            registration.PreferenceKey, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Shortcut preference key '"
                + registration.PreferenceKey + "' is already registered.");

        EnsureRunner();
        var handle = new ShortcutHandle(key, registration);
        Handles.Add(key, handle);
        RebuildSnapshot();
        try { handle.Initialize(); }
        catch { handle.Dispose(); throw; }
        return handle;
    }

    private static void Validate(ShortcutRegistration value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        if (string.IsNullOrWhiteSpace(value.ConsumerId)) throw new ArgumentException("ConsumerId is required.");
        if (string.IsNullOrWhiteSpace(value.Id)) throw new ArgumentException("Id is required.");
        if (string.IsNullOrWhiteSpace(value.DisplayName)) throw new ArgumentException("DisplayName is required.");
        if (string.IsNullOrWhiteSpace(value.PreferenceKey)) throw new ArgumentException("PreferenceKey is required.");
        if (value.OnPressed == null) throw new ArgumentException("OnPressed is required.");
    }

    private static void EnsureRunner()
    {
        if (runner != null) return;
        var go = new GameObject("AUMF_ShortcutService");
        UnityEngine.Object.DontDestroyOnLoad(go);
        runner = go.AddComponent<ShortcutRunner>();
    }

    internal static void Tick()
    {
        // Only one registration may capture at a time. This also prevents an
        // assigned shortcut firing while another consumer is choosing a key.
        ShortcutHandle[] snapshot = handleSnapshot;
        ShortcutHandle capture = null;
        for (int i = 0; i < snapshot.Length; i++)
        {
            if (!snapshot[i].IsCapturing) continue;
            capture = snapshot[i];
            break;
        }
        if (capture != null) capture.Capture();
        else
        {
            // Keep the same snapshot for the entire dispatch. A callback may
            // register or dispose shortcuts; the rebuilt snapshot takes effect
            // on the next frame, while disposed handles safely ignore Tick().
            for (int i = 0; i < snapshot.Length; i++) snapshot[i].Tick();
        }
    }

    internal static bool TryFindFrameworkConflict(ShortcutHandle requester,
        string path, out string name)
    {
        foreach (ShortcutHandle handle in Handles.Values)
        {
            if (handle != requester && handle.HasShortcut && PathsMatch(handle.ShortcutPath, path))
            {
                name = handle.ActionName;
                return true;
            }
        }
        name = null;
        return false;
    }

    internal static void Remove(ShortcutHandle handle)
    {
        if (Handles.Remove(handle.RegistryKey)) RebuildSnapshot();
        if (Handles.Count == 0 && runner != null)
        {
            UnityEngine.Object.Destroy(runner.gameObject);
            runner = null;
        }
    }

    internal static ShortcutHandle[] SnapshotHandles() => handleSnapshot;

    private static void RebuildSnapshot()
    {
        var snapshot = new ShortcutHandle[Handles.Count];
        Handles.Values.CopyTo(snapshot, 0);
        handleSnapshot = snapshot;
    }

    internal static bool PathsMatch(string a, string b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Replace(" ", ""), b.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

    internal static bool IsTextInputFocused()
    {
        GameObject selected = EventSystem.current?.currentSelectedGameObject;
        return selected != null && (selected.GetComponent<TMP_InputField>() != null
            || selected.GetComponentInParent<TMP_InputField>() != null
            || selected.GetComponent<InputField>() != null
            || selected.GetComponentInParent<InputField>() != null);
    }

    private sealed class ShortcutRunner : MonoBehaviour
    {
        private void Update() => Tick();
    }
}

internal sealed class ShortcutHandle : IShortcutHandle
{
    private readonly ShortcutRegistration registration;
    private InputAction action;
    private bool disposed;
    private string path;

    internal string RegistryKey { get; }
    internal string PreferenceKey => registration.PreferenceKey;
    internal string ActionName => registration.DisplayName;
    public bool IsCapturing { get; private set; }
    public bool HasShortcut => !string.IsNullOrWhiteSpace(path);
    public string ShortcutPath => path ?? string.Empty;
    public string DisplayName => HasShortcut ? Humanize(path) : "Not assigned";
    public string StatusMessage { get; private set; } = "No shortcut assigned.";
    public event Action StateChanged;

    internal ShortcutHandle(string registryKey, ShortcutRegistration value)
    { RegistryKey = registryKey; registration = value; }

    internal void Initialize()
    {
        The.Prefs.RegisterString(registration.PreferenceKey, string.Empty);
        path = The.Prefs.GetStringAutoHash(registration.PreferenceKey, string.Empty);
        if (HasShortcut && TryFindConflict(path, out string conflict))
            StatusMessage = $"{DisplayName} conflicts with “{conflict}” and is currently disabled.";
        else { CreateAction(); RestoreStatus(); }
    }

    public void BeginCapture()
    {
        ThrowIfDisposed();
        foreach (ShortcutHandle other in ShortcutService.SnapshotHandles()) other.CancelCaptureInternal();
        IsCapturing = true;
        StatusMessage = "Press a keyboard key. Escape cancels.";
        StateChanged?.Invoke();
    }

    public void CancelCapture() { ThrowIfDisposed(); CancelCaptureInternal(); }
    internal void CancelCaptureInternal()
    {
        if (!IsCapturing) return;
        IsCapturing = false; RestoreStatus(); StateChanged?.Invoke();
    }

    public void Clear()
    {
        ThrowIfDisposed(); IsCapturing = false; path = string.Empty;
        DisposeAction(); Save(); RestoreStatus(); StateChanged?.Invoke();
    }

    internal void Tick()
    {
        if (disposed || action == null || !action.WasPressedThisFrame()
            || ShortcutService.IsTextInputFocused()) return;
        if (TryFindConflict(path, out string conflict))
        {
            DisposeAction();
            StatusMessage = $"{DisplayName} now conflicts with “{conflict}” and has been disabled.";
            StateChanged?.Invoke(); return;
        }
        try { registration.OnPressed(); }
        catch (Exception ex) { Debug.LogError($"[AUMF] Shortcut '{RegistryKey}' callback failed: {ex}"); }
    }

    internal void Capture()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null) { IsCapturing = false; StatusMessage = "No keyboard is available."; StateChanged?.Invoke(); return; }
        if (keyboard.escapeKey.wasPressedThisFrame) { CancelCaptureInternal(); return; }
        foreach (KeyControl key in keyboard.allKeys)
        {
            if (!key.wasPressedThisFrame || IsModifier(key, keyboard)) continue;
            string candidate = "<Keyboard>/" + key.name;
            string display = Humanize(candidate);
            if (string.Equals(candidate, "<Keyboard>/f12", StringComparison.OrdinalIgnoreCase))
            { Reject($"Cannot assign {display}: Steam reserves it for screenshots."); return; }
            if (TryFindConflict(candidate, out string conflict))
            { Reject($"Cannot assign {display}: it is already used by “{conflict}”."); return; }
            path = candidate; IsCapturing = false; CreateAction(); Save(); RestoreStatus(); StateChanged?.Invoke(); return;
        }
    }

    private void Reject(string message) { IsCapturing = false; StatusMessage = message; StateChanged?.Invoke(); }

    private bool TryFindConflict(string candidate, out string name)
    {
        if (ShortcutService.TryFindFrameworkConflict(this, candidate, out name)) return true;
        int f = FunctionKey(candidate);
        if (f >= 1 && f <= 10 && registration.GameState?.Sys?.Active != null)
        {
            var overlays = registration.GameState.Sys.Active.OfType<IOverlayProvider>()
                .SelectMany(p => p.Overlays).Where(o => o.Type == OverlayType.Visual)
                .OrderBy(o => o.Priority).ToList();
            if (f <= overlays.Count)
            { var overlay = overlays[f - 1]; name = string.IsNullOrWhiteSpace(overlay.TooltipText) ? overlay.Id : overlay.TooltipText; name += " overlay"; return true; }
        }
        if (The.Bindings?.All != null)
            foreach (var pair in The.Bindings.All)
                if (pair.Value?.InputAction != null)
                    foreach (InputBinding binding in pair.Value.InputAction.bindings)
                        if (ShortcutService.PathsMatch(binding.effectivePath, candidate))
                        { name = pair.Value.ActionName; return true; }
        name = null; return false;
    }

    private static int FunctionKey(string value)
    { string n = value?.Substring(value.LastIndexOf('/') + 1).Trim(); return n?.Length > 1 && char.ToUpperInvariant(n[0]) == 'F' && int.TryParse(n.Substring(1), out int f) ? f : 0; }
    private static bool IsModifier(KeyControl key, Keyboard kb) => key == kb.leftShiftKey || key == kb.rightShiftKey || key == kb.leftCtrlKey || key == kb.rightCtrlKey || key == kb.leftAltKey || key == kb.rightAltKey;
    private static string Humanize(string value) => InputControlPath.ToHumanReadableString(value, InputControlPath.HumanReadableStringOptions.OmitDevice);
    private void CreateAction() { DisposeAction(); if (!HasShortcut) return; action = new InputAction("AUMF." + RegistryKey, InputActionType.Button, path); action.Enable(); }
    private void DisposeAction() { action?.Disable(); action?.Dispose(); action = null; }
    private void Save() { The.Prefs.SetString(registration.PreferenceKey, path ?? string.Empty); The.SaveGlobalSettings(); }
    private void RestoreStatus() { StatusMessage = HasShortcut ? $"Press {DisplayName} to {registration.DisplayName}." : "No shortcut assigned."; }
    private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(ShortcutHandle)); }
    public void Dispose()
    {
        if (disposed) return; disposed = true; IsCapturing = false; DisposeAction();
        StateChanged = null; ShortcutService.Remove(this);
    }
}
