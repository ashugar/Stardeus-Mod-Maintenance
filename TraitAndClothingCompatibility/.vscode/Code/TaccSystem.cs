using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using AutoUtilitiesModFramework;
using Game.CodeGen;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems;
using Game.Systems.Creatures;
using Game.Systems.Items;
using Game.Systems.Mental;
using Game.Systems.Policies;
using Game.Utils;
using HarmonyLib;
using KL.Utils;
using Newtonsoft.Json;
using UnityEngine;

namespace TraitAndClothingCompatibility {
    public sealed class TaccSystem : GameSystem, ISaveable {
        public const string SysId = "TraitAndClothingCompatibilitySys";
        public const string ConsumerId = "TraitAndClothingCompatibility";
        public const string DisplayName = "Trait and Clothing Compatibility Mod (TACC)";
        public const string ModVersion = "1.1.3";
        public const string RequiredUtilitiesVersion = "1.4.0";
        internal const string HarmonyId = "TraitAndClothingCompatibility.Notifications";
        internal const int LogLimit = 200;
        internal const string Disclaimer = "This log is present for debugging purposes. A mod update may, or may not, be provided based on the contents of the log.";
        private const float ScanSeconds = 10f;
        private const float CatalogSeconds = 60f;
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings {
            TypeNameHandling = TypeNameHandling.None, MaxDepth = 48
        };

        public static TaccSystem Instance { get; private set; }
        public override string Id => SysId;
        internal SavedState State { get; private set; } = new SavedState();
        internal IReadOnlyList<BeingRow> Rows => rows;
        internal double LastPassMs { get; private set; }
        internal double LastCatalogMs { get; private set; }
        internal int LastChanged { get; private set; }
        internal string Failure { get; private set; }
        internal DiagnosticJournal Diagnostics { get; } = new DiagnosticJournal();
        internal bool DiagnosticScanSucceeded { get; private set; }
        internal float TatteredThreshold => Tunables.TatteredQualityThreshold;
        private readonly Dictionary<uint, ManagedBeing> records = new Dictionary<uint, ManagedBeing>();
        private readonly List<BeingRow> rows = new List<BeingRow>();
        private GameState loadedState;
        private PoliciesSys policies;
        private object loadedSaveGameId;
        private bool ready;
        private bool dirty;
        private bool applying;
        private bool disposed;
        private bool invalidSave;
        private string lastRowsSignature;
        private Dictionary<string, string> observedCode;
        private float nextScan;
        private float nextCatalog;
        private ComponentData saveData;
        private Harmony harmony;
        private TaccPanel panel;
        private ITopBarWidgetHandle widget;

        [SelfInit]
        private static void Register() => GameSystems.Register(SysId, () => new TaccSystem());

        protected override void OnInitialize() {
            if (!AutoUtilitiesApi.TryRegisterConsumer(ConsumerId, DisplayName, ModVersion, RequiredUtilitiesVersion)) return;
            Instance = this;
            harmony = new Harmony(HarmonyId);
            harmony.PatchAll(typeof(TaccSystem).Assembly);
            panel = new TaccPanel(this);
            widget = AutoUtilitiesApi.RegisterTopBarWidget(new TopBarWidgetRegistration {
                Id = ConsumerId + ".Controls", Icon = () => IconId.CClothing,
                Tint = () => Failure != null ? Color.yellow : State.Enabled ? Color.green : Color.gray,
                Tooltip = () => "TACC — " + (State.Enabled ? "Enabled" : "Disabled")
                    + "\n" + rows.Count(r => r.Managed) + " beings managed\nClick for policies, traits, and diagnostic log.",
                OnClick = () => panel.Toggle(), Width = 32f, Order = 320
            });
            S.Sig.AfterLoadState.AddListener(OnAfterLoad);
        }

        private void OnAfterLoad(GameState state) {
            Detach();
            if (!Equals(loadedSaveGameId, state.GameId)) {
                State = new SavedState(); records.Clear(); saveData = null; invalidSave = false; Failure = null;
            }
            loadedState = state;
            if (state.Sys.Known.TryGetValue(PoliciesSys.SysId, out GameSystem found)) policies = found as PoliciesSys;
            if (policies == null) { Failure = "Core equipment policy system is unavailable."; Log("Unavailable", "Equipment policies", Failure); return; }
            state.Clock.OnTick.AddListener(OnTick);
            state.Sig.BeingAdded.AddListener(OnBeingAdded);
            state.Sig.MindInstalled.AddListener(OnMindInstalled);
            state.Sig.EquipmentPolicyChanged.AddListener(OnPolicyChanged);
            panel?.Close();
            state.Clock.OnNextUpdate(() => {
                if (disposed || loadedState != state) return;
                ready = true;
                CheckCatalog(true);
                Reconcile(true);
                D.Log("[TACC] Loaded {0}. Managed {1} beings; policy pass {2:0.00} ms; trait catalog {3:0.00} ms. Core {4}.",
                    ModVersion, rows.Count(r => r.Managed), LastPassMs, LastCatalogMs, Consts.FullVersion);
            });
        }

