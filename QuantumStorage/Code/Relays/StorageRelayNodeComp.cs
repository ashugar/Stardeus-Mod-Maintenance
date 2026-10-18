using System;
using Game.Utils;
using Game.Systems.QuantumStorage.Relays;
using KL.Utils;
using Game.Constants;
using UnityEngine;

namespace Game.Components
{
    // Deliberately NOT QuantumStorageNodeComp: no late-game pool, damage feedback,
    // object-Hive delivery, or optional TACC route can enroll this component.
    public sealed class StorageRelayNodeComp : BaseComponent<StorageRelayNodeComp>, IRanged
    {
        public string NetworkId { get; private set; }
        public int Capacity { get; private set; }
        public int Range { get; private set; }

        // Core uses IRanged for both build ghosts and selected buildings. Use
        // the exact link radius, not electrical connector or Hive coverage.
        float IRanged.Range => Range;
        public Color RadiusColor => Consts.ColorTurretRange;
        public bool RadiusSameArea => false;

        [SelfInit(SelfInit.Stage.CoreDefs, 0)]
        private static void Register() => AddComponentPrototype(new StorageRelayNodeComp());

        protected override void OnConfig()
        {
            NetworkId = Config.GetString(Hashes.S("NetworkId"), "RawMaterials");
            Capacity = Math.Max(1, Config.GetInt(Hashes.S("LocalCapacity"), 250));
            Range = Math.Max(1, Math.Min(12, Config.GetInt(Hashes.S("LinkRange"), 12)));
        }

        public override void OnLateReady(bool wasLoaded) => StorageRelaySys.Get(S)?.Attach(this);
        public override void Receive(IComponent sender, int message)
        {
            if (message == Game.CodeGen.MsgIdH.ConstructionFinished) { StorageRelaySys.Get(S)?.Attach(this); }
            if (message == Game.CodeGen.MsgIdH.ElectricNodeSwitchedOff ||
                message == Game.CodeGen.MsgIdH.ElectricNodeSwitchedOn ||
                message == Game.CodeGen.MsgIdH.TilePosChanged)
            { StorageRelaySys.Get(S)?.InvalidateTopology(); }
        }
        public override void OnRemove() => StorageRelaySys.Get(S)?.Detach(this);
    }
}
