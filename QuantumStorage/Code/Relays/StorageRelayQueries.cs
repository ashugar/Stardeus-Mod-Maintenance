using System;
using System.Collections.Generic;
using Game.Components;

namespace Game.Systems.QuantumStorage.Relays
{
    public sealed partial class StorageRelaySys
    {
        private long queryGeneration, membershipRevision, querySnapshotMembershipRevision, querySnapshotVersion;
        private bool querySnapshotReady, queryObjectsReady;
        private MaterialQueryEntry[] queryMaterialVertices = Array.Empty<MaterialQueryEntry>();
        private ObjectQueryEntry[] queryObjectVertices = Array.Empty<ObjectQueryEntry>();
        private int queryMaterialCount, queryObjectCount;
        private List<StorageRelayNodeComp> lastTopologyNodes = new List<StorageRelayNodeComp>();
        [ThreadStatic] private static MaterialReadScope materialRead;
        private MaterialReadScope activatingMaterialRead;
        private int advisoryReadWorldThread;

        internal void PrepareAdvisoryReadWorld()
        { if (ready && Operational && advisoryReadWorldThread == 0) advisoryReadWorldThread = System.Threading.Thread.CurrentThread.ManagedThreadId; }
        internal bool CanPrepareAdvisoryReadWorld => ready && Operational &&
            advisoryReadWorldThread == System.Threading.Thread.CurrentThread.ManagedThreadId;

        // Snapshot entries retain private registry identities. Public/native facts
        // are still read on every boundary; only registry hashing is avoided.
        private readonly struct MaterialQueryEntry
        {
            internal readonly MatStorageComp Storage;
            internal readonly QueryVertex Vertex;
            internal MaterialQueryEntry(MatStorageComp storage, QueryVertex vertex)
            { Storage = storage; Vertex = vertex; }
        }

        private readonly struct ObjectQueryEntry
        {
            internal readonly int RegistryId;
            internal readonly ObjStorageComp Storage;
            internal readonly QueryVertex Vertex;
            internal ObjectQueryEntry(int registryId, ObjStorageComp storage, QueryVertex vertex)
            { RegistryId = registryId; Storage = storage; Vertex = vertex; }
        }

        // Snapshots contain topology facts only, never inventory/reservation totals.
        private readonly struct QueryVertex
        {
            internal readonly StorageRelayNodeComp Node;
            internal readonly int Id, X, Y, Range;
            internal readonly string Network;
            internal readonly bool Active, IsObject;

            private QueryVertex(StorageRelayNodeComp node, int id, string network, int range, int x, int y, bool active, bool isObject)
            { Node = node; Id = id; Network = network; Range = range; X = x; Y = y; Active = active; IsObject = isObject; }

            internal static QueryVertex ForGrouping(StorageRelayNodeComp node)
            {
                // Retain the original grouping getter order and bind every later
                // snapshot to these exact facts, rather than recapturing newer ones.
                int id = node.EntityId;
                bool isObject = node.Entity.TryGetComponent<ObjStorageComp>(out _);
                string network = node.NetworkId;
                int x = (int)node.Tile.Position.x, y = (int)node.Tile.Position.y;
                int range = node.Range;
                bool active = StorageRelaySys.Active(node);
                return new QueryVertex(node, id, network, range, x, y, active, isObject);
            }

#if QUANTUM_RELAY_QUERY_BASELINE
            internal bool MatchesDetailed(StorageRelayNodeComp node, bool objects)
            {
                int offset = objects ? 4 : 0;
                var sample = RelayQueryBaseline.BeginDetail(offset);
                bool metadata;
                try
                {
                    metadata = ReferenceEquals(Node, node) && node.Tile != null && Id == node.EntityId && Network == node.NetworkId &&
                        Range == node.Range && X == (int)node.Tile.Position.x && Y == (int)node.Tile.Position.y;
                }
                finally { RelayQueryBaseline.EndDetail(sample); }
                if (!metadata) return false;
                sample = RelayQueryBaseline.BeginDetail(offset + 1);
                bool kind;
                try { kind = IsObject == node.Entity.TryGetComponent<ObjStorageComp>(out _); }
                finally { RelayQueryBaseline.EndDetail(sample); }
                if (!kind) return false;
                sample = RelayQueryBaseline.BeginDetail(offset + 2);
                try { return Active == StorageRelaySys.Active(node); }
                finally { RelayQueryBaseline.EndDetail(sample); }
            }
#endif
            internal bool Matches(StorageRelayNodeComp node)
            {
                return ReferenceEquals(Node, node) && node.Tile != null && Id == node.EntityId && Network == node.NetworkId &&
                    Range == node.Range && X == (int)node.Tile.Position.x && Y == (int)node.Tile.Position.y &&
                    IsObject == node.Entity.TryGetComponent<ObjStorageComp>(out _) &&
                    Active == StorageRelaySys.Active(node);
            }
        }

