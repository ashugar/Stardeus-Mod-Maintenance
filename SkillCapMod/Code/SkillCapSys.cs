using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Game;
using Game.CodeGen;
using Game.Components;
using Game.Data;
using Game.Systems;
using Game.Systems.Creatures;
using Game.Systems.Mental;
using Game.Utils;
using HarmonyLib;
using KL.Utils;
using Newtonsoft.Json;

namespace SkillCapMod;

/// <summary>
/// The mod's Core <see cref="GameSystem"/>. It owns Harmony's lifetime and implements
/// <see cref="ISaveable"/> so exact levels travel with a save even though Core's native
/// <c>JobSlotData.Level</c> remains a byte.
/// </summary>
public sealed class SkillCapSys : GameSystem, ISaveable
{
    public const string SysId = "SkillCapModSys";
    private const string RequiredHarmonyId = "jleas.stardeus.skillcapmod";
    private const int SaveSchema = 1;

    // Required progression patches and the guarded Job Matrix integration use different owners.
    // An optional UI mismatch can therefore fail closed without undoing or aborting core behavior.
    private Harmony requiredHarmony;
    private Harmony optionalHarmony;

    // ISaveable.Load may run before Core has reconstructed every Mind and JobSlot. Deserialized
    // records wait here until AfterLoadSaveState confirms that those Core objects exist.
    private SkillCapSave pending = new();

    // Core requires the instance Id to match the key supplied to GameSystems.Register.
    public override string Id => SysId;

    // Skills exist in sandbox games too, so Core should not deactivate this system there.
    public override bool SkipInSandbox => false;

    /// <summary>
    /// Called by Core's generated self-initialization pass. The factory lets Core create one
    /// SkillCapSys for each GameRuntime and include it in normal initialize/save/load/unload flows.
    /// </summary>
    [SelfInit]
    private static void Register() => GameSystems.Register(SysId, () => new SkillCapSys());

    /// <summary>
    /// Runs when Core initializes this GameSystem. It publishes the active bridge for settings
    /// changes, applies this assembly's required Harmony patches, and only then attempts the
    /// separately owned, fail-closed Job Matrix tooltip integration. Only a successfully installed
    /// optional owner is retained for teardown; a rejected candidate is cleaned up and discarded.
    /// Core's post-load signal is registered after patch setup so saved Mind/Job identifiers resolve
    /// through required logic.
    /// </summary>
    protected override void OnInitialize()
    {
        SkillCapRuntime.ActiveSystem = this;
        requiredHarmony = new Harmony(RequiredHarmonyId);
        requiredHarmony.PatchAll(typeof(SkillCapSys).Assembly);
        var optionalCandidate = new Harmony(SkillCapOptionalPatches.HarmonyId);
        if (SkillCapOptionalPatches.TryInstall(optionalCandidate))
        {
            optionalHarmony = optionalCandidate;
        }
        else
        {
            // TryInstall already cleans up on failure. Repeating that cleanup here makes the
            // ownership handoff explicit while keeping any Harmony cleanup exception optional.
            try
            {
                optionalCandidate.UnpatchAll(SkillCapOptionalPatches.HarmonyId);
            }
            catch
            {
                // Required patches remain active under their independent owner.
            }
            optionalHarmony = null;
        }
        The.SysSig.AfterLoadSaveState.AddListener(OnLoaded);
        D.Warn("Skill Cap Mod {0} initialized. Maximum level: {1}.",
            typeof(SkillCapSys).Assembly.GetName().Version.ToString(3),
            SkillCapMath.Format(SkillCapSettings.MaximumLevel));
    }

    /// <summary>
    /// Handles Core's AfterLoadSaveState signal for both new and loaded games. Exact sidecar values
    /// are restored first; the current global cap is then applied consistently to every slot.
    /// </summary>
    private void OnLoaded(GameState state)
    {
        ApplyPending(state);
        ApplyMaximumToAll();
    }

    /// <summary>
    /// Walks Core's <see cref="BeingsSys"/> registry and asks the runtime bridge to normalize every
    /// loaded job slot. This is used after loading and when the player lowers the global cap.
    /// Missing systems are treated as a normal main-menu/teardown state.
    /// </summary>
    internal void ApplyMaximumToAll()
    {
        if (S?.Sys?.Known == null
            || !S.Sys.Known.TryGetValue(BeingsSys.SysId, out GameSystem system)
            || system is not BeingsSys beings) return;

        foreach (Mind mind in beings.Minds.Values)
        {
            if (mind?.Jobs == null) continue;
            foreach (JobSlot slot in mind.Jobs.JobSlots.Values)
            {
                SkillCapRuntime.ApplyMaximum(slot);
            }
        }
    }

