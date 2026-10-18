using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace AutoUtilitiesModFramework;

/// <summary>Commits a TMP input on Enter or when focus leaves the field.</summary>
/// <remarks>
/// <see cref="PanelContentContext.AddInput"/> already provides this behavior.
/// Use this component only for a custom <see cref="TMP_InputField"/>.
/// Submit detection is event-driven through <see cref="TMP_InputField.onSubmit"/>;
/// this helper deliberately does not poll Unity's legacy Input API because
/// Stardeus uses the Input System package exclusively.
/// </remarks>
/// <example><code>
/// var commit = input.gameObject.AddComponent&lt;InputCommitHandler&gt;();
/// commit.InputField = input;
/// commit.OnCommit = value =&gt; SaveName(value);
/// input.onSubmit.AddListener(_ =&gt; commit.RequestCommit());
/// </code></example>
public sealed class InputCommitHandler : MonoBehaviour, IDeselectHandler
{
    /// <summary>The custom TextMesh Pro input being monitored.</summary>
    public TMP_InputField InputField { get; set; }
    /// <summary>Callback receiving the final text after a commit.</summary>
    public Action<string> OnCommit { get; set; }
    private bool commitRequested;

    /// <summary>Unity event callback; requests a commit when focus is lost.</summary>
    public void OnDeselect(BaseEventData eventData) => RequestCommit();
    /// <summary>
    /// Queues one commit for late update. Use from submit callbacks so multiple
    /// UI events in one frame collapse into one consumer callback.
    /// </summary>
    public void RequestCommit() => commitRequested = true;

    private void LateUpdate()
    {
        if (!commitRequested) return;
        commitRequested = false;
        OnCommit?.Invoke(InputField?.text);
    }
}