        private void AdvanceQueryGeneration() { unchecked { queryGeneration++; } }
        private void AdvanceMembershipRevision() { unchecked { membershipRevision++; } }

        // Rare invalidation path: inspect CURRENT registrations, not stale dense
        // entries, before the existing rebuild dereferences their tile/entity.
        private bool CurrentRegistryReadable()
        {
            long revision = membershipRevision;
            bool includeObjects = objectsReady;
            try
            {
                foreach (var pair in nodes)
                {
                    if (pair.Value?.Tile == null || pair.Value.Entity == null) { return false; }
                }
                if (includeObjects)
                foreach (var storage in objectNodes.Values)
                {
                    if (storage?.Entity == null) { return false; }
                    var node = storage.Entity.GetComponent<StorageRelayNodeComp>();
                    if (node?.Tile == null) { return false; }
                }
            }
            catch (InvalidOperationException) when (revision != membershipRevision)
            { return false; }
            return revision == membershipRevision && includeObjects == objectsReady;
        }

        private bool SnapshotMembershipMatches(long revision, long version)
        {
            return querySnapshotReady && revision == membershipRevision && revision == querySnapshotMembershipRevision &&
                version == querySnapshotVersion && queryObjectsReady == objectsReady && queryMaterialCount == nodes.Count &&
                queryObjectCount == (objectsReady ? objectNodes.Count : 0);
        }

        private bool QueryTopologyMatches(out bool readable)
        {
#if QUANTUM_RELAY_QUERY_BASELINE
            RelayQueryBaseline.NoteTopologyWalk();
#endif
            long revision = membershipRevision, version = querySnapshotVersion;
            if (!SnapshotMembershipMatches(revision, version))
            { readable = CurrentRegistryReadable(); return false; }
            readable = true;
            bool matches = true;
            int materialCount = queryMaterialCount, objectCount = queryObjectCount;
#if QUANTUM_RELAY_QUERY_BASELINE
            int detailVertex = RelayQueryBaseline.SelectDetailVertex(materialCount + objectCount);
#endif
            for (int i = 0; i < materialCount; i++)
            {
                if (revision != membershipRevision || version != querySnapshotVersion || queryObjectsReady != objectsReady)
                { readable = CurrentRegistryReadable(); return false; }
                ref readonly MaterialQueryEntry entry = ref queryMaterialVertices[i];
                ref readonly QueryVertex vertex = ref entry.Vertex;
                var node = vertex.Node;
                if (node?.Tile == null || node.Entity == null)
                {
                    readable = SnapshotMembershipMatches(revision, version) ? false : CurrentRegistryReadable();
                    return false;
                }
#if QUANTUM_RELAY_QUERY_BASELINE
                if (i == detailVertex) { if (!vertex.MatchesDetailed(node, false)) { matches = false; } }
                else
#endif

                if (!vertex.Matches(node)) { matches = false; }
            }
            for (int i = 0; i < objectCount; i++)
            {
                if (revision != membershipRevision || version != querySnapshotVersion || queryObjectsReady != objectsReady)
                { readable = CurrentRegistryReadable(); return false; }
                ref readonly ObjectQueryEntry entry = ref queryObjectVertices[i];
                var storage = entry.Storage;
                if (storage?.Entity == null)
                {
                    readable = SnapshotMembershipMatches(revision, version) ? false : CurrentRegistryReadable();
                    return false;
                }
                // The component array is public and may change in place. Always
                // rediscover the actual node with Core's original native getter.
#if QUANTUM_RELAY_QUERY_BASELINE
                StorageRelayNodeComp node;
                if (materialCount + i == detailVertex)
                {
                    var detail = RelayQueryBaseline.BeginDetail(3);
                    try { node = storage.Entity.GetComponent<StorageRelayNodeComp>(); }
                    finally { RelayQueryBaseline.EndDetail(detail); }
                }
                else node = storage.Entity.GetComponent<StorageRelayNodeComp>();
#else
                var node = storage.Entity.GetComponent<StorageRelayNodeComp>();
#endif
                if (node?.Tile == null)
                {
                    readable = SnapshotMembershipMatches(revision, version) ? false : CurrentRegistryReadable();
                    return false;
                }
                ref readonly QueryVertex vertex = ref entry.Vertex;
#if QUANTUM_RELAY_QUERY_BASELINE
                if (materialCount + i == detailVertex) { if (!vertex.MatchesDetailed(node, true)) { matches = false; } }
                else
#endif
                if (!vertex.Matches(node)) { matches = false; }
            }
            if (!SnapshotMembershipMatches(revision, version))
            { readable = CurrentRegistryReadable(); return false; }
            return matches;
        }

