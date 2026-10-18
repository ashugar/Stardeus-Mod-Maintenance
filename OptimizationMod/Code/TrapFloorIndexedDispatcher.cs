using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Game.Components;
using Game.Data;
using Game.Systems.Creatures;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.OptimizationMod {
    /// <summary>
    /// Replaces TrapFloorComp's per-instance BeingPosChanged subscriptions with
    /// one positional dispatcher. Candidate traps still execute Core's exact
    /// private handler; this class owns only routing and fail-open restoration.
    /// </summary>
    internal static class TrapFloorIndexedDispatcher {
        private const long AuditIntervalTicks = 600L;

        private delegate void TrapHandler(Being being, int from, int to);

        private sealed class TrapEntry {
            internal int PosIdx;
            internal long Order;
            internal Delegate SignalListener;
            internal TrapHandler Handler;
        }

        private static readonly Dictionary<int, HashSet<TrapFloorComp>> ByPos =
            new Dictionary<int, HashSet<TrapFloorComp>>();
        private static readonly Dictionary<TrapFloorComp, TrapEntry> ByTrap =
            new Dictionary<TrapFloorComp, TrapEntry>();
        private static readonly FieldInfo PosIdxField =
            AccessTools.Field(typeof(TrapFloorComp), "posIdx");
        private static readonly FieldInfo IsSubscribedField =
            AccessTools.Field(typeof(TrapFloorComp), "isSubscribed");
        private static readonly MethodInfo HandlerMethod =
            AccessTools.DeclaredMethod(typeof(TrapFloorComp),
                "OnBeingPosChange",
                new[] { typeof(Being), typeof(int), typeof(int) });
        private static readonly MethodInfo DispatcherMethod =
            AccessTools.DeclaredMethod(typeof(TrapFloorIndexedDispatcher),
                nameof(OnBeingPosChanged));

        private static GameState state;
        private static FieldInfo listenersField;
        private static Type signalListenerType;
        private static MethodInfo addListenerMethod;
        private static MethodInfo removeListenerMethod;
        private static long startedAt;
        private static long nextAuditTick;
        private static bool initialized;
        private static bool dispatcherActive;
        private static bool diagnosticsEnabled;
        private static bool failureLogged;

        private static long movementSignals;
        private static long routedCandidates;
        private static long avoidedCallbacks;
        private static long noTrapDestinations;
        private static long oneTrapDestinations;
        private static long multipleTrapDestinations;
        private static int maxCandidatesAtDestination;
        private static int maxRegisteredTraps;
        private static long lifecycleAdds;
        private static long lifecycleRemoves;
        private static long lifecycleRelocations;
        private static long audits;
        private static long initialTakeoverCount;
        private static long restoredListeners;
        private static long nextRegistrationOrder;

        internal static bool IsFailOpen { get; private set; }
        internal static bool IsDispatcherActive => dispatcherActive;

        internal static void Initialize(
                GameState gameState, bool enableDiagnostics) {
            Reset();
            diagnosticsEnabled = enableDiagnostics;
            if (gameState == null || PosIdxField == null
                    || IsSubscribedField == null || HandlerMethod == null
                    || DispatcherMethod == null) {
                FailOpen("Optimization Mod Trap Floor dispatcher could not "
                    + "resolve its required Core fields or handler.", null);
                return;
            }

            try {
                state = gameState;
                object signal = state.Sig.BeingPosChanged;
                listenersField = FindListenersField(signal?.GetType());
                signalListenerType = listenersField?.FieldType
                    .GetGenericArguments()[0];
                addListenerMethod = signal?.GetType().GetMethod(
                    "AddListener",
                    BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { signalListenerType }, null);
                removeListenerMethod = signal?.GetType().GetMethod(
                    "RemoveListener",
                    BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { signalListenerType }, null);
                if (listenersField == null || signalListenerType == null
                        || addListenerMethod == null
                        || removeListenerMethod == null) {
                    FailOpen("Optimization Mod Trap Floor dispatcher could "
                        + "not resolve Core's BeingPosChanged listener API.",
                        null);
                    return;
                }

                initialized = true;
                state.Clock.OnTick.AddListener(OnTick);
                state.Clock.OnNextUpdate(InitialTakeover);
                if (diagnosticsEnabled) {
                    startedAt = Stopwatch.GetTimestamp();
                }
            } catch (Exception ex) {
                FailOpen("Optimization Mod Trap Floor dispatcher failed to "
                    + "initialize.", ex);
            }
        }

        internal static void Register(TrapFloorComp trap) {
            if (!initialized || IsFailOpen || !dispatcherActive
                    || trap == null) {
                return;
            }
            try {
                if (!TryGetPosition(trap, out int posIdx)
                        || !IsCoreSubscribed(trap)) {
                    FailOpen("Optimization Mod Trap Floor dispatcher saw an "
                        + "invalid newly registered trap.", null);
                    return;
                }

                if (ByTrap.TryGetValue(trap, out TrapEntry existing)) {
                    RemoveCoreListener(existing);
                    if (existing.PosIdx != posIdx) {
                        MoveAt(trap, existing, posIdx);
                        if (diagnosticsEnabled) { lifecycleRelocations++; }
                    }
                    return;
                }

                TrapEntry entry = CreateEntry(trap, posIdx);
                AddAt(trap, entry);
                RemoveCoreListener(entry);
                if (diagnosticsEnabled) { lifecycleAdds++; }
            } catch (Exception ex) {
                FailOpen("Optimization Mod Trap Floor registration routing "
                    + "failed.", ex);
            }
        }

        internal static void Unregister(TrapFloorComp trap) {
            if (!initialized || IsFailOpen || !dispatcherActive
                    || trap == null) {
                return;
            }
            try {
                if (!ByTrap.TryGetValue(trap, out TrapEntry entry)) {
                    FailOpen("Optimization Mod Trap Floor dispatcher missed a "
                        + "trap removal.", null);
                    return;
                }
                RemoveAt(trap, entry.PosIdx);
                if (diagnosticsEnabled) { lifecycleRemoves++; }
            } catch (Exception ex) {
                FailOpen("Optimization Mod Trap Floor removal routing failed.",
                    ex);
            }
        }

        internal static void Relocate(TrapFloorComp trap) {
            if (!initialized || IsFailOpen || !dispatcherActive
                    || trap == null) {
                return;
            }
            try {
                if (!TryGetPosition(trap, out int posIdx)) {
                    FailOpen("Optimization Mod Trap Floor dispatcher saw an "
                        + "invalid relocated trap position.", null);
                    return;
                }
                if (!ByTrap.TryGetValue(trap, out TrapEntry entry)) {
                    entry = CreateEntry(trap, posIdx);
                    AddAt(trap, entry);
                    FailOpen("Optimization Mod Trap Floor dispatcher missed a "
                        + "trap before relocation.", null);
                    return;
                }
                if (entry.PosIdx == posIdx) { return; }
                MoveAt(trap, entry, posIdx);
                if (diagnosticsEnabled) { lifecycleRelocations++; }
            } catch (Exception ex) {
                FailOpen("Optimization Mod Trap Floor relocation routing "
                    + "failed.", ex);
            }
        }

        private static void InitialTakeover() {
            if (!initialized || IsFailOpen || dispatcherActive) { return; }
            try {
                IList listeners = GetListeners();
                var vanilla = new List<KeyValuePair<TrapFloorComp, Delegate>>();
                for (int i = 0; i < listeners.Count; i++) {
                    if (listeners[i] is Delegate listener
                            && listener.Target is TrapFloorComp trap
                            && listener.Method == HandlerMethod) {
                        vanilla.Add(new KeyValuePair<TrapFloorComp, Delegate>(
                            trap, listener));
                    }
                }

                for (int i = 0; i < vanilla.Count; i++) {
                    TrapFloorComp trap = vanilla[i].Key;
                    if (!TryGetPosition(trap, out int posIdx)
                            || !IsCoreSubscribed(trap)) {
                        throw new InvalidOperationException(
                            "Core exposed an invalid subscribed Trap Floor.");
                    }
                    if (ByTrap.ContainsKey(trap)) {
                        throw new InvalidOperationException(
                            "Core exposed a duplicate Trap Floor listener.");
                    }
                    TrapEntry entry = CreateEntry(trap, posIdx,
                        vanilla[i].Value);
                    AddAt(trap, entry);
                    RemoveCoreListener(entry);
                }

                state.Sig.BeingPosChanged.AddListener(OnBeingPosChanged);
                dispatcherActive = true;
                nextAuditTick = state.Ticks + AuditIntervalTicks;
                if (diagnosticsEnabled) {
                    initialTakeoverCount = vanilla.Count;
                    D.Warn("Optimization Mod Trap Floor indexed dispatcher "
                        + "activated for {0} traps; vanilla eligibility and "
                        + "trigger logic remain authoritative.", vanilla.Count);
                }
            } catch (Exception ex) {
                FailOpen("Optimization Mod Trap Floor initial listener "
                    + "takeover failed.", ex);
            }
        }

        private static void OnBeingPosChanged(Being being, int from, int to) {
            if (!dispatcherActive || IsFailOpen) { return; }

            if (diagnosticsEnabled) { movementSignals++; }
            try {
                if (!ByPos.TryGetValue(to,
                        out HashSet<TrapFloorComp> traps)
                        || traps.Count == 0) {
                    if (diagnosticsEnabled) {
                        noTrapDestinations++;
                        avoidedCallbacks += ByTrap.Count;
                    }
                    return;
                }

                var candidates = new List<TrapEntry>(traps.Count);
                foreach (TrapFloorComp trap in traps) {
                    if (!ByTrap.TryGetValue(trap, out TrapEntry entry)
                            || entry.PosIdx != to
                            || !IsCoreSubscribed(trap)) {
                        FailOpen("Optimization Mod Trap Floor dispatcher found "
                            + "an invalid destination entry.", null);
                        DispatchCurrentEventVanilla(being, from, to);
                        return;
                    }
                    candidates.Add(entry);
                }

                if (diagnosticsEnabled) {
                    routedCandidates += candidates.Count;
                    avoidedCallbacks += Math.Max(0,
                        ByTrap.Count - candidates.Count);
                    if (candidates.Count == 1) {
                        oneTrapDestinations++;
                    } else {
                        multipleTrapDestinations++;
                    }
                    if (candidates.Count > maxCandidatesAtDestination) {
                        maxCandidatesAtDestination = candidates.Count;
                    }
                }

                candidates.Sort((left, right) =>
                    right.Order.CompareTo(left.Order));

                for (int i = 0; i < candidates.Count; i++) {
                    try {
                        candidates[i].Handler(being, from, to);
                    } catch (Exception handlerEx) {
                        FailOpen("Optimization Mod Trap Floor dispatcher saw "
                            + "Core's original trap handler throw.", handlerEx);
                        throw;
                    }
                }
            } catch (Exception ex) {
                if (!IsFailOpen) {
                    FailOpen("Optimization Mod Trap Floor dispatch failed.",
                        ex);
                    DispatchCurrentEventVanilla(being, from, to);
                } else {
                    throw;
                }
            }
        }

        private static void OnTick(long ticks) {
            if (!dispatcherActive || IsFailOpen || ticks < nextAuditTick) {
                return;
            }
            nextAuditTick = ticks + AuditIntervalTicks;
            Audit();
        }

        private static void Audit() {
            try {
                IList listeners = GetListeners();
                int centralListeners = 0;
                int vanillaTrapListeners = 0;
                for (int i = 0; i < listeners.Count; i++) {
                    if (!(listeners[i] is Delegate listener)) { continue; }
                    if (listener.Method == DispatcherMethod) {
                        centralListeners++;
                    } else if (listener.Target is TrapFloorComp
                            && listener.Method == HandlerMethod) {
                        vanillaTrapListeners++;
                    }
                }
                if (centralListeners != 1 || vanillaTrapListeners != 0) {
                    FailOpen("Optimization Mod Trap Floor listener audit found "
                        + $"central/vanilla counts {centralListeners}/"
                        + $"{vanillaTrapListeners}.", null);
                    return;
                }

                foreach (KeyValuePair<TrapFloorComp, TrapEntry> item in ByTrap) {
                    if (!IsCoreSubscribed(item.Key)
                            || !TryGetPosition(item.Key, out int posIdx)
                            || posIdx != item.Value.PosIdx
                            || !ByPos.TryGetValue(posIdx,
                                out HashSet<TrapFloorComp> atPosition)
                            || !atPosition.Contains(item.Key)) {
                        FailOpen("Optimization Mod Trap Floor registry audit "
                            + "found a lifecycle or position mismatch.", null);
                        return;
                    }
                }
                int positionalEntries = 0;
                foreach (KeyValuePair<int, HashSet<TrapFloorComp>> position
                        in ByPos) {
                    foreach (TrapFloorComp trap in position.Value) {
                        positionalEntries++;
                        if (!ByTrap.TryGetValue(trap, out TrapEntry entry)
                                || entry.PosIdx != position.Key) {
                            FailOpen("Optimization Mod Trap Floor positional "
                                + "audit found an extra or duplicate entry.",
                                null);
                            return;
                        }
                    }
                }
                if (positionalEntries != ByTrap.Count) {
                    FailOpen("Optimization Mod Trap Floor positional audit "
                        + "count did not match the trap registry.", null);
                    return;
                }
                if (diagnosticsEnabled) { audits++; }
            } catch (Exception ex) {
                FailOpen("Optimization Mod Trap Floor registry audit failed.",
                    ex);
            }
        }

        private static TrapEntry CreateEntry(
                TrapFloorComp trap, int posIdx, Delegate signal = null) {
            Delegate signalListener = signal ?? (Delegate)
                Delegate.CreateDelegate(signalListenerType, trap,
                    HandlerMethod);
            var handler = (TrapHandler) Delegate.CreateDelegate(
                typeof(TrapHandler), trap, HandlerMethod);
            return new TrapEntry {
                PosIdx = posIdx,
                Order = nextRegistrationOrder++,
                SignalListener = signalListener,
                Handler = handler
            };
        }

        private static void AddAt(TrapFloorComp trap, TrapEntry entry) {
            if (!ByPos.TryGetValue(entry.PosIdx,
                    out HashSet<TrapFloorComp> traps)) {
                traps = new HashSet<TrapFloorComp>();
                ByPos.Add(entry.PosIdx, traps);
            }
            traps.Add(trap);
            ByTrap[trap] = entry;
            if (ByTrap.Count > maxRegisteredTraps) {
                maxRegisteredTraps = ByTrap.Count;
            }
        }

        private static void RemoveAt(TrapFloorComp trap, int posIdx) {
            ByTrap.Remove(trap);
            if (!ByPos.TryGetValue(posIdx,
                    out HashSet<TrapFloorComp> traps)) {
                return;
            }
            traps.Remove(trap);
            if (traps.Count == 0) { ByPos.Remove(posIdx); }
        }

        private static void MoveAt(
                TrapFloorComp trap, TrapEntry entry, int newPosIdx) {
            if (!ByPos.TryGetValue(newPosIdx,
                    out HashSet<TrapFloorComp> newPosition)) {
                newPosition = new HashSet<TrapFloorComp>();
                ByPos.Add(newPosIdx, newPosition);
            }
            newPosition.Add(trap);
            if (ByPos.TryGetValue(entry.PosIdx,
                    out HashSet<TrapFloorComp> oldPosition)) {
                oldPosition.Remove(trap);
                if (oldPosition.Count == 0) {
                    ByPos.Remove(entry.PosIdx);
                }
            }
            entry.PosIdx = newPosIdx;
            ByTrap[trap] = entry;
        }

        private static bool TryGetPosition(
                TrapFloorComp trap, out int posIdx) {
            posIdx = 0;
            if (trap == null || PosIdxField == null) { return false; }
            object value = PosIdxField.GetValue(trap);
            if (!(value is int position) || position <= 0) { return false; }
            posIdx = position;
            return true;
        }

        private static bool IsCoreSubscribed(TrapFloorComp trap) {
            return trap != null && IsSubscribedField != null
                && IsSubscribedField.GetValue(trap) is bool subscribed
                && subscribed;
        }

        private static IList GetListeners() {
            object raw = listenersField.GetValue(state.Sig.BeingPosChanged);
            if (!(raw is IList listeners)) {
                throw new InvalidOperationException(
                    "BeingPosChanged listeners are not an IList.");
            }
            return listeners;
        }

        private static void RemoveCoreListener(TrapEntry entry) {
            object signal = state.Sig.BeingPosChanged;
            removeListenerMethod.Invoke(signal,
                new object[] { entry.SignalListener });
            if (GetListeners().Contains(entry.SignalListener)) {
                throw new InvalidOperationException(
                    "Core Trap Floor listener remained after removal.");
            }
        }

        private static void RestoreCoreListeners() {
            if (state == null || addListenerMethod == null) { return; }
            object signal = state.Sig.BeingPosChanged;
            IList listeners = GetListeners();
            var entries = new List<KeyValuePair<TrapFloorComp, TrapEntry>>(
                ByTrap);
            entries.Sort((left, right) =>
                left.Value.Order.CompareTo(right.Value.Order));
            for (int i = 0; i < entries.Count; i++) {
                KeyValuePair<TrapFloorComp, TrapEntry> item = entries[i];
                if (!IsCoreSubscribed(item.Key)
                        || listeners.Contains(item.Value.SignalListener)) {
                    continue;
                }
                addListenerMethod.Invoke(signal,
                    new object[] { item.Value.SignalListener });
                restoredListeners++;
            }
        }

        private static void DispatchCurrentEventVanilla(
                Being being, int from, int to) {
            var handlers = new List<TrapHandler>(ByTrap.Count);
            var entries = new List<KeyValuePair<TrapFloorComp, TrapEntry>>(
                ByTrap);
            entries.Sort((left, right) =>
                right.Value.Order.CompareTo(left.Value.Order));
            for (int i = 0; i < entries.Count; i++) {
                KeyValuePair<TrapFloorComp, TrapEntry> item = entries[i];
                if (IsCoreSubscribed(item.Key)) {
                    handlers.Add(item.Value.Handler);
                }
            }
            for (int i = 0; i < handlers.Count; i++) {
                handlers[i](being, from, to);
            }
        }

        private static FieldInfo FindListenersField(Type type) {
            for (Type current = type; current != null;
                    current = current.BaseType) {
                FieldInfo field = current.GetField("listeners",
                    BindingFlags.Instance | BindingFlags.NonPublic
                    | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (field != null) { return field; }
            }
            return null;
        }

        internal static void LogSummary() {
            if (!diagnosticsEnabled || (!initialized && startedAt == 0L)) {
                return;
            }
            double seconds = startedAt == 0L
                ? 0d
                : (Stopwatch.GetTimestamp() - startedAt)
                    / (double) Stopwatch.Frequency;
            D.Warn($"Optimization Mod Trap Floor indexed-dispatch summary: "
                + $"{seconds:0.0}s captured, movement signals="
                + $"{movementSignals}, registered traps={ByTrap.Count} "
                + $"current/{maxRegisteredTraps} max, routed Core handlers="
                + $"{routedCandidates}, avoided callbacks={avoidedCallbacks}, "
                + $"destinations zero/one/multiple={noTrapDestinations}/"
                + $"{oneTrapDestinations}/{multipleTrapDestinations}, max "
                + $"candidates={maxCandidatesAtDestination}, audits={audits}, "
                + $"fail-open={IsFailOpen}.");
            D.Warn($"Optimization Mod Trap Floor indexed-dispatch lifecycle: "
                + $"initial takeover={initialTakeoverCount}, adds="
                + $"{lifecycleAdds}, removes={lifecycleRemoves}, relocations="
                + $"{lifecycleRelocations}, restored listeners="
                + $"{restoredListeners}.");
        }

        internal static void Shutdown() {
            if (state != null) {
                try {
                    state.Sig.BeingPosChanged.RemoveListener(
                        OnBeingPosChanged);
                    RestoreCoreListeners();
                    state.Clock.OnTick.RemoveListener(OnTick);
                } catch (Exception ex) {
                    D.Err("Optimization Mod Trap Floor dispatcher failed to "
                        + "restore Core listeners during unload: {0}", ex);
                }
            }
            dispatcherActive = false;
        }

        internal static void Reset() {
            ByPos.Clear();
            ByTrap.Clear();
            state = null;
            listenersField = null;
            signalListenerType = null;
            addListenerMethod = null;
            removeListenerMethod = null;
            startedAt = 0L;
            nextAuditTick = 0L;
            initialized = false;
            dispatcherActive = false;
            diagnosticsEnabled = false;
            failureLogged = false;
            IsFailOpen = false;
            movementSignals = 0L;
            routedCandidates = 0L;
            avoidedCallbacks = 0L;
            noTrapDestinations = 0L;
            oneTrapDestinations = 0L;
            multipleTrapDestinations = 0L;
            maxCandidatesAtDestination = 0;
            maxRegisteredTraps = 0;
            lifecycleAdds = 0L;
            lifecycleRemoves = 0L;
            lifecycleRelocations = 0L;
            audits = 0L;
            initialTakeoverCount = 0L;
            restoredListeners = 0L;
            nextRegistrationOrder = 0L;
        }

        private static void FailOpen(string message, Exception ex) {
            if (IsFailOpen) { return; }
            IsFailOpen = true;
            dispatcherActive = false;
            Exception restoreError = null;
            try {
                if (state != null) {
                    state.Sig.BeingPosChanged.RemoveListener(
                        OnBeingPosChanged);
                    RestoreCoreListeners();
                }
            } catch (Exception restoreEx) {
                restoreError = restoreEx;
            }

            if (failureLogged) { return; }
            failureLogged = true;
            string detail = ex == null ? "" : $" Cause: {ex}";
            string restoration = restoreError == null
                ? " Core's per-trap listeners are active."
                : $" Core listener restoration also failed: {restoreError}";
            D.Err(message + restoration + detail);
        }
    }

    internal static class TrapFloorLateReadyDispatcherPatch {
        internal static void Postfix(TrapFloorComp __instance) {
            TrapFloorIndexedDispatcher.Register(__instance);
        }
    }

    internal static class TrapFloorRemoveDispatcherPatch {
        internal static void Postfix(TrapFloorComp __instance) {
            TrapFloorIndexedDispatcher.Unregister(__instance);
        }
    }

    internal static class TrapFloorPositionDispatcherPatch {
        internal static void Postfix(TrapFloorComp __instance) {
            TrapFloorIndexedDispatcher.Relocate(__instance);
        }
    }
}
