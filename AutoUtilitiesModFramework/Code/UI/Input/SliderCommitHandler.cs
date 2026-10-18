using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace AutoUtilitiesModFramework;

/// <summary>Separates slider dragging from its final committed value.</summary>
/// <example><code>
/// slider.onValueChanged.AddListener(value =&gt; Preview(value));
/// var commit = slider.gameObject.AddComponent&lt;SliderCommitHandler&gt;();
/// commit.OnCommit = () =&gt; Save(slider.value);
/// </code></example>
public sealed class SliderCommitHandler : MonoBehaviour, IPointerUpHandler
{
    /// <summary>Callback raised when the player releases the slider pointer.</summary>
    public Action OnCommit;
    /// <summary>Unity pointer callback; consumers should not call it directly.</summary>
    public void OnPointerUp(PointerEventData eventData) => OnCommit?.Invoke();
}