        private void OnTick(long ticks) {
            if (!ready || disposed) return;
            float now = Time.unscaledTime;
            if (now >= nextCatalog) CheckCatalog(false);
            if ((dirty && now >= nextScan - ScanSeconds + 0.5f) || now >= nextScan) Reconcile(false);
        }

        public void RequestScan() { if (!disposed) dirty = true; }
        private void OnBeingAdded(Being being) => RequestScan();
        private void OnMindInstalled(Mind mind, ObjData part, BeingData being) => RequestScan();
        private void OnPolicyChanged() { if (!applying) RequestScan(); }

        internal ProfileRule Rule(WearerProfile profile) => profile == WearerProfile.Thrifty ? State.Thrifty
            : profile == WearerProfile.Fastidious ? State.Fastidious : State.Ordinary;
        internal bool IsManagedChoice(uint mindId) => records.TryGetValue(mindId, out var record) && !record.OptOut;
        internal bool ManagesEquipment(BeingData being) => being?.Mind != null
            && records.TryGetValue(being.Mind.Id, out ManagedBeing record)
            && State.Enabled && record.OwnsPolicy && !record.OptOut;
        internal bool AllowsForbiddenEquipment(BeingData being) => ManagesEquipment(being)
            && State.AllowForbiddenEquipment;

        internal void ChangeRule(WearerProfile profile, string field, bool value) {
            ProfileRule rule = Rule(profile);
            if (field == "SecondHand") rule.ForbidSecondHand = value;
            else if (field == "DeadMans") rule.ForbidDeadMans = value;
            else rule.AvoidTattered = value;
            Log("Policy setting", ProfileName(profile), field + " = " + value);
            Reconcile(false);
        }

        internal void ResetRules() {
            State.Ordinary = ProfileRule.DefaultFor(WearerProfile.Ordinary);
            State.Thrifty = ProfileRule.DefaultFor(WearerProfile.Thrifty);
            State.Fastidious = ProfileRule.DefaultFor(WearerProfile.Fastidious);
            Log("Policy setting", "Defaults restored", "Restored the three default clothing profiles.");
            Reconcile(false);
        }

        internal void SetEnabled(bool value) {
            if (invalidSave) return;
            State.Enabled = value;
            Log("Automation", value ? "Enabled" : "Disabled", value
                ? "Automatic profiles enabled; individual opt-outs remain in place."
                : "Restoring unchanged TACC-owned policies to their prior settings.");
            Reconcile(false);
        }

        internal void SetAutomaticWashing(bool value) {
            State.AutomaticWashing = value;
            Log("Laundry setting", "Automatic washing", value
                ? "Stored corpse-origin clothing may be queued for an available Washing Machine."
                : "No new automatic washing orders will be created; manual Wash orders remain available.");
            WashingMachineSys.RequestStoredItemSearch(loadedState);
            RefreshUi();
        }

        internal void SetAllowForbiddenEquipment(bool value) {
            State.AllowForbiddenEquipment = value;
            Log("Policy setting", "Forbidden loose equipment", value
                ? "TACC-managed colonists may select compatible loose equipment carrying the red forbidden marker. The marker and ordinary hauling restriction remain unchanged."
                : "Core's normal forbidden-item exclusion applies to equipment searches.");
            RefreshUi();
        }

        internal void SetManaged(uint mindId, bool value) {
            if (!records.TryGetValue(mindId, out ManagedBeing record)) return;
            record.OptOut = !value;
            record.AutomaticPause = false;
            Log("Being policy", record.Name, value ? "Automatic management enabled." : "Opted out; restoring the prior policy where TACC still owns it.");
            Reconcile(false);
        }

        internal void Recheck() { CheckCatalog(true); Reconcile(false); }

        private static bool HasCurrentMind(BeingData b) => b?.Mind != null && b.Mind.Id != 0
            && (b.Mind.Owner == null || ReferenceEquals(b.Mind.Owner, b));