        private static void EnsureQueryCapacity<T>(ref T[] entries, int count)
        {
            if (entries.Length >= count) { return; }
            int doubled = entries.Length <= int.MaxValue / 2 ? entries.Length * 2 : count;
            Array.Resize(ref entries, Math.Max(count, Math.Max(4, doubled)));
        }

        private readonly struct QueryBuildMember
        {
            internal readonly MatStorageComp Material;
            internal readonly ObjStorageComp Object;
            internal readonly int ObjectId;
            internal readonly StorageRelayNodeComp Node;
            internal QueryBuildMember(MatStorageComp material, StorageRelayNodeComp node)
            { Material = material; Object = null; ObjectId = 0; Node = node; }
            internal QueryBuildMember(int id, ObjStorageComp objects, StorageRelayNodeComp node)
            { Material = null; Object = objects; ObjectId = id; Node = node; }
            internal bool SameRegistration(QueryBuildMember other) => ReferenceEquals(Material, other.Material) &&
                ReferenceEquals(Object, other.Object) && ObjectId == other.ObjectId && ReferenceEquals(Node, other.Node);
        }

        private bool CaptureQueryTopology(QueryBuildMember[] members, QueryVertex[] buildFacts, long revision, bool includeObjects)
        {
            querySnapshotReady = false;
            long version;
            unchecked { version = ++querySnapshotVersion; }
            if (revision != membershipRevision || includeObjects != objectsReady || members.Length != buildFacts.Length ||
                members.Length != nodes.Count + (includeObjects ? objectNodes.Count : 0)) { return false; }
            int oldMaterialCount = queryMaterialCount, oldObjectCount = queryObjectCount;
            EnsureQueryCapacity(ref queryMaterialVertices, nodes.Count);
            EnsureQueryCapacity(ref queryObjectVertices, includeObjects ? objectNodes.Count : 0);
            var materialEntries = queryMaterialVertices;
            var objectEntries = queryObjectVertices;
            int materialCount = 0, objectCount = 0;
            try
            {
                for (int i = 0; i < members.Length; i++)
                {
                    ref readonly QueryBuildMember member = ref members[i];
                    ref readonly QueryVertex vertex = ref buildFacts[i];
                    var node = member.Node;
                    if (member.Material == null)
                    {
                        if (member.Object?.Entity == null) { return false; }
                        node = member.Object.Entity.GetComponent<StorageRelayNodeComp>();
                    }
                    if (node?.Tile == null || node.Entity == null || !vertex.Matches(node)) { return false; }
                    if (version != querySnapshotVersion || revision != membershipRevision || includeObjects != objectsReady) { return false; }
                    if (member.Material != null)
                        materialEntries[materialCount++] = new MaterialQueryEntry(member.Material, vertex);
                    else
                        objectEntries[objectCount++] = new ObjectQueryEntry(member.ObjectId, member.Object, vertex);
                }
                if (version != querySnapshotVersion || revision != membershipRevision || includeObjects != objectsReady) { return false; }
                querySnapshotMembershipRevision = revision;
                queryObjectsReady = includeObjects;
                querySnapshotReady = materialCount == nodes.Count && objectCount == (includeObjects ? objectNodes.Count : 0);
                return querySnapshotReady;
            }
            finally
            {
                // A reentrant capture owns its own publication. Do not overwrite
                // or clear entries it published while a native getter was called.
                if (version == querySnapshotVersion)
                {
                    queryMaterialCount = materialCount; queryObjectCount = objectCount;
                    if (oldMaterialCount > materialCount) Array.Clear(materialEntries, materialCount, oldMaterialCount - materialCount);
                    if (oldObjectCount > objectCount) Array.Clear(objectEntries, objectCount, oldObjectCount - objectCount);
                }
            }
        }

        private bool AbortTopologyBuild()
        {
            querySnapshotReady = false;
            unchecked { querySnapshotVersion++; }
            InvalidateTopology();
            return false;
        }

