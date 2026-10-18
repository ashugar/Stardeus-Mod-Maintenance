using ColonistAndBotsAutoUpgrade.Systems;
using UnityEngine;

namespace ColonistAndBotsAutoUpgrade.UI
{
    /// <summary>
    /// Performs the small amount of real-time work required by the CARAU
    /// tracking widget.
    ///
    /// Why this is a MonoBehaviour instead of a normal game tick:
    /// Stardeus game ticks stop while paused, but players commonly queue and
    /// cancel surgery while paused. Unity Update continues running, allowing
    /// CARAU to refresh the widget without requiring time to advance.
    ///
    /// Reuse guidance for other mods:
    /// keep this component intentionally thin. The component owns timing only;
    /// all state and decisions remain in the GameSystem. Attach it to a
    /// persistent UI object and pass the owning system to Initialize().
    /// </summary>
    public sealed class CARAUOperationTrackingUpdater : MonoBehaviour
    {
        private const float RefreshIntervalSeconds = 0.20f;

        private CARAUOperationTrackingSys owner;
        private float nextRefreshTime;

        public void Initialize(CARAUOperationTrackingSys system)
        {
            owner = system;
            nextRefreshTime = 0f;
        }

        private void Update()
        {
            if (owner == null ||
                Time.unscaledTime < nextRefreshTime)
            {
                return;
            }

            nextRefreshTime =
                Time.unscaledTime + RefreshIntervalSeconds;
            owner.RefreshTrackedOperations();
        }
    }
}