        private static bool Eligible(BeingData b) => HasCurrentMind(b) && b.Species?.IsHumanoid == true
            && b.Inventory != null && b.EffectiveFaction?.IsPlayer == true && !b.IsGuest && !b.HasStateFlag(BeingState.Dead);

        private void Reconcile(bool initial) {
            if (!ready || policies == null || applying || disposed || invalidSave) { RefreshUi(); return; }
            var timer = Stopwatch.StartNew();
            applying = true;
            dirty = false;
            nextScan = Time.unscaledTime + ScanSeconds;
            bool changed = false;
            LastChanged = 0;
            rows.Clear();
            try {
                foreach (var being in loadedState.Query.AllBeingData.Ask().Values) {
                    if (!HasCurrentMind(being)) continue;
                    records.TryGetValue(being.Mind.Id, out ManagedBeing record);
                    if (!Eligible(being)) {
                        if (record != null && record.OwnsPolicy) changed |= Restore(being, record);
                        continue;
                    }
                    if (record == null) {
                        record = new ManagedBeing { MindId = being.Mind.Id };
                        records.Add(record.MindId, record);
                        if (!initial) Log("Colonist found", NameOf(being), "Evaluating clothing policy from current traits.");
                    }
                    record.BeingId = being.Id;
                    record.Name = NameOf(being);
                    try {
                        bool result = ReconcileBeing(being, record, initial);
                        changed |= result;
                        if (result) LastChanged++;
                    } catch (Exception e) {
                        string message = "Policy check failed: " + e.Message;
                        if (record.Status != message) {
                            Log("Error", record.Name, message);
                            D.LogEx(e, "[TACC] Policy check failed for mind {0}", record.MindId);
                        }
                        record.Status = message;
                    }
                    rows.Add(new BeingRow {
                        MindId = record.MindId, Name = record.Name, Profile = record.Profile,
                        Traits = TraitNames(being), Status = record.Status,
                        Managed = State.Enabled && !record.OptOut && record.OwnsPolicy
                    });
                }
                rows.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            } catch (Exception e) {
                Failure = "Policy reconciliation failed: " + e.Message;
                Log("Error", "Policy reconciliation", Failure);
                D.LogEx(e, "[TACC] Policy reconciliation failed");
            } finally {
                if (changed) {
                    try { policies.CommitChange(); }
                    catch (Exception e) { Log("Error", "Core policy notification", e.Message); }
                }
                applying = false;
                timer.Stop();
                LastPassMs = timer.Elapsed.TotalMilliseconds;
                string signature = string.Join("|", rows.Select(r => r.MindId + ":" + r.Name + ":" + r.Status + ":" + r.Traits + ":" + r.Managed));
                if (initial || signature != lastRowsSignature) panel?.Refresh();
                lastRowsSignature = signature;
                widget?.Refresh();
            }
        }

        private bool ReconcileBeing(BeingData being, ManagedBeing record, bool initial) {
            string signature = TraitSignature(being);
            if (record.TraitSignature != null && signature != record.TraitSignature) {
                Log("Being traits changed", record.Name, "Previous: " + record.TraitSignature + ". Current: " + signature + ".");
            }
            record.TraitSignature = signature;
            record.Profile = PolicyEngine.Profile(ItemOwnership.CaresAboutOrigin(being),
                being.HasTrait(TraitIdH.Thrifty), being.HasTrait(TraitIdH.Fastidious));

            PolicyState current = CoreAdapter.Capture(being.Mind.EquipmentPolicy);
            if (PolicyEngine.WasManuallyEdited(record.OwnsPolicy, current, record.LastApplied)) {
                record.OptOut = true;
                record.AutomaticPause = true;
                ReleaseRecord(record);
                Log("Manual override", record.Name, "An individual policy was edited outside TACC. Automatic management paused; the edited policy is retained.");
            }
            if (!State.Enabled || record.OptOut || record.Profile == WearerProfile.NoMentalHealth) {
                bool restored = Restore(being, record);
                record.Status = !State.Enabled ? "Disabled" : record.OptOut
                    ? record.AutomaticPause ? "Paused after manual edit" : "Opted out"
                    : "No mood restrictions";
                return restored;
            }
            if (!record.OwnsPolicy) {
                record.OriginalPolicy = current?.Clone();
            }
            var baselines = CoreAdapter.Baselines(policies, being, record.OriginalPolicy);
            var plan = PolicyEngine.Build(record.OriginalPolicy, baselines, record.Profile,
                Rule(record.Profile), TatteredThreshold);
            string conflicts = string.Join(", ", plan.Conflicts);
            if (conflicts.Length != 0 && conflicts != record.ConflictSignature)
                Log("Quality conflict", record.Name, "Existing maximum quality is too low to exclude tatters in: " + conflicts
                    + ". That quality range was preserved; adjust it in the Core equipment policy.");
            record.ConflictSignature = conflicts;
            if (PolicyState.Same(current, plan.Policy)) {
                record.LastApplied = plan.Policy?.Clone();
                record.OwnsPolicy = true;
                record.Status = StatusFor(baselines, conflicts);
                return false;
            }
            policies.SetIndividualEquipment(being, CoreAdapter.Materialize(plan.Policy));
            record.LastApplied = plan.Policy?.Clone();
            record.OwnsPolicy = true;
            record.Status = StatusFor(baselines, conflicts);
            return true;
        }