        private void ResetQueryTopology()
        {
            Array.Clear(queryMaterialVertices, 0, queryMaterialCount);
            Array.Clear(queryObjectVertices, 0, queryObjectCount);
            queryMaterialCount = queryObjectCount = 0;
            querySnapshotReady = false;
            topologyMembershipRevision = long.MinValue;
            lastTopologyMembers = Array.Empty<QueryBuildMember>();
            unchecked { querySnapshotVersion++; }
        }

#if QUANTUM_RELAY_QUERY_BASELINE
        private bool ValidateQueryTopology()
        {
            var sample = RelayQueryBaseline.BeginTopology();
            if (sample.Entered)
            {
                try { return ValidateQueryTopologyCore(); }
                finally { RelayQueryBaseline.EndTopology(sample); }
            }
            return ValidateQueryTopologyCore();
        }

        private bool ValidateQueryTopologyCore()
#else
        private bool ValidateQueryTopology()
#endif
        {
            if (rebuilding) { return false; }
            if (!Operational || !ready) { return true; }
            if (QueryTopologyMatches(out bool readable)) { return true; }
            // A component in removal may remain registered until Detach. Refuse
            // this bounded read rather than dereferencing it in the rebuild path.
            if (!readable) { return false; }
            // Actual metadata is examined on every boundary, including paused or
            // same-tick changes. Only differences run the allocating rebuild path.
            if (!EnsureTopology(true)) { return false; }
            return !rebuilding && QueryTopologyMatches(out _);
        }

        internal RelayGroup Group(MatStorageComp storage, bool force = false)
        {
            if (RelayCore.LocalDepth == 0)
            {
                MarkMaterialReadDependency();
                // A native getter may synchronously reenter a read during the
                // initial freshness walk. Forced operations retain their path;
                // a repaired generation may use the native committed-map fallback.
                if (!force && !CanReadDuringMaterialActivation()) { return null; }
            }
            if (!Operational || !ready || rebuilding || RelayCore.LocalDepth > 0) { return null; }
            if (!force && TryScopedGroup(storage, out var scoped))
            { if (scoped != null) { Refresh(scoped); } return scoped; }
            if (!EnsureTopology(force)) { return null; }
            if (!groups.TryGetValue(storage, out var group)) { return null; }
            // Read views refresh at most once per clock tick. Every operation
            // that touches remote stock forces a fresh topology check, including
            // bridge changes within that same tick. Local power is always checked.
            if (!nodes.TryGetValue(storage, out var node) || !Active(node))
            {
                if (!EnsureTopology(true)) { return null; }
                if (!groups.TryGetValue(storage, out group)) { return null; }
            }
            Refresh(group);
            return group;
        }

        internal MaterialReadScope BeginMaterialRead()
        {
            if (RelayCore.LocalDepth > 0) { return null; }
            bool valid;
            if (activatingMaterialRead != null)
            { activatingMaterialRead.Invalidate(); valid = false; }
            else { valid = ValidateQueryTopology(); }
            var scope = new MaterialReadScope(this, materialRead, valid, true);
            materialRead = scope;
            return scope;
        }

        internal MaterialReadScope BeginDeferredMaterialRead()
        {
            if (RelayCore.LocalDepth > 0) { return null; }
            bool valid = !rebuilding && activatingMaterialRead == null;
            if (activatingMaterialRead != null) { activatingMaterialRead.Invalidate(); }
            var scope = new MaterialReadScope(this, materialRead, valid, false);
            materialRead = scope;
            return scope;
        }

        internal MaterialReadScope BeginAdvisoryMaterialRead()
        {
            if (RelayCore.LocalDepth > 0) { return null; }
            // An enclosing strict query must not lose its stronger guarantee
            // merely because an ordinary provider evaluates a Nanobot goal.
            for (var outer = materialRead; outer != null; outer = outer.Previous)
                if (!outer.Disposed && ReferenceEquals(outer.Owner, this) && !outer.Advisory)
                    return BeginDeferredMaterialRead();
            bool valid = !rebuilding && activatingMaterialRead == null;
            if (activatingMaterialRead != null) { activatingMaterialRead.Invalidate(); }
            var scope = new MaterialReadScope(this, materialRead, valid, false, true);
            materialRead = scope;
            return scope;
        }

