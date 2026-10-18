using System;
using System.Collections.Generic;
using System.Linq;
using AutoUtilitiesModFramework;
using KL.Grid;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ReconnectTeleporterConnections;

internal sealed class TeleporterGraphView : MonoBehaviour
{
    private const float NodeWidth = 176f;
    private const float NodeHeight = 68f;

    private readonly List<TrackedTeleporter> teleporters = new();
    private readonly Dictionary<int, TrackedTeleporter> recordsById = new();
    private readonly Dictionary<int, RectTransform> nodes = new();
    private readonly List<EdgeVisual> edges = new();
    private ReconnectTeleporterConnectionsSys system;
    private RectTransform graphRect;
    private Vector2 previousSize = new Vector2(-1f, -1f);

    internal void Configure(ReconnectTeleporterConnectionsSys owner,
        IEnumerable<TrackedTeleporter> trackedTeleporters, RectTransform root)
    {
        system = owner;
        graphRect = root;
        teleporters.Clear();
        teleporters.AddRange(trackedTeleporters
            .Where(item => item != null)
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Position));
        recordsById.Clear();
        foreach (TrackedTeleporter record in teleporters)
            recordsById[record.Id] = record;
        CreateEdges();
        CreateNodes();
        Relayout();
    }

    private void LateUpdate()
    {
        if (graphRect == null) return;
        Vector2 size = graphRect.rect.size;
        if ((size - previousSize).sqrMagnitude > 0.25f) Relayout();
    }

    private void CreateNodes()
    {
        foreach (TrackedTeleporter teleporter in teleporters)
        {
            TrackedTeleporter captured = teleporter;
            string label = string.IsNullOrWhiteSpace(teleporter.Label)
                ? "Teleporter"
                : teleporter.Label;
            string nodeLabel = label + "\n" + Pos.String(
                teleporter.Position)
                + (teleporter.IsMissing ? " • rebuilding" : string.Empty);
            bool selected = !teleporter.IsMissing && teleporter.EntityId
                == system.SelectedGraphEntityId;
            Button button = UIComponentFactory.CreateTabButton(graphRect,
                nodeLabel, selected,
                () => system.SelectGraphTeleporter(captured),
                NodeWidth, NodeWidth);
            button.gameObject.name = "TeleporterRecord_" + teleporter.Id;
            button.GetComponent<Image>().color = NodeColor(
                ConnectionStateFor(teleporter));
            RectTransform rect = button.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(NodeWidth, NodeHeight);
            LayoutElement layout = button.GetComponent<LayoutElement>();
            layout.minWidth = NodeWidth;
            layout.preferredWidth = NodeWidth;
            layout.minHeight = NodeHeight;
            layout.preferredHeight = NodeHeight;

            TextMeshProUGUI text = button.GetComponentInChildren<
                TextMeshProUGUI>();
            text.fontSize = 15f;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.lineSpacing = -5f;
            UIComponentFactory.SetTip(button,
                (teleporter.IsMissing
                    ? system.Describe(teleporter)
                        + " is waiting for reconstruction. "
                    : "Select and focus " + system.Describe(teleporter)
                        + ". ")
                + "Tracked outbound: "
                + (teleporter.DesiredOutboundId == 0
                    ? "not linked"
                    : system.DesiredDestinationDescription(teleporter))
                + ". " + ConnectionStateDescription(teleporter));
            nodes[teleporter.Id] = rect;
        }
    }

    private void CreateEdges()
    {
        foreach (TrackedTeleporter source in teleporters)
        {
            if (source.DesiredOutboundId == 0
                || !recordsById.TryGetValue(source.DesiredOutboundId,
                    out TrackedTeleporter target))
            {
                continue;
            }

            var lineObject = new GameObject("Connection_"
                + source.Id + "_" + target.Id);
            lineObject.transform.SetParent(graphRect, false);
            RectTransform line = lineObject.AddComponent<RectTransform>();
            line.anchorMin = new Vector2(0.5f, 0.5f);
            line.anchorMax = new Vector2(0.5f, 0.5f);
            line.pivot = new Vector2(0.5f, 0.5f);
            var image = lineObject.AddComponent<Image>();
            image.color = EdgeColor(ConnectionStateFor(source));
            image.raycastTarget = false;

            var arrowObject = new GameObject("Arrow");
            arrowObject.transform.SetParent(graphRect, false);
            RectTransform arrow = arrowObject.AddComponent<RectTransform>();
            arrow.anchorMin = new Vector2(0.5f, 0.5f);
            arrow.anchorMax = new Vector2(0.5f, 0.5f);
            arrow.pivot = new Vector2(0.5f, 0.5f);
            arrow.sizeDelta = new Vector2(24f, 24f);
            var arrowText = arrowObject.AddComponent<TextMeshProUGUI>();
            arrowText.text = "▶";
            arrowText.alignment = TextAlignmentOptions.Center;
            arrowText.fontSize = 18f;
            arrowText.color = image.color;
            arrowText.raycastTarget = false;

            edges.Add(new EdgeVisual(source.Id, target.Id,
                line, arrow));
        }
    }

    private ConnectionVisualState ConnectionStateFor(
        TrackedTeleporter source)
    {
        if (source?.DesiredOutboundId <= 0
            || !recordsById.TryGetValue(source.DesiredOutboundId,
                out TrackedTeleporter target))
        {
            return ConnectionVisualState.Unlinked;
        }

        // The color follows the direction of this Teleporter's outbound edge.
        // In a reciprocal pair with one missing endpoint, the edge pointing to
        // that endpoint is cyan while the remembered return edge is green.
        if (target.IsMissing) return ConnectionVisualState.AwaitingRebuild;
        if (target.DesiredOutboundId == source.Id)
            return ConnectionVisualState.Reciprocal;
        return ConnectionVisualState.BuiltTarget;
    }

    private string ConnectionStateDescription(TrackedTeleporter source)
    {
        ConnectionVisualState state = ConnectionStateFor(source);
        switch (state)
        {
            case ConnectionVisualState.BuiltTarget:
                return "Yellow: outbound destination is constructed.";
            case ConnectionVisualState.AwaitingRebuild:
                return "Cyan: outbound destination is waiting for reconstruction.";
            case ConnectionVisualState.Reciprocal:
                return source.IsMissing
                    ? "Green: the remembered return direction points to a "
                        + "constructed partner; its direction toward this "
                        + "rebuilding Teleporter is cyan."
                    : "Green: both constructed Teleporters point to each other.";
            default:
                return "Red: no outbound destination is recorded.";
        }
    }

    private static Color NodeColor(ConnectionVisualState state)
    {
        switch (state)
        {
            case ConnectionVisualState.BuiltTarget:
                return new Color(0.52f, 0.37f, 0.04f, 0.94f);
            case ConnectionVisualState.AwaitingRebuild:
                return new Color(0.03f, 0.38f, 0.48f, 0.94f);
            case ConnectionVisualState.Reciprocal:
                return new Color(0.06f, 0.42f, 0.18f, 0.94f);
            default:
                return new Color(0.50f, 0.09f, 0.12f, 0.94f);
        }
    }

    private static Color EdgeColor(ConnectionVisualState state)
    {
        switch (state)
        {
            case ConnectionVisualState.AwaitingRebuild:
                return new Color(0.18f, 0.88f, 1f, 0.92f);
            case ConnectionVisualState.Reciprocal:
                return new Color(0.30f, 1f, 0.48f, 0.92f);
            default:
                return new Color(1f, 0.78f, 0.12f, 0.92f);
        }
    }

    private void Relayout()
    {
        if (graphRect == null || teleporters.Count == 0) return;
        Vector2 size = graphRect.rect.size;
        if (size.x < 10f || size.y < 10f) return;
        previousSize = size;

        float radiusX = Mathf.Max(0f, size.x * 0.5f
            - NodeWidth * 0.62f);
        float radiusY = Mathf.Max(0f, size.y * 0.5f
            - NodeHeight * 0.9f);
        for (int index = 0; index < teleporters.Count; index++)
        {
            float angle = teleporters.Count == 1
                ? 0f
                : 90f - index * 360f / teleporters.Count;
            Vector2 position = teleporters.Count == 1
                ? Vector2.zero
                : new Vector2(
                    Mathf.Cos(angle * Mathf.Deg2Rad) * radiusX,
                    Mathf.Sin(angle * Mathf.Deg2Rad) * radiusY);
            nodes[teleporters[index].Id].anchoredPosition = position;
        }

        foreach (EdgeVisual edge in edges) PositionEdge(edge);
    }

    private void PositionEdge(EdgeVisual edge)
    {
        if (!nodes.TryGetValue(edge.SourceRecordId, out RectTransform source)
            || !nodes.TryGetValue(edge.TargetRecordId,
                out RectTransform target))
        {
            edge.Line.gameObject.SetActive(false);
            edge.Arrow.gameObject.SetActive(false);
            return;
        }

        Vector2 start = source.anchoredPosition;
        Vector2 end = target.anchoredPosition;
        Vector2 delta = end - start;
        float distance = delta.magnitude;
        if (distance < 1f) return;
        Vector2 direction = delta / distance;
        Vector2 normal = new Vector2(-direction.y, direction.x);
        Vector2 offset = normal * 7f;
        float trim = Mathf.Min(NodeWidth * 0.44f,
            Mathf.Max(12f, distance * 0.24f));
        Vector2 visibleStart = start + direction * trim + offset;
        Vector2 visibleEnd = end - direction * trim + offset;
        Vector2 visibleDelta = visibleEnd - visibleStart;
        float visibleLength = visibleDelta.magnitude;
        float angle = Mathf.Atan2(direction.y, direction.x)
            * Mathf.Rad2Deg;

        edge.Line.anchoredPosition = (visibleStart + visibleEnd) * 0.5f;
        edge.Line.sizeDelta = new Vector2(visibleLength, 4f);
        edge.Line.localEulerAngles = new Vector3(0f, 0f, angle);
        edge.Arrow.anchoredPosition = Vector2.Lerp(visibleStart,
            visibleEnd, 0.68f);
        edge.Arrow.localEulerAngles = new Vector3(0f, 0f, angle);
    }

    private sealed class EdgeVisual
    {
        internal readonly int SourceRecordId;
        internal readonly int TargetRecordId;
        internal readonly RectTransform Line;
        internal readonly RectTransform Arrow;

        internal EdgeVisual(int sourceRecordId, int targetRecordId,
            RectTransform line, RectTransform arrow)
        {
            SourceRecordId = sourceRecordId;
            TargetRecordId = targetRecordId;
            Line = line;
            Arrow = arrow;
        }
    }

    private enum ConnectionVisualState
    {
        Unlinked,
        BuiltTarget,
        AwaitingRebuild,
        Reciprocal
    }
}