        private static string StatusFor(List<SlotPolicy> baselines, string conflicts) {
            if (conflicts.Length != 0) return "Quality conflict: " + conflicts;
            int manual = baselines.Count(s => s.Mode == 0);
            return baselines.Count == 0 ? "No worn slots" : manual == baselines.Count ? "Manual slots retained"
                : manual > 0 ? "Managed; " + manual + " manual slot(s)" : "Managed";
        }

        private bool Restore(BeingData being, ManagedBeing record) {
            if (!record.OwnsPolicy) return false;
            bool changed = false;
            PolicyState current = CoreAdapter.Capture(being.Mind.EquipmentPolicy);
            if (PolicyEngine.CanRestore(current, record.LastApplied)) {
                if (!PolicyState.Same(current, record.OriginalPolicy)) {
                    policies.SetIndividualEquipment(being, CoreAdapter.Materialize(record.OriginalPolicy));
                    changed = true;
                }
            } else {
                Log("Manual override", record.Name, "Restoration skipped because the current policy was edited outside TACC.");
            }
            ReleaseRecord(record);
            return changed;
        }

        private static void ReleaseRecord(ManagedBeing record) {
            record.OwnsPolicy = false;
            record.LastApplied = null;
            record.OriginalPolicy = null;
        }

        private void CheckCatalog(bool checkBuild) {
            nextCatalog = Time.unscaledTime + CatalogSeconds;
            if (loadedState == null || invalidSave) return;
            var timer = Stopwatch.StartNew();
            try {
                var snapshots = TraitCatalog.Snapshot();
                var equipment = TraitCatalog.EquipmentSnapshot();
                string patches = TraitCatalog.PatchSnapshot();
                if (checkBuild || observedCode == null) observedCode = TraitCatalog.CodeSnapshot();
                var findings = TraitCatalog.FindUnvalidated(TraitCatalog.ReferenceSnapshot(), snapshots,
                    TraitCatalog.ReferenceEquipment(), equipment, TraitCatalog.ReferenceCode(), observedCode, patches);
                foreach (var entry in Diagnostics.Reconcile(findings, State.Log,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), LogLimit))
                    D.Log("[TACC] {0} — {1}: {2}", entry.Kind, entry.Subject, entry.Detail);
                // Old-save observations remain inspectable, but never approve a change.
                State.TraitFingerprints = snapshots.ToDictionary(p => p.Key, p => TraitCatalog.Hash(p.Value), StringComparer.Ordinal);
                State.TraitSummaries = snapshots;
                State.CatalogInitialized = true;
                State.PatchFingerprint = TraitCatalog.Hash(patches);
                State.PatchSummary = patches;
                State.CoreFingerprint = observedCode["Game.dll"];
                DiagnosticScanSucceeded = true;
                RefreshUi();
            } catch (Exception e) {
                DiagnosticScanSucceeded = false;
                Log("Diagnostic error", "Trait catalog", e.Message);
                D.LogEx(e, "[TACC] Trait catalog scan failed");
            } finally {
                timer.Stop();
                LastCatalogMs = timer.Elapsed.TotalMilliseconds;
            }
        }

        private static string NameOf(BeingData b) => b.Mind?.Name?.FullNameOrNameless ?? "Being " + b.Id;
        private static string TraitSignature(BeingData b) => b.Mind.Traits == null ? "" : string.Join(", ", b.Mind.Traits.Slots
            .Where(t => t?.Trait != null).Select(t => t.Trait.Id).OrderBy(id => id, StringComparer.Ordinal));
        private static string TraitNames(BeingData b) => b.Mind.Traits == null ? "" : string.Join(", ", b.Mind.Traits.Slots
            .Where(t => t?.Trait != null).Select(t => t.Trait.NameT ?? t.Trait.Id));
        internal static string ProfileName(WearerProfile profile) => profile == WearerProfile.Ordinary ? "Other traits"
            : profile == WearerProfile.NoMentalHealth ? "No mental health" : profile.ToString();