    /// <summary>
    /// Serializes exact levels and progress into this GameSystem's Core <see cref="ComponentData"/>.
    /// Core continues saving its ordinary projected JobSlotData as well, providing a vanilla-safe
    /// fallback if this mod's component is missing or cannot be read later.
    /// </summary>
    public ComponentData Save()
    {
        var save = new SkillCapSave { Schema = SaveSchema };
        if (S?.Sys?.Known != null
            && S.Sys.Known.TryGetValue(BeingsSys.SysId, out GameSystem system)
            && system is BeingsSys beings)
        {
            foreach (Mind mind in beings.Minds.Values)
            {
                if (mind?.Jobs == null) continue;
                foreach (JobSlot slot in mind.Jobs.JobSlots.Values)
                {
                    ExtendedSkillState state = SkillCapRuntime.State(slot);
                    save.Skills.Add(new SavedSkill
                    {
                        MindId = mind.Id,
                        JobId = slot.Job.Id,
                        Level = state.Level.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Progress = state.Progress
                    });
                }
            }
        }

        ComponentData data = ComponentData.Create(SysId);
        data.SetString("State", JsonConvert.SerializeObject(save));
        return data;
    }

    /// <summary>
    /// Parses the mod-owned ComponentData defensively but does not touch Core objects yet. Core may
    /// still be rebuilding BeingsSys during ISaveable.Load, so validated records remain pending for
    /// the AfterLoadSaveState callback. Unknown schemas fail back to Core's projected levels.
    /// </summary>
    public void Load(ComponentData data)
    {
        pending = new SkillCapSave();
        if (data == null) return;
        try
        {
            string json = data.GetString("State");
            if (string.IsNullOrWhiteSpace(json)) return;
            pending = JsonConvert.DeserializeObject<SkillCapSave>(json,
                new JsonSerializerSettings
                {
                    TypeNameHandling = TypeNameHandling.None,
                    MaxDepth = 16
                }) ?? new SkillCapSave();
            if (pending.Schema != SaveSchema)
                throw new InvalidOperationException("Unsupported Skill Cap save schema.");
            pending.Skills ??= new List<SavedSkill>();
        }
        catch (Exception ex)
        {
            pending = new SkillCapSave();
            D.LogEx(ex, "Skill Cap Mod saved levels could not be read; Core levels will be used.");
        }
    }

    /// <summary>
    /// Resolves pending records against Core's stable Mind IDs and JobType string IDs after loading.
    /// Invalid or missing beings/jobs are skipped so a removed creature, renamed definition, or bad
    /// record cannot prevent the rest of the save from loading.
    /// </summary>
    private void ApplyPending(GameState state)
    {
        if (pending?.Skills == null || pending.Skills.Count == 0) return;
        if (!state.Sys.Known.TryGetValue(BeingsSys.SysId, out GameSystem system)
            || system is not BeingsSys beings) return;

        foreach (SavedSkill saved in pending.Skills)
        {
            if (saved == null || string.IsNullOrEmpty(saved.JobId)
                || !BigInteger.TryParse(saved.Level, out BigInteger level)
                || level < BigInteger.Zero || double.IsNaN(saved.Progress)
                || !beings.Minds.TryGetValue(saved.MindId, out Mind mind)
                || mind?.Jobs == null
                || !mind.Jobs.TryJob(Hashes.S(saved.JobId), out JobSlot slot)) continue;
            SkillCapRuntime.Set(slot, level, saved.Progress);
        }
        pending.Skills.Clear();
    }

    /// <summary>
    /// Mirrors Core's GameSystem teardown contract: remove listeners attached to the global SysSig,
    /// unpatch the optional owner before the required owner, then clear runtime sidecars before the
    /// next game. Independent owners keep an optional UI failure isolated throughout teardown too.
    /// </summary>
    public override void Unload()
    {
        The.SysSig.AfterLoadSaveState.RemoveListener(OnLoaded);
        optionalHarmony?.UnpatchAll(SkillCapOptionalPatches.HarmonyId);
        optionalHarmony = null;
        requiredHarmony?.UnpatchAll(RequiredHarmonyId);
        requiredHarmony = null;
        SkillCapRuntime.Reset();
    }
}

/// <summary>
/// Versioned JSON envelope stored inside Core's ComponentData for this GameSystem.
/// </summary>
internal sealed class SkillCapSave
{
    public int Schema = 1;
    public List<SavedSkill> Skills = new();
}

/// <summary>
/// Identifies one Core JobSlot without holding runtime references. MindId is Core's stable being-
/// mind key, JobId is the definition string, Level is a decimal BigInteger string, and Progress is
/// the mod's higher-precision counterpart to Core's float LevelProgress.
/// </summary>
internal sealed class SavedSkill
{
    public uint MindId;
    public string JobId;
    public string Level;
    public double Progress;
}