        private void MarkMaterialReadDependency()
        {
            // A nested query can feed a Relay-dependent value into an ordinary
            // outer provider. Every live enclosing attempt must retain a final
            // freshness boundary, not just the scope serving this lookup.
            for (var scope = materialRead; scope != null; scope = scope.Previous)
                if (!scope.Disposed && ReferenceEquals(scope.Owner, this)) { scope.Touched = true; }
        }

        private bool CanReadDuringMaterialActivation()
        {
            if (activatingMaterialRead == null) { return true; }
            activatingMaterialRead.Invalidate();
            // Generation changes occur under the topology-build guard, through
            // InvalidateTopology (which clears lastTick), or during unload. The
            // original Group path therefore either refuses or freshly ensures
            // maps before reading them. Post-publication UI callbacks can see
            // the repaired maps, while this older query must still retry.
            return !activatingMaterialRead.OriginalGenerationMatches;
        }

        private void ActivateMaterialReadDependency()
        {
            for (var scope = materialRead; scope != null; scope = scope.Previous)
                if (!scope.Disposed && ReferenceEquals(scope.Owner, this))
                { scope.Activate(); return; }
        }

        private bool TryScopedGroup(MatStorageComp storage, out RelayGroup group)
        {
            for (var scope = materialRead; scope != null; scope = scope.Previous)
            {
                if (!scope.Disposed && ReferenceEquals(scope.Owner, this))
                { group = scope.Pool(storage); return scope.Valid; }
            }
            group = null;
            return false;
        }

        internal sealed class MaterialReadScope : IDisposable
        {
            internal readonly StorageRelaySys Owner;
            internal readonly MaterialReadScope Previous;
            private readonly long generation;
            private readonly bool operational, ready;
            private readonly long advisoryTick;
            private readonly int advisoryEpoch;
            private bool valid;
            internal bool Advisory { get; }
            internal bool Touched { get; set; }
            internal bool Activated { get; private set; }
            internal bool Disposed { get; private set; }
            internal bool Valid => valid && !Disposed;
            internal bool OriginalGenerationMatches => generation == Owner.queryGeneration;

            internal MaterialReadScope(StorageRelaySys owner, MaterialReadScope previous, bool valid, bool activated, bool advisory = false)
            {
                Owner = owner; Previous = previous; this.valid = valid;
                Activated = Touched = activated;
                Advisory = advisory;
                if (advisory) { advisoryTick = Game.Constants.G.Ticks; advisoryEpoch = NanobotCandidateQueries.Epoch; }
                generation = owner.queryGeneration; operational = owner.Operational; ready = owner.ready;
            }

            internal void Invalidate() { valid = false; }

            private bool StampsMatch() => generation == Owner.queryGeneration &&
                operational == Owner.Operational && ready == Owner.ready && (!Advisory ||
                    advisoryTick == Game.Constants.G.Ticks && advisoryEpoch == NanobotCandidateQueries.Epoch && !Owner.rebuilding);

            private bool RefreshAdvisoryTopology(bool force)
            {
#if QUANTUM_RELAY_QUERY_BASELINE
                var sample = RelayQueryBaseline.BeginTopology();
                if (!force) { RelayQueryBaseline.NoteAdvisoryRefresh(Owner.lastTick == Game.Constants.G.Ticks); }
                try { return Owner.EnsureTopology(force); }
                finally { RelayQueryBaseline.EndTopology(sample); }
#else
                return Owner.EnsureTopology(force);
#endif
            }

            internal bool Activate()
            {
                Touched = true;
                if (!Valid || !StampsMatch()) { valid = false; return false; }
                if (Owner.activatingMaterialRead != null)
                { Owner.activatingMaterialRead.Invalidate(); valid = false; return false; }
                if (Activated) { return true; }
                // Keep the constructor's generation from before ordinary-prefix
                // work. Repairing topology here must retry that earlier work,
                // never accept its keys under a newly captured generation.
                Activated = true;
                Owner.activatingMaterialRead = this;
                try
                {
                    // Restore the original planning read view only in the
                    // reviewed Nanobot claim context. No quantity is cached.
                    bool current = Advisory ? !operational || !ready || RefreshAdvisoryTopology(false) : Owner.ValidateQueryTopology();
                    valid = valid && current && StampsMatch();
                    return Valid;
                }
                catch { valid = false; throw; }
                finally { Owner.activatingMaterialRead = null; }
            }

