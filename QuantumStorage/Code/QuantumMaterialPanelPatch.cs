using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.QuantumStorage.Relays;
using Game.Systems.UIUX;
using Game.Utils;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.QuantumStorage
{
    // Keep Core's own rows, click closures, quantity choices, tooltips and Eject All.
    // Only replace rebuilding an unchanged set of material types with text/order edits.
    [HarmonyPatch(typeof(MatStorageTab), "BuildContentsList", new Type[] { })]
    internal static class QuantumMaterialPanelPatch
    {
        // Reviewed native contract: rc342.3224. These are resolved-contract hashes,
        // not a version gate or raw IL tokens: equivalent reference renumbering is
        // accepted, but changed instructions/signatures/locals/EH/actions are not.
        // Core retains current quantities, general forbidden-item notification,
        // callbacks and filter lifetimes. Unknown contracts use the full native UI.
        internal const string ReviewedBodyHash = "7BF9F70836798FDB76908C53D4A8E84DE046AB2632EF0A1CDAFF15C4F5272FF1";
        private const BindingFlags DeclaredMethods = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        private static readonly Dictionary<string, string> ReviewedCallbackHashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Eject All: native DumpContents(true), then ReloadTab.
            { "Game.Systems.UIUX.MatStorageTab+<>c__DisplayClass22_0::<BuildContentsList>b__0",
                "1E6C7F0749C4D6C419B5BCEBCF078E31C2EACDAC450318AF7DFCF050F088232C" },
            // Row click: current contents, native quantity options/labels and menu.
            { "Game.Systems.UIUX.MatStorageTab+<>c__DisplayClass22_1::<BuildContentsList>b__1",
                "27C248DCB493928093B47AFB98B5AAD042670BF2CA8CD228EF693EDD832FD2B9" },
            // Quantity click: native eject and returned-pile policy; no row mutation.
            { "Game.Systems.UIUX.MatStorageTab+<>c__DisplayClass22_2::<BuildContentsList>b__2",
                "FE5A1FDBA00F454128265127B65792F3AC0C72122F3D35DEAE81A4F847771B39" }
        };
        private static FieldInfo Label;
        private static FieldInfo Icon;
        private static FieldInfo CountLabel;
        private static readonly ConditionalWeakTable<MatStorageTab, Panel> Panels = new ConditionalWeakTable<MatStorageTab, Panel>();
        private static bool disabled;
        [ThreadStatic] private static string guardStage;
        [ThreadStatic] private static string firstFailure;

        private static void GuardDetail(string detail)
        {
            try { if (QuantumPanelDiagnostics.Enabled) { QuantumPanelDiagnostics.Guard(detail); } }
            catch (Exception) { }
        }

        private static void GuardException(string stage, Exception ex)
        {
            try { if (QuantumPanelDiagnostics.Enabled) { QuantumPanelDiagnostics.GuardException(stage, ex); } }
            catch (Exception) { }
        }

        private static void Outcome(MatStorageTab tab, string reason)
        {
            try { if (QuantumPanelDiagnostics.Enabled) { QuantumPanelDiagnostics.Outcome(tab, reason); } }
            catch (Exception) { }
        }

        private static bool CheckGuard(bool accepted, string stage)
        {
            guardStage = stage;
            if (!accepted && firstFailure == null) { firstFailure = stage; }
            return accepted;
        }

        private sealed class Row
        {
            internal MatType Type;
            internal WButtonIconText2 Widget;
            internal string Name;
            internal string Icon;
        }

        private sealed class Panel
        {
            internal MatStorageComp Storage;
            internal WGroup Block;
            internal Lifetime Life;
            internal MaterialPanelRows<MatType, Row> Rows;
        }

        [HarmonyPrepare]
        private static bool Prepare()
        {
            bool reviewed = false;
            guardStage = "root-resolution";
            firstFailure = null;
            try
            {
                var method = AccessTools.DeclaredMethod(typeof(MatStorageTab), "BuildContentsList", Type.EmptyTypes);
                var label = DeclaredField(typeof(WIconText), "lastText", typeof(string), FieldAttributes.Family, "widget-label");
                var icon = DeclaredField(typeof(WIconText), "lastIcon", typeof(string), FieldAttributes.Family, "widget-icon");
                var countLabel = DeclaredField(typeof(WButtonIconText2), "lastText2", typeof(string), FieldAttributes.Family, "widget-count");
                reviewed = label != null && icon != null && countLabel != null &&
                    DeclaredField(typeof(MatStorageTab), "storage", typeof(MatStorageComp), FieldAttributes.Private, "tab-storage") != null &&
                    DeclaredField(typeof(MatStorageTab), "contentsBlock", typeof(WGroup), FieldAttributes.Private, "tab-block") != null &&
                    DeclaredField(typeof(MatStorageTab), "contentsLifetime", typeof(Lifetime), FieldAttributes.Private, "tab-lifetime") != null &&
                    HasReviewedClosureFields() && IsReviewedContract(method);
                if (reviewed) { Label = label; Icon = icon; CountLabel = countLabel; }
            }
            // An unavailable/refactored optional UI contract must not fail mod startup.
            catch (Exception ex) { reviewed = false; GuardException(guardStage ?? "prepare", ex); }
            try
            {
                if (QuantumPanelDiagnostics.Enabled)
                {
                    GuardDetail("guard verdict=" + (reviewed ? "accepted" : "refused") + " first=" +
                        (reviewed ? "none" : firstFailure ?? guardStage ?? "prepare") +
                        " later-contract-checks=" + (reviewed ? "completed" : "short-circuited"));
                }
            }
            catch (Exception) { }
            if (!reviewed) { D.Warn("Quantum Storage material-panel optimization disabled: reviewed UI contract could not be verified; using the complete native panel."); }
            return reviewed;
        }

        internal static bool IsExpectedField(FieldInfo field, Type owner, Type fieldType, FieldAttributes access) =>
            field != null && field.DeclaringType == owner && field.FieldType == fieldType &&
            !field.IsStatic && !field.IsLiteral && !field.IsInitOnly &&
            field.GetRequiredCustomModifiers().Length == 0 && field.GetOptionalCustomModifiers().Length == 0 &&
            (field.Attributes & FieldAttributes.FieldAccessMask) == access;

        private static FieldInfo DeclaredField(Type owner, string name, Type fieldType, FieldAttributes access, string stage)
        {
            guardStage = stage;
            var field = owner?.GetField(name, DeclaredMethods);
            return CheckGuard(IsExpectedField(field, owner, fieldType, access), stage) ? field : null;
        }

        private static bool HasReviewedClosureFields()
        {
            var root = typeof(MatStorageTab);
            guardStage = "closure-types";
            var all = root.GetNestedType("<>c__DisplayClass22_0", BindingFlags.NonPublic);
            var material = root.GetNestedType("<>c__DisplayClass22_1", BindingFlags.NonPublic);
            var quantity = root.GetNestedType("<>c__DisplayClass22_2", BindingFlags.NonPublic);
            return CheckGuard(all != null && material != null && quantity != null &&
                all.GetFields(DeclaredMethods).Length == 2 && material.GetFields(DeclaredMethods).Length == 2 &&
                quantity.GetFields(DeclaredMethods).Length == 2, "closure-schema") &&
                DeclaredField(all, "comp", typeof(MatStorageComp), FieldAttributes.Public, "closure-storage") != null &&
                DeclaredField(all, "<>4__this", root, FieldAttributes.Public, "closure-tab") != null &&
                DeclaredField(material, "matType", typeof(MatType), FieldAttributes.Public, "closure-material") != null &&
                DeclaredField(material, "CS$<>8__locals1", all, FieldAttributes.Public, "closure-chain-one") != null &&
                DeclaredField(quantity, "num", typeof(int), FieldAttributes.Public, "closure-quantity") != null &&
                DeclaredField(quantity, "CS$<>8__locals2", material, FieldAttributes.Public, "closure-chain-two") != null;
        }

        internal static bool IsReviewedBody(MethodInfo method) =>
            CheckGuard(method != null && method.DeclaringType == typeof(MatStorageTab) && method.Name == "BuildContentsList" &&
            IsActionSignature(method), "root-signature") &&
            CheckGuard(ReviewedHash(method, "root", ReviewedBodyHash) == ReviewedBodyHash, "root-digest");

        // Only called during patch preparation. No reflection/hash work is added to
        // contents notifications, and Core continues to own every action callback.
        internal static bool IsReviewedContract(MethodInfo method) => IsReviewedBody(method) &&
            MatchesReviewedCallbacks(ContentsCallbacks(method.DeclaringType).Select(FingerprintCallback));

        private static KeyValuePair<string, string> FingerprintCallback(MethodInfo callback)
        {
            guardStage = "callback-signature";
            string key = CallbackKey(callback);
            string hash;
            if (QuantumPanelDiagnostics.Enabled)
            {
                string slot = key == "Game.Systems.UIUX.MatStorageTab+<>c__DisplayClass22_0::<BuildContentsList>b__0" ? "eject-all" :
                    key == "Game.Systems.UIUX.MatStorageTab+<>c__DisplayClass22_1::<BuildContentsList>b__1" ? "menu" :
                    key == "Game.Systems.UIUX.MatStorageTab+<>c__DisplayClass22_2::<BuildContentsList>b__2" ? "quantity" : "unreviewed-callback";
                ReviewedCallbackHashes.TryGetValue(key ?? "", out var expected);
                hash = ReviewedHash(callback, slot, expected);
            }
            else { hash = BodyHash(callback); }
            return new KeyValuePair<string, string>(key, hash);
        }

        private static bool IsActionSignature(MethodInfo method) => !method.IsStatic && !method.IsGenericMethod &&
            method.ReturnType == typeof(void) && method.GetParameters().Length == 0;

        private static string CallbackKey(MethodInfo method) => IsActionSignature(method) ?
            method.DeclaringType.FullName + "::" + method.Name : null;

        private static IEnumerable<MethodInfo> ContentsCallbacks(Type type)
        {
            foreach (var method in type.GetMethods(DeclaredMethods))
            {
                if (method.Name.StartsWith("<BuildContentsList>", StringComparison.Ordinal)) { yield return method; }
            }
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                foreach (var method in ContentsCallbacks(nested)) { yield return method; }
            }
        }

        internal static bool MatchesReviewedCallbacks(IEnumerable<KeyValuePair<string, string>> callbacks)
        {
            guardStage = "callback-discovery";
            if (callbacks == null) { return CheckGuard(false, "callback-discovery"); }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var callback in callbacks)
            {
                // Keep the original short-circuit order, without printing arbitrary
                // discovered names. Only fixed refusal stages enter diagnostics.
                if (callback.Key == null) { return CheckGuard(false, "callback-signature"); }
                if (!seen.Add(callback.Key)) { return CheckGuard(false, "callback-duplicate"); }
                if (!ReviewedCallbackHashes.TryGetValue(callback.Key, out var reviewed))
                { return CheckGuard(false, "callback-unreviewed"); }
                if (callback.Value != reviewed) { return CheckGuard(false, "callback-digest"); }
            }
            return CheckGuard(seen.Count == ReviewedCallbackHashes.Count, "callback-count");
        }

        internal static string BodyHash(MethodInfo method) => MaterialPanelContractFingerprint.Hash(method);

        private static string ReviewedHash(MethodInfo method, string slot, string expected)
        {
            guardStage = "fingerprint-entry";
            if (!QuantumPanelDiagnostics.Enabled) { return BodyHash(method); }
            MaterialPanelContractFingerprint.DiagnosticResult result;
            try { result = MaterialPanelContractFingerprint.HashDiagnostic(method); }
            catch (Exception ex)
            {
                // Includes helper static initialization before Hash's own catch.
                // If diagnostics itself cannot start, preserve the original path.
                GuardException("fingerprint-entry", ex);
                return BodyHash(method);
            }
            string hash = result.Hash;
            // A failed sink/format operation cannot alter a computed hash/verdict.
            try
            {
                string N(int value) => value.ToString(CultureInfo.InvariantCulture);
                GuardDetail("fingerprint slot=" + slot + " result=" +
                    (hash == null ? result.Reason : hash == expected ? "matched" : "digest-mismatch") +
                    " stage=" + result.Stage + " expected=" + (expected ?? "unreviewed") +
                    " actual=" + (hash ?? "null") + " body=" + (result.HasBody ? "yes" : "no") +
                    " il=" + N(result.IlBytes) + " flags=" + (result.HasBodyFlags ? N(result.MethodFlags) + "/" + N(result.ImplementationFlags) : "not-read") +
                    " init=" + (result.HasBodyFlags ? result.InitLocals ? "yes" : "no" : "not-read") +
                    " stack=" + (result.HasBodyFlags ? N(result.MaxStack) : "not-read") +
                    " locals=" + (result.HasBodyFlags ? N(result.Locals) : "not-read") +
                    " eh=" + (result.HasExceptionClauses ? N(result.Clauses) : "not-read") + " offset=" + N(result.Offset) +
                    " table=" + N(result.TokenTable) + " depth=" + N(result.SignatureDepth) +
                    " exception=" + (result.ExceptionType ?? "none") + "/" + N(result.ExceptionHResult) +
                    " inner=" + (result.InnerType ?? "none") + "/" + N(result.InnerHResult) +
                    " inner2=" + (result.InnerInnerType ?? "none") + "/" + N(result.InnerInnerHResult));
            }
            catch (Exception) { }
            return hash;
        }

        private static bool Managed(MatStorageComp storage)
        {
            return storage != null && (QuantumStorageNetworkSys.TryGetNode(storage, out _) || StorageRelaySys.IsRelay(storage));
        }

        private static bool Prefix(MatStorageTab __instance, MatStorageComp ___storage,
            WGroup ___contentsBlock, Lifetime ___contentsLifetime, out bool __state)
        {
            __state = false;
            if (disabled || !Managed(___storage))
            { Outcome(__instance, disabled ? "disabled" : "unmanaged"); return true; }
            __state = true;
            try
            {
                if (!Panels.TryGetValue(__instance, out var panel))
                { Outcome(__instance, "uncached"); return true; }
                if (!ReferenceEquals(panel.Storage, ___storage) || !ReferenceEquals(panel.Block, ___contentsBlock) ||
                    !ReferenceEquals(panel.Life, ___contentsLifetime))
                { Outcome(__instance, "context-changed"); return true; }
                if (panel.Life.IsDisposed) { Outcome(__instance, "lifetime-disposed"); return true; }

                var contents = ___storage.SortedContents?.ToList() ?? new List<KeyValuePair<MatType, int>>();
                bool invalidRow = false;
                bool reused = panel.Rows.TryUpdate(contents,
                    row => {
                        bool valid = row.Widget != null && !row.Widget.IsDisposed &&
                        row.Widget.transform.parent == panel.Block.Container &&
                        row.Name == row.Type.NameT && row.Icon == row.Type.IconId;
                        if (!valid) { invalidRow = true; }
                        return valid;
                    },
                    (row, count) => row.Widget.SetText2(Units.Num(count)),
                    (row, index) => row.Widget.SetSiblingIdx(index));
                if (!reused) { Outcome(__instance, invalidRow ? "invalid-row" : "structural-refused"); return true; }
                __state = false;
                Outcome(__instance, "reused");
                return false;
            }
            catch (Exception ex)
            {
                Disable(__instance, ex);
                __state = false;
                return true;
            }
        }

        private static void Postfix(MatStorageTab __instance, MatStorageComp ___storage,
            WGroup ___contentsBlock, Lifetime ___contentsLifetime, bool __state)
        {
            if (!__state || disabled) { return; }
            Panels.Remove(__instance);
            try
            {
                if (___contentsBlock == null || ___contentsLifetime == null || ___contentsLifetime.IsDisposed)
                { Outcome(__instance, "capture-context-refused"); return; }
                var contents = ___storage.SortedContents?.ToList() ?? new List<KeyValuePair<MatType, int>>();
                var rows = ___contentsBlock.Container.GetComponentsInChildren<WButtonIconText2>(false);
                if (rows.Length != contents.Count) { Outcome(__instance, "capture-count-refused"); return; }
                var captured = new List<Row>(rows.Length);
                for (int i = 0; i < rows.Length; i++)
                {
                    var widget = rows[i];
                    var item = contents[i];
                    // Do not assume a changed/modded hierarchy has Core's known shape.
                    if (widget.IsDisposed || widget.transform.parent != ___contentsBlock.Container ||
                        widget.transform.GetSiblingIndex() != i ||
                        (string)Label.GetValue(widget) != item.Key.NameT ||
                        (string)Icon.GetValue(widget) != item.Key.IconId ||
                        (string)CountLabel.GetValue(widget) != Units.Num(item.Value))
                    { Outcome(__instance, "capture-row-refused"); return; }
                    captured.Add(new Row { Type = item.Key, Widget = widget, Name = item.Key.NameT, Icon = item.Key.IconId });
                }
                var panel = new Panel { Storage = ___storage, Block = ___contentsBlock, Life = ___contentsLifetime,
                    Rows = new MaterialPanelRows<MatType, Row>(contents, captured) };
                Panels.Add(__instance, panel);
                ___contentsLifetime.OnDispose(() => {
                    if (Panels.TryGetValue(__instance, out var current) && ReferenceEquals(current, panel))
                    { Panels.Remove(__instance); }
                });
                Outcome(__instance, "captured");
            }
            catch (Exception ex) { Disable(__instance, ex); }
        }

        private static void Disable(MatStorageTab tab, Exception ex)
        {
            Panels.Remove(tab);
            disabled = true;
            Outcome(tab, "runtime-disable");
            D.LogEx(ex, "Quantum Storage material-panel optimization disabled; reverting to the full native list.");
        }
    }
}
