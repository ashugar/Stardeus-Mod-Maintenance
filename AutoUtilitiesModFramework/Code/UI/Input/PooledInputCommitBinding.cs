using System;
using Game.Systems.UIUX;
using Game.Utils;
using TMPro;
using UnityEngine;

namespace AutoUtilitiesModFramework;

/// <summary>Owns one AddInput commit binding for one content lifetime.</summary>
internal sealed class PooledInputCommitBinding
{
    private TMP_InputField field;
    private Lifetime lifetime;
    private Action<string> committed;
    private int lastCommitFrame = -1;

    private PooledInputCommitBinding(TMP_InputField field, Lifetime lifetime,
        Action<string> committed)
    {
        this.field = field;
        this.lifetime = lifetime;
        this.committed = committed;
    }

    internal static void Bind(WInput input, Lifetime lifetime,
        Action<string> committed)
    {
        var binding = new PooledInputCommitBinding(input.InputField, lifetime,
            committed);
        input.SetOnSubmit(binding.Commit);
        input.InputField.onEndEdit.AddListener(binding.Commit);
        lifetime.OnDispose(binding.Dispose);
    }

    private void Commit(string value)
    {
        // Core marks the lifetime disposed before deactivating its widgets.
        // Ignore events raised by that teardown, and stale invocation snapshots.
        if (lifetime == null || lifetime.IsDisposed
            || lastCommitFrame == Time.frameCount) return;
        lastCommitFrame = Time.frameCount;
        committed?.Invoke(value);
    }

    private void Dispose()
    {
        // Invalidate first, including any delegate already being dispatched.
        // Release the consumer and lifetime even if Core retains our submit slot.
        committed = null;
        lifetime = null;
        if (field != null) field.onEndEdit.RemoveListener(Commit);
        field = null;

        // WInput exposes a submit setter, but no getter/conditional removal.
        // Do not clear a replacement installed by a consumer. Our old submit
        // delegate is now inert and retains no consumer, field or lifetime;
        // the next binding replaces that single Core slot normally.
    }
}
