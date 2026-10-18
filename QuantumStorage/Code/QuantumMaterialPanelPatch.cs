using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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
        // Reviewed against rc335.3217 Game.dll SHA256 7122A8E390B8B34D9D3241960FCCF743EE022BB30B346E841C372CAC72FEC62B,
        // including all generated BuildContentsList action callbacks:
        // quantities are read at click time and callbacks no longer edit pooled rows
        // after Eject. Keep Core's callbacks and filter-panel lifecycle untouched.
        // A different root OR callback body must use the complete native panel until
        // reviewed. The root IL alone does not change when a callback body changes.
        internal const string ReviewedBodyHash = "76FF13D9C8D9F78833C6CD7F0BC7263FA03E072C7B49B615817E6FAAB2F8CD54";
        private const BindingFlags DeclaredMethods = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        private static readonly Dictionary<string, string> ReviewedCallbackHashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Eject All: native DumpContents(true), then ReloadTab.
            { "Game.Systems.UIUX.MatStorageTab+<>c__DisplayClass22_0::<BuildContentsList>b__0",
                "ED56B7B1DE015F3ABA2A14FC9BE52549269CACD4DFD58D1D7E442AF7683547A1" },
            // Row click: current contents, native quantity options/labels and menu.
            { "Game.Systems.UIUX.MatStorageTab+<>c__DisplayClass22_1::<BuildContentsList>b__1",
                "0D35DB527C74E70A4085C43E7477716B87C3D7A04787FC945D587DBA797B6609" },
            // Quantity click: native eject and returned-pile policy; no row mutation.
            { "Game.Systems.UIUX.MatStorageTab+<>c__DisplayClass22_2::<BuildContentsList>b__2",
                "A5D2C3A9FA33CB98B0AD1E98AA9D61DC8AB9CB850CAC7E062342521C4EC2E1A9" }
        };
        private static readonly FieldInfo Label = AccessTools.Field(typeof(WIconText), "lastText");
        private static readonly FieldInfo Icon = AccessTools.Field(typeof(WIconText), "lastIcon");
        private static readonly FieldInfo CountLabel = AccessTools.Field(typeof(WButtonIconText2), "lastText2");
        private static readonly ConditionalWeakTable<MatStorageTab, Panel> Panels = new ConditionalWeakTable<MatStorageTab, Panel>();
        private static bool disabled;

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
            try
            {
                var method = AccessTools.DeclaredMethod(typeof(MatStorageTab), "BuildContentsList", Type.EmptyTypes);
                reviewed = Label != null && Icon != null && CountLabel != null && IsReviewedContract(method);
            }
            // An unavailable/refactored optional UI contract must not fail mod startup.
            catch (Exception) { reviewed = false; }
            if (!reviewed) { D.Warn("Quantum Storage material-panel optimization disabled: Core UI changed; using the complete native panel."); }
            return reviewed;
        }

        internal static bool IsReviewedBody(MethodInfo method) =>
            method != null && method.DeclaringType == typeof(MatStorageTab) && method.Name == "BuildContentsList" &&
            IsActionSignature(method) && BodyHash(method) == ReviewedBodyHash;

        // Only called during patch preparation. No reflection/hash work is added to
        // contents notifications, and Core continues to own every action callback.
        internal static bool IsReviewedContract(MethodInfo method) => IsReviewedBody(method) &&
            MatchesReviewedCallbacks(ContentsCallbacks(method.DeclaringType).Select(callback =>
                new KeyValuePair<string, string>(CallbackKey(callback), BodyHash(callback))));

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
            if (callbacks == null) { return false; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var callback in callbacks)
            {
                if (callback.Key == null || !seen.Add(callback.Key) ||
                    !ReviewedCallbackHashes.TryGetValue(callback.Key, out var reviewed) ||
                    callback.Value != reviewed) { return false; }
            }
            return seen.Count == ReviewedCallbackHashes.Count;
        }

        internal static string BodyHash(MethodInfo method)
        {
            var body = method?.GetMethodBody()?.GetILAsByteArray();
            if (body == null) { return null; }
            using (var sha = SHA256.Create()) { return BitConverter.ToString(sha.ComputeHash(body)).Replace("-", ""); }
        }

        private static bool Managed(MatStorageComp storage)
        {
            return storage != null && (QuantumStorageNetworkSys.TryGetNode(storage, out _) || StorageRelaySys.IsRelay(storage));
        }

        private static bool Prefix(MatStorageTab __instance, MatStorageComp ___storage,
            WGroup ___contentsBlock, Lifetime ___contentsLifetime, out bool __state)
        {
            __state = false;
            if (disabled || !Managed(___storage)) { return true; }
            __state = true;
            try
            {
                if (!Panels.TryGetValue(__instance, out var panel) ||
                    !ReferenceEquals(panel.Storage, ___storage) || !ReferenceEquals(panel.Block, ___contentsBlock) ||
                    !ReferenceEquals(panel.Life, ___contentsLifetime) || panel.Life.IsDisposed)
                { return true; }

                var contents = ___storage.SortedContents?.ToList() ?? new List<KeyValuePair<MatType, int>>();
                bool reused = panel.Rows.TryUpdate(contents,
                    row => row.Widget != null && !row.Widget.IsDisposed &&
                        row.Widget.transform.parent == panel.Block.Container &&
                        row.Name == row.Type.NameT && row.Icon == row.Type.IconId,
                    (row, count) => row.Widget.SetText2(Units.Num(count)),
                    (row, index) => row.Widget.SetSiblingIdx(index));
                if (!reused) { return true; }
                __state = false;
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
                if (___contentsBlock == null || ___contentsLifetime == null || ___contentsLifetime.IsDisposed) { return; }
                var contents = ___storage.SortedContents?.ToList() ?? new List<KeyValuePair<MatType, int>>();
                var rows = ___contentsBlock.Container.GetComponentsInChildren<WButtonIconText2>(false);
                if (rows.Length != contents.Count) { return; }
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
                        (string)CountLabel.GetValue(widget) != Units.Num(item.Value)) { return; }
                    captured.Add(new Row { Type = item.Key, Widget = widget, Name = item.Key.NameT, Icon = item.Key.IconId });
                }
                var panel = new Panel { Storage = ___storage, Block = ___contentsBlock, Life = ___contentsLifetime,
                    Rows = new MaterialPanelRows<MatType, Row>(contents, captured) };
                Panels.Add(__instance, panel);
                ___contentsLifetime.OnDispose(() => {
                    if (Panels.TryGetValue(__instance, out var current) && ReferenceEquals(current, panel))
                    { Panels.Remove(__instance); }
                });
            }
            catch (Exception ex) { Disable(__instance, ex); }
        }

        private static void Disable(MatStorageTab tab, Exception ex)
        {
            Panels.Remove(tab);
            disabled = true;
            D.LogEx(ex, "Quantum Storage material-panel optimization disabled; reverting to the full native list.");
        }
    }
}
