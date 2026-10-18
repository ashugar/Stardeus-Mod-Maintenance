// Isolated tests of the production binding source, not an in-game Unity test.
// Kept outside Code and .vscode so the mod project cannot include these doubles.
// The doubles model the inspected Core lifetime order and UnityEvent snapshots.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AutoUtilitiesModFramework;
using Game.Systems.UIUX;
using Game.Utils;
using UnityEngine;

namespace UnityEngine { public static class Time { public static int frameCount; } }
namespace UnityEngine.Events { public delegate void UnityAction<T>(T value); }
namespace TMPro
{
    public sealed class TMP_InputField
    {
        public readonly EditEvent onEndEdit = new EditEvent();
        public sealed class EditEvent
        {
            private readonly List<UnityEngine.Events.UnityAction<string>> listeners =
                new List<UnityEngine.Events.UnityAction<string>>();
            public int Count => listeners.Count;
            public void AddListener(UnityEngine.Events.UnityAction<string> callback) => listeners.Add(callback);
            public void RemoveListener(UnityEngine.Events.UnityAction<string> callback) => listeners.RemoveAll(x => x == callback);
            public void RemoveAllListeners() => listeners.Clear();
            public Action<string> Snapshot()
            {
                var copy = listeners.ToArray();
                return value => { foreach (var callback in copy) callback(value); };
            }
            public void Invoke(string value) => Snapshot()(value);
        }
    }
}
namespace Game.Systems.UIUX
{
    public sealed class WInput
    {
        public readonly TMPro.TMP_InputField InputField = new TMPro.TMP_InputField();
        public Action<string> Submit;
        public void SetOnSubmit(Action<string> callback) => Submit = callback;
    }
}
namespace Game.Utils
{
    public sealed class Lifetime
    {
        public bool IsDisposed { get; private set; }
        private Action onDispose;
        public Action DeactivateWidgets;
        public Lifetime OnDispose(Action callback) { onDispose += callback; return this; }
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            DeactivateWidgets?.Invoke();
            onDispose?.Invoke();
            onDispose = null;
            DeactivateWidgets = null;
        }
    }
}

internal static class BindingTests
{
    private static int passed;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static void Test(string name, Action run)
    {
        Time.frameCount += 10;
        run();
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private static void LegacyBind(WInput input, Action<string> committed)
    {
        // Negative control: the original AddInput binding, before this repair.
        int lastCommitFrame = -1;
        void CommitOnce(string value)
        {
            if (lastCommitFrame == Time.frameCount) return;
            lastCommitFrame = Time.frameCount;
            committed?.Invoke(value);
        }
        input.SetOnSubmit(CommitOnce);
        input.InputField.onEndEdit.AddListener(CommitOnce);
    }

    private sealed class Receiver { public void Commit(string value) { } }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] BindAndRelease(WInput input)
    {
        var receiver = new Receiver();
        var lifetime = new Lifetime();
        var weak = new[] { new WeakReference(receiver), new WeakReference(lifetime) };
        PooledInputCommitBinding.Bind(input, lifetime, receiver.Commit);
        lifetime.Dispose();
        return weak;
    }