            internal RelayGroup Pool(MatStorageComp storage)
            {
#if QUANTUM_RELAY_QUERY_BASELINE
                if (RelayQueryFocus.TryEnterRelayPoolProbe()) { return PoolProfiled(storage); }
#endif
                if (RelayCore.LocalDepth > 0) { return null; }
                Touched = true;
                Owner.MarkMaterialReadDependency();
                if (!Activate()) { return null; }
                if (!operational || !ready || RelayCore.LocalDepth > 0) { return null; }
                if (Advisory)
                {
                    // Original Group ordering: an already excluded offline
                    // endpoint stays excluded; a mapped entry going offline
                    // forces current topology before the result is re-read.
                    if (!Owner.groups.TryGetValue(storage, out var advisoryGroup)) { return null; }
                    if (!Owner.nodes.TryGetValue(storage, out var advisoryNode) || !Active(advisoryNode))
                    {
                        if (!RefreshAdvisoryTopology(true)) { valid = false; return null; }
                        if (!Owner.groups.TryGetValue(storage, out advisoryGroup)) { return null; }
                    }
                    return advisoryGroup;
                }
                if (!Owner.nodes.TryGetValue(storage, out var node) || !Active(node)) { return null; }
                return Owner.groups.TryGetValue(storage, out var group) ? group : null;
            }

#if QUANTUM_RELAY_QUERY_BASELINE
            // Only the root's selected key reaches this copy. Nested queries and
            // rebuilding run the unchanged body above. One inner category is
            // clocked; refresh/refetch branches are counted without extra clocks.
            private RelayGroup PoolProfiled(MatStorageComp storage)
            {
                int kind = RelayQueryFocus.PoolInnerKind;
                long token = RelayQueryFocus.BeginStage(12, kind == 0);
                try
                {
                    if (RelayCore.LocalDepth > 0) { return null; }
                    Touched = true;
                    Owner.MarkMaterialReadDependency();
                    if (!Activate()) { return null; }
                    if (!operational || !ready || RelayCore.LocalDepth > 0) { return null; }
                }
                finally { RelayQueryFocus.EndStage(12, token); }
                if (Advisory)
                {
                    RelayGroup advisoryGroup;
                    StorageRelayNodeComp advisoryNode;
                    bool foundNode;
                    token = RelayQueryFocus.BeginStage(13, kind == 1);
                    try
                    {
                        if (!Owner.groups.TryGetValue(storage, out advisoryGroup)) { return null; }
                        foundNode = Owner.nodes.TryGetValue(storage, out advisoryNode);
                    }
                    finally { RelayQueryFocus.EndStage(13, token); }
                    if (!foundNode || !ProfiledActive(advisoryNode, kind == 2))
                    {
                        RelayQueryFocus.NoteForcedRefresh();
                        if (!RefreshAdvisoryTopology(true)) { valid = false; return null; }
                        RelayQueryFocus.NoteRefetch();
                        if (!Owner.groups.TryGetValue(storage, out advisoryGroup)) { return null; }
                    }
                    return advisoryGroup;
                }
                StorageRelayNodeComp node;
                bool found;
                token = RelayQueryFocus.BeginStage(13, kind == 1);
                try { found = Owner.nodes.TryGetValue(storage, out node); }
                finally { RelayQueryFocus.EndStage(13, token); }
                if (!found || !ProfiledActive(node, kind == 2)) { return null; }
                return Owner.groups.TryGetValue(storage, out var group) ? group : null;
            }
            private static bool ProfiledActive(StorageRelayNodeComp node, bool timed)
            {
                long token = RelayQueryFocus.BeginStage(14, timed);
                try { return Active(node); }
                finally { RelayQueryFocus.EndStage(14, token); }
            }
#endif

            internal bool Validate()
            {
                if (!Valid || !StampsMatch()) { valid = false; return false; }
                // No Relay-derived value, including a negative Group answer,
                // entered this attempt. Native ordinary providers stand alone.
                if (!Touched) { return true; }
                if (!Activated) { return Activate(); }
                if (Advisory) { return true; }
                try
                {
                    bool current = Owner.ValidateQueryTopology();
                    valid = valid && current && StampsMatch();
                    return Valid;
                }
                catch { valid = false; throw; }
            }

            public void Dispose()
            {
                if (Disposed) { return; }
                if (!StampsMatch()) { valid = false; }
#if QUANTUM_RELAY_QUERY_BASELINE
                RelayQueryBaseline.NoteReadScope(Touched, Activated, Valid, Advisory);
#endif
                Disposed = true;
                if (ReferenceEquals(materialRead, this)) { materialRead = Previous; }
            }
        }
    }
}
