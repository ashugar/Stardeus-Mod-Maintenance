using System;
using System.Collections.Generic;
using System.Linq;
using Game.Components;
using Game.Constants;

namespace Game.Systems.QuantumStorage.Relays
{
    public sealed partial class StorageRelaySys
    {
        private long topologyMembershipRevision = long.MinValue;
        private QueryBuildMember[] lastTopologyMembers = Array.Empty<QueryBuildMember>();

        private bool EnsureTopology(bool force)
        {
            if (!ready || rebuilding) { return false; }
            if (!force && lastTick == G.Ticks) { return true; }
            QueryBuildMember[] members;
            bool changed, complete;
            long revision, committedGeneration;
            rebuilding = true;
            try
            {
                // Both full native reads must match the same committed snapshot.
                // This avoids sorting and temporary build arrays on an unchanged
                // tick or forced access without using a tick/event freshness cache.
                long memberStamp = membershipRevision, snapshotStamp = querySnapshotVersion, generationStamp = queryGeneration;
                bool unchanged = QueryTopologyMatches(out bool readable);
                if (unchanged) { unchanged = QueryTopologyMatches(out readable); }
                if (unchanged && SnapshotMembershipMatches(memberStamp, snapshotStamp) && generationStamp == queryGeneration)
                { lastTick = G.Ticks; return true; }
                if (!readable) { return AbortTopologyBuild(); }
                complete = BuildTopology(out members, out changed, out revision, out committedGeneration);
            }
            catch { AbortTopologyBuild(); throw; }
            finally { rebuilding = false; }
            if (!complete) { return false; }
            if (changed)
            {
                // Notifications are outside the metadata transaction, so native
                // controls observe committed online maps. Iterate immutable build
                // targets; callbacks may legally change the current registry.
                foreach (var member in members)
                {
                    if (member.Material != null && nodes.TryGetValue(member.Material, out var current) && ReferenceEquals(current, member.Node))
                        QuantumStorageReflection.RefreshDisplay(member.Material);
                }
                RefreshObjectDisplays();
                if (revision != membershipRevision || committedGeneration != queryGeneration || !QueryTopologyMatches(out _))
                    return AbortTopologyBuild();
            }
            return true;
        }