    public static int Main()
    {
        try
        {
            Test("negative control reproduces accumulation", () => {
                var input = new WInput();
                int calls = 0;
                for (int i = 0; i < 50; i++) LegacyBind(input, _ => calls++);
                input.InputField.onEndEdit.Invoke("old");
                Check(input.InputField.onEndEdit.Count == 50 && calls == 50, "Legacy leak not reproduced");
            });
            Test("submit and end-edit commit once per frame", () => {
                var input = new WInput(); var life = new Lifetime(); int calls = 0;
                string value = null;
                PooledInputCommitBinding.Bind(input, life, x => { calls++; value = x; });
                input.Submit("17"); input.InputField.onEndEdit.Invoke("17");
                Check(calls == 1 && value == "17", "Duplicate or incorrect submit");
                Time.frameCount++; input.InputField.onEndEdit.Invoke("18");
                Check(calls == 2 && value == "18", "Later edit not committed");
                life.Dispose();
            });
            Test("reverse event order is deduplicated", () => {
                var input = new WInput(); var life = new Lifetime(); int calls = 0;
                PooledInputCommitBinding.Bind(input, life, _ => calls++);
                input.InputField.onEndEdit.Invoke("a"); input.Submit("a");
                Check(calls == 1, "Reverse order duplicated commit"); life.Dispose();
            });
            Test("1000 reuses keep one live binding and correct role", () => {
                var input = new WInput(); int calls = 0; int role = -1;
                for (int i = 0; i < 1000; i++)
                {
                    var life = new Lifetime(); int expected = i;
                    PooledInputCommitBinding.Bind(input, life, _ => { role = expected; calls++; });
                    Check(input.InputField.onEndEdit.Count == 1, "Listeners accumulated");
                    Time.frameCount++; input.InputField.onEndEdit.Invoke("value");
                    Check(role == i && calls == i + 1, "Old role received edit");
                    life.Dispose();
                    Check(input.InputField.onEndEdit.Count == 0, "Listener not removed");
                }
            });
            Test("independent fields commit in the same frame", () => {
                var a = new WInput(); var b = new WInput(); var life = new Lifetime(); int calls = 0;
                PooledInputCommitBinding.Bind(a, life, _ => calls++);
                PooledInputCommitBinding.Bind(b, life, _ => calls++);
                a.Submit("a"); b.Submit("b");
                Check(calls == 2, "One field suppressed another"); life.Dispose();
            });
            Test("Core and third-party end-edit listeners survive cleanup", () => {
                var input = new WInput(); var life = new Lifetime(); int native = 0, consumer = 0;
                input.InputField.onEndEdit.AddListener(_ => native++);
                PooledInputCommitBinding.Bind(input, life, _ => consumer++);
                input.InputField.onEndEdit.AddListener(_ => native++);
                life.Dispose(); input.InputField.onEndEdit.Invoke("a");
                Check(native == 2 && consumer == 0 && input.InputField.onEndEdit.Count == 2, "Unowned listeners changed");
            });
            Test("replacement submit callback is preserved", () => {
                var input = new WInput(); var life = new Lifetime(); int old = 0, replacement = 0;
                PooledInputCommitBinding.Bind(input, life, _ => old++);
                input.SetOnSubmit(_ => replacement++);
                life.Dispose(); input.Submit("a");
                Check(old == 0 && replacement == 1, "Cleanup erased replacement submit");
            });
            Test("consumer replacement of both handlers survives cleanup", () => {
                var input = new WInput(); var life = new Lifetime(); int calls = 0;
                PooledInputCommitBinding.Bind(input, life, _ => { throw new Exception("Old callback"); });
                input.InputField.onEndEdit.RemoveAllListeners();
                input.InputField.onEndEdit.AddListener(_ => calls++);
                input.SetOnSubmit(_ => calls++);
                life.Dispose(); input.Submit("a"); input.InputField.onEndEdit.Invoke("a");
                Check(calls == 2, "Consumer workaround changed");
            });
            Test("teardown events suppressed before OnDispose runs", () => {
                var input = new WInput(); var life = new Lifetime(); int calls = 0;
                PooledInputCommitBinding.Bind(input, life, _ => calls++);
                life.DeactivateWidgets = () => { input.Submit("a"); input.InputField.onEndEdit.Invoke("a"); };
                life.Dispose(); Check(calls == 0, "Disposed content committed during deactivation");
            });
            Test("already captured dispatch is inert after disposal", () => {
                var input = new WInput(); var life = new Lifetime(); int calls = 0;
                PooledInputCommitBinding.Bind(input, life, _ => calls++);
                var snapshot = input.InputField.onEndEdit.Snapshot(); var submit = input.Submit;
                life.Dispose(); Time.frameCount++; snapshot("old"); submit("old");
                Check(calls == 0, "Retired delegate still called consumer");
            });
            Test("old lifetime cleanup cannot remove a newer binding", () => {
                var input = new WInput(); var old = new Lifetime(); var current = new Lifetime(); int calls = 0;
                PooledInputCommitBinding.Bind(input, old, _ => { throw new Exception("Old callback"); });
                PooledInputCommitBinding.Bind(input, current, _ => calls++);
                old.Dispose(); input.Submit("new"); input.InputField.onEndEdit.Invoke("new");
                Check(calls == 1 && input.InputField.onEndEdit.Count == 1, "New binding damaged"); current.Dispose();
            });
            Test("callback-triggered disposal and reuse are safe", () => {
                var input = new WInput(); var old = new Lifetime(); var current = new Lifetime(); int first = 0, second = 0;
                PooledInputCommitBinding.Bind(input, old, _ => {
                    first++; old.Dispose(); PooledInputCommitBinding.Bind(input, current, x => second++);
                });
                input.Submit("old");
                Time.frameCount++; input.InputField.onEndEdit.Invoke("new");
                Check(first == 1 && second == 1, "Reentrant reuse dispatched old callback"); current.Dispose();
            });
            Test("disposal from another listener invalidates pending callback", () => {
                var input = new WInput(); var life = new Lifetime(); int calls = 0;
                input.InputField.onEndEdit.AddListener(_ => life.Dispose());
                PooledInputCommitBinding.Bind(input, life, _ => calls++);
                input.InputField.onEndEdit.Invoke("a");
                Check(calls == 0, "Pending retired callback ran");
            });
            Test("null callback and repeated disposal are safe", () => {
                var input = new WInput(); var life = new Lifetime();
                PooledInputCommitBinding.Bind(input, life, null);
                input.Submit(null); life.Dispose(); life.Dispose(); input.Submit(null);
                Check(input.InputField.onEndEdit.Count == 0, "Null callback leaked listener");
            });
            Test("inert submit slot retains no consumer, field or lifetime", () => {
                var input = new WInput(); var weak = BindAndRelease(input);
                object binding = input.Submit.Target;
                foreach (var member in binding.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                    if (!member.FieldType.IsValueType) Check(member.GetValue(binding) == null, "Retained " + member.Name);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Check(weak.All(x => !x.IsAlive), "Consumer or lifetime remains rooted");
                GC.KeepAlive(input);
            });
            Console.WriteLine("Passed " + passed + " isolated lifecycle tests.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
