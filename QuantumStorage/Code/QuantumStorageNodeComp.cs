using System;
using Game.Utils;
using KL.Utils;

namespace Game.Components
{
    public sealed class QuantumStorageNodeComp : BaseComponent<QuantumStorageNodeComp>
    {
        public string NetworkId { get; private set; }

        public int BaseCapacity { get; private set; }

        public int CapacityMultiplier { get; private set; }

        public int MaxNetworkCapacity { get; private set; }

        public float FireFeedbackChance { get; private set; }

        public float ExplosionFeedbackChance { get; private set; }

        public float FeedbackDamageFraction { get; private set; }

        [SelfInit(SelfInit.Stage.CoreDefs, 0)]
        private static void Register()
        {
            AddComponentPrototype(new QuantumStorageNodeComp());
        }

        protected override void OnConfig()
        {
            NetworkId = Config.GetString(Hashes.S("NetworkId"), "RawMaterials");
            if (string.IsNullOrWhiteSpace(NetworkId))
            {
                NetworkId = "RawMaterials";
            }

            BaseCapacity = Math.Max(1, Config.GetInt(Hashes.S("BaseCapacity"), 500));
            CapacityMultiplier = Math.Max(1, Config.GetInt(Hashes.S("CapacityMultiplier"), 2));
            MaxNetworkCapacity = Math.Max(BaseCapacity, Config.GetInt(Hashes.S("MaxNetworkCapacity"), 2000000000));
            FireFeedbackChance = Clamp01(Config.GetFloat(Hashes.S("FireFeedbackChance"), 0.02f));
            ExplosionFeedbackChance = Clamp01(Config.GetFloat(Hashes.S("ExplosionFeedbackChance"), 0.05f));
            FeedbackDamageFraction = Clamp01(Config.GetFloat(Hashes.S("FeedbackDamageFraction"), 0.2f));
        }

        private static float Clamp01(float value)
        {
            return Math.Max(0f, Math.Min(1f, value));
        }

        public override string ToString()
        {
            return $"QuantumStorageNode[{NetworkId}] {Entity}";
        }
    }
}