        private void Log(string kind, string subject, string detail) {
            if (detail?.Length > 2400) detail = detail.Substring(0, 2400) + "…";
            State.Log.Add(new DiagnosticEntry {
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Kind = kind, Subject = subject, Detail = detail
            });
            while (State.Log.Count > LogLimit) State.Log.RemoveAt(0);
            D.Log("[TACC] {0} — {1}: {2}", kind, subject, detail);
            panel?.Refresh();
        }

        internal void ClearLog() {
            Diagnostics.Clear(State.Log);
            CheckCatalog(true);
            RefreshUi();
        }
        internal string CopyLogText() => DisplayName + " " + ModVersion + "\nCore " + Consts.FullVersion + "\n" + Disclaimer
            + "\n\nCURRENT UNVALIDATED FINDINGS: " + Diagnostics.Active.Count
            + (DiagnosticScanSucceeded ? "" : "\nLatest compatibility scan has not completed successfully.")
            + "\n" + string.Join("\n\n", Diagnostics.Active.OrderBy(e => e.Subject, StringComparer.Ordinal).Select(EntryText))
            + "\n\nHISTORY\n" + string.Join("\n\n", Diagnostics.History(State.Log).Select(EntryText));
        private static string EntryText(DiagnosticEntry e) => e.Time + " | " + e.Kind + " | " + e.Subject + "\n" + e.Detail;
        private void RefreshUi() { widget?.Refresh(); panel?.Refresh(); }

        public ComponentData Save() {
            if (invalidSave && saveData != null) return saveData;
            saveData ??= new ComponentData(0, SysId);
            State.Beings = records.Values.OrderBy(r => r.MindId).ToList();
            saveData.SetInt("SaveSchemaVersion", 1);
            saveData.SetString("TaccState", JsonConvert.SerializeObject(State, Formatting.None, JsonSettings));
            return saveData;
        }

        public void Load(ComponentData data) {
            saveData = data;
            loadedSaveGameId = S.GameId;
            records.Clear();
            try {
                if (data.GetInt("SaveSchemaVersion", 1) > 1) throw new InvalidOperationException("This save uses a newer TACC schema.");
                State = JsonConvert.DeserializeObject<SavedState>(data.GetString("TaccState", "{}"), JsonSettings) ?? new SavedState();
                State.Ordinary ??= ProfileRule.DefaultFor(WearerProfile.Ordinary);
                State.Thrifty ??= ProfileRule.DefaultFor(WearerProfile.Thrifty);
                State.Fastidious ??= ProfileRule.DefaultFor(WearerProfile.Fastidious);
                State.Beings ??= new List<ManagedBeing>();
                State.Log ??= new List<DiagnosticEntry>();
                State.TraitFingerprints ??= new Dictionary<string, string>();
                State.TraitSummaries ??= new Dictionary<string, string>();
                foreach (ManagedBeing record in State.Beings) if (record != null && record.MindId != 0) records[record.MindId] = record;
                while (State.Log.Count > LogLimit) State.Log.RemoveAt(0);
            } catch (Exception e) {
                invalidSave = true;
                State = new SavedState { Enabled = false };
                Failure = "TACC settings could not be read. Existing game policies were left in place. " + e.Message;
                Log("Load error", "Saved settings", Failure);
            }
        }

        private void Detach() {
            ready = false;
            Diagnostics.Reset();
            observedCode = null;
            DiagnosticScanSucceeded = false;
            if (loadedState == null) return;
            loadedState.Clock.OnTick.RemoveListener(OnTick);
            loadedState.Sig.BeingAdded.RemoveListener(OnBeingAdded);
            loadedState.Sig.MindInstalled.RemoveListener(OnMindInstalled);
            loadedState.Sig.EquipmentPolicyChanged.RemoveListener(OnPolicyChanged);
            loadedState = null;
            policies = null;
        }

        public override void Unload() {
            disposed = true;
            S?.Sig.AfterLoadState.RemoveListener(OnAfterLoad);
            Detach();
            panel?.Dispose(); panel = null;
            widget?.Dispose(); widget = null;
            harmony?.UnpatchAll(HarmonyId); harmony = null;
            if (ReferenceEquals(Instance, this)) Instance = null;
            rows.Clear(); records.Clear();
        }
    }
}