        private bool BuildTopology(out QueryBuildMember[] members, out bool changed, out long revision, out long committedGeneration)
        {
            revision = membershipRevision;
            bool includeObjects = objectsReady;
            members = null; changed = false; committedGeneration = queryGeneration;
            lastTick = G.Ticks;
            List<StorageRelayNodeComp> ordered;
            QueryVertex[] facts;
            List<RelayVertex> vertices;
            try
            {
                var registrations = new List<QueryBuildMember>(nodes.Count + (includeObjects ? objectNodes.Count : 0));
                foreach (var pair in nodes)
                {
                    if (pair.Value?.Tile == null || pair.Value.Entity == null) { return AbortTopologyBuild(); }
                    registrations.Add(new QueryBuildMember(pair.Key, pair.Value));
                }
                if (includeObjects)
                foreach (var pair in objectNodes)
                {
                    if (pair.Value?.Entity == null) { return AbortTopologyBuild(); }
                    var node = pair.Value.Entity.GetComponent<StorageRelayNodeComp>();
                    if (node?.Tile == null) { return AbortTopologyBuild(); }
                    registrations.Add(new QueryBuildMember(pair.Key, pair.Value, node));
                }
                members = registrations.OrderBy(m => m.Node.EntityId).ToArray();
                ordered = members.Select(m => m.Node).ToList();
                facts = new QueryVertex[members.Length];
                vertices = new List<RelayVertex>(members.Length);
                for (int i = 0; i < members.Length; i++)
                {
                    if (revision != membershipRevision || includeObjects != objectsReady) { return AbortTopologyBuild(); }
                    var fact = QueryVertex.ForGrouping(members[i].Node);
                    facts[i] = fact;
                    vertices.Add(new RelayVertex { Id = fact.Id, Kind = (fact.IsObject ? "obj:" : "mat:") + fact.Network,
                        X = fact.X, Y = fact.Y, Range = fact.Range, Active = fact.Active });
                }
            }
            catch (InvalidOperationException) when (revision != membershipRevision)
            { return AbortTopologyBuild(); }
            if (revision != membershipRevision || includeObjects != objectsReady) { return AbortTopologyBuild(); }

            changed = topologyMembershipRevision == long.MinValue || members.Length != lastTopologyMembers.Length ||
                vertices.Count != lastVertices.Count || ordered.Count != lastTopologyNodes.Count;
            for (int i = 0; !changed && i < vertices.Count; i++)
            {
                var a = vertices[i]; var b = lastVertices[i];
                changed = !members[i].SameRegistration(lastTopologyMembers[i]) || !ReferenceEquals(ordered[i], lastTopologyNodes[i]) ||
                    a.Id != b.Id || a.X != b.X || a.Y != b.Y || a.Kind != b.Kind || a.Range != b.Range || a.Active != b.Active;
            }
            if (changed)
            {
                AdvanceQueryGeneration();
                var materialGroups = new Dictionary<MatStorageComp, RelayGroup>();
                var objectsGroups = new Dictionary<int, RelayGroup>();
                foreach (var component in RelayPolicy.Components(vertices))
                {
                    // Use exactly the facts supplied to Components. A fresh
                    // kind read here could combine two different topologies.
                    var group = new RelayGroup { IsObject = facts[component[0]].IsObject };
                    foreach (int i in component)
                    {
                        group.Nodes.Add(ordered[i]);
                        if (group.IsObject) { objectsGroups[facts[i].Id] = group; }
                        else
                        {
                            var storage = Storage(ordered[i]);
                            if (storage == null) { return AbortTopologyBuild(); }
                            materialGroups[storage] = group;
                        }
                    }
                    if (group.IsObject) { RefreshObjects(group); } else { Refresh(group); }
                }
                if (!CaptureQueryTopology(members, facts, revision, includeObjects)) { return AbortTopologyBuild(); }
                // Both map references are private, and every access refuses
                // while rebuilding. No partial map can escape this commit.
                groups = materialGroups; objectGroups = objectsGroups;
                lastVertices = vertices; lastTopologyNodes = ordered;
            }
            else if (!CaptureQueryTopology(members, facts, revision, includeObjects)) { return AbortTopologyBuild(); }
            topologyMembershipRevision = revision; lastTopologyMembers = members;
            committedGeneration = queryGeneration;
            return true;
        }

        internal RelayGroup ObjectGroup(ObjStorageComp storage, bool force = false)
        {
            if (RelayObjectCore.LocalDepth == 0)
            {
                MarkMaterialReadDependency();
                if (!force)
                {
                    if (!CanReadDuringMaterialActivation()) { return null; }
                    ActivateMaterialReadDependency();
                }
            }
            if (!Operational || !ready || !objectsReady || rebuilding || RelayObjectCore.LocalDepth > 0 || storage == null) { return null; }
            if (!EnsureTopology(force)) { return null; }
            if (!Active(storage.Entity.GetComponent<StorageRelayNodeComp>())) { EnsureTopology(true); return null; }
            if (!objectGroups.TryGetValue(storage.EntityId, out var group)) { return null; }
            RefreshObjects(group);
            return group;
        }

        internal bool ConnectedObjects(int ownerId, ObjStorageComp access)
        {
            MarkMaterialReadDependency();
            if (!Operational || !ready || !objectsReady || rebuilding || access == null) { return false; }
            if (!EnsureTopology(true)) { return false; }
            return objectGroups.TryGetValue(ownerId, out var a) && objectGroups.TryGetValue(access.EntityId, out var b) && ReferenceEquals(a, b);
        }
    }
}
