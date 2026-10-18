using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Game.Constants;
using Game.Data;
using Game.Input;
using Game.Systems.Creatures;
using Game.Systems.Mental;
using KL.Grid;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Game.Systems.AutoHauling {
    /// <summary>
    /// Enables the two Core link forms exposed by haul failure reasons:
    /// map coordinates and Being/Mind names. The handler is attached only to
    /// failure-row detail text and does not alter the saved diagnostic value.
    /// </summary>
    internal sealed class FailureTextLinkHandler : MonoBehaviour {
        private static readonly Regex PositionPattern = new Regex(
            @"(\[-?\d+:-?\d+\])", RegexOptions.Compiled);

        private TMP_Text text;
        private RectTransform rowRoot;
        private RectTransform viewport;
        private EventSystem cachedEventSystem;
        private PointerEventData pointerEventData;
        private readonly List<RaycastResult> raycastResults =
            new List<RaycastResult>(8);

        internal void Initialize(
                TMP_Text target, RectTransform row, RectTransform clipViewport,
                string rawText) {
            text = target;
            rowRoot = row;
            viewport = clipViewport;
            if (text == null) {
                return;
            }
            text.raycastTarget = true;
            text.SetText(AddPositionLinks(rawText ?? string.Empty));
        }

        private void LateUpdate() {
            // Stardeus consumes clicks through its input bindings rather than
            // Unity's IPointerClickHandler path for these managed panel rows.
            // Mirror Core's clickable-text polling while limiting the hit to
            // this row so links cannot be activated through another window.
            if (text == null
                    || !text.gameObject.activeInHierarchy
                    || text.textInfo.linkCount == 0
                    || The.Bindings?.Mouse == null) {
                return;
            }
            Vector2 screenPosition = The.Bindings.Mouse.ScreenPosition;
            if (viewport != null
                    && !TMP_TextUtilities.IsIntersectingRectTransform(
                        viewport, screenPosition, null)) {
                return;
            }
            if (!TMP_TextUtilities.IsIntersectingRectTransform(
                    text.rectTransform, screenPosition, null)) {
                return;
            }
            int linkIndex = TMP_TextUtilities.FindIntersectingLink(
                text, screenPosition, null);
            if (linkIndex < 0
                    || linkIndex >= text.textInfo.linkCount) {
                return;
            }

            EventSystem currentEventSystem = EventSystem.current;
            if (currentEventSystem == null) {
                return;
            }
            if (!ReferenceEquals(cachedEventSystem, currentEventSystem)) {
                cachedEventSystem = currentEventSystem;
                pointerEventData = new PointerEventData(currentEventSystem);
            }
            pointerEventData.position = screenPosition;
            raycastResults.Clear();
            currentEventSystem.RaycastAll(pointerEventData, raycastResults);
            if (raycastResults.Count == 0) {
                return;
            }
            Transform hit = raycastResults[0].gameObject.transform;
            if (rowRoot != null
                    && hit != rowRoot
                    && !hit.IsChildOf(rowRoot)) {
                return;
            }
            if (!The.Bindings.IsJustPressed(ActionType.Click)) {
                return;
            }

            string linkId =
                text.textInfo.linkInfo[linkIndex].GetLinkID();
            if (!TryNavigate(linkId)) {
                The.SysSig.ShowTooltipWithTimeout.Send(
                    T.NotAvailable, true, 1f);
            }
            The.SysSig.TextLinkClicked.Send();
        }

        private static bool TryNavigate(string linkId) {
            GameState state = The.R?.State;
            if (state == null || string.IsNullOrEmpty(linkId)) {
                return false;
            }
            if (Pos.TryParse(linkId, out int position)) {
                EntityUtils.CameraFocusOn(position);
                return true;
            }

            char prefix = linkId[0];
            if (prefix != 'b' && prefix != 'm') {
                return false;
            }
            if (!uint.TryParse(linkId.Substring(1), out uint id)) {
                return false;
            }

            BeingData being = prefix == 'b'
                ? state.Query.FindBeingData.Ask(id)
                : state.Query.FindMind.Ask(id)?.Owner;
            return being != null && BeingUtils.SelectFocused(being);
        }

        private static string AddPositionLinks(string rawText) {
            return PositionPattern.Replace(rawText, match => {
                // Do not wrap a coordinate that Core has already supplied
                // inside a link tag.
                int lastOpen = rawText.LastIndexOf(
                    "<link", match.Index,
                    StringComparison.OrdinalIgnoreCase);
                int lastClose = rawText.LastIndexOf(
                    "</link>", match.Index,
                    StringComparison.OrdinalIgnoreCase);
                if (lastOpen > lastClose) {
                    return match.Value;
                }
                return "<nobr><link=\"" + match.Value + "\"><u>"
                    + match.Value + "</u></link></nobr>";
            });
        }
    }
}
