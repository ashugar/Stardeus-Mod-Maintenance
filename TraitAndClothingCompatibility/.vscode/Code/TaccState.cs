using System.Collections.Generic;

namespace TraitAndClothingCompatibility {
    public sealed class ManagedBeing {
        public uint MindId;
        public uint BeingId;
        public string Name;
        public bool OptOut;
        public bool OwnsPolicy;
        public bool AutomaticPause;
        public PolicyState OriginalPolicy;
        public PolicyState LastApplied;
        public string TraitSignature;
        public string ConflictSignature;
        public WearerProfile Profile;
        public string Status;
    }

    public sealed class DiagnosticEntry {
        public string FindingKey;
        public string Time;
        public string Kind;
        public string Subject;
        public string Detail;
    }

    public sealed class SavedState {
        public int Schema = 1;
        public bool Enabled = true;
        public bool AutomaticWashing = true;
        public bool AllowForbiddenEquipment = true;
        public ProfileRule Ordinary = ProfileRule.DefaultFor(WearerProfile.Ordinary);
        public ProfileRule Thrifty = ProfileRule.DefaultFor(WearerProfile.Thrifty);
        public ProfileRule Fastidious = ProfileRule.DefaultFor(WearerProfile.Fastidious);
        public List<ManagedBeing> Beings = new List<ManagedBeing>();
        public Dictionary<string, string> TraitFingerprints = new Dictionary<string, string>();
        public Dictionary<string, string> TraitSummaries = new Dictionary<string, string>();
        public string CoreFingerprint;
        public bool CatalogInitialized;
        public string PatchFingerprint;
        public string PatchSummary;
        public List<DiagnosticEntry> Log = new List<DiagnosticEntry>();
    }

    public sealed class BeingRow {
        public uint MindId;
        public string Name;
        public string Traits;
        public string Status;
        public WearerProfile Profile;
        public bool Managed;
    }
}
