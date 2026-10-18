using ColonistAndBotsAutoUpgrade.Models;
using MessagePack;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ColonistAndBotsAutoUpgrade.Models
{
    public enum CARAURetryPolicy
    {
        Manual = 0,
        TransientOnly = 1,
        TransientAndSurgical = 2
    }

    public static class CARAUSettings
    {
        public const int DefaultQualityThresholdPercent = 80;
        public const int DefaultAutomaticRetryLimit = 3;
        public const int DefaultAutomaticRetryChanceThresholdPercent = 90;
        public const int MinimumAutomaticRetryLimit = 0;
        public const int MaximumAutomaticRetryLimit = 10;

        public static bool ShowDisabledSlots { get; set; }

        public static readonly HashSet<int> DisabledSlotIds = new();

        public static int QualityThresholdPercent { get; set; } =
            DefaultQualityThresholdPercent;

        public static float QualityThreshold => QualityThresholdPercent / 100f;

        public static bool AllowTierUpgrades { get; set; } = true;

        public static bool AllowQualityUpgrades { get; set; } = true;

        public static CARAURetryPolicy RetryPolicy { get; set; } =
            CARAURetryPolicy.Manual;

        public static int AutomaticRetryLimit { get; set; } =
            DefaultAutomaticRetryLimit;

        public static int AutomaticRetryChanceThresholdPercent { get; set; } =
            DefaultAutomaticRetryChanceThresholdPercent;

        // Opt-in because this authorizes a new preventative surgery rather
        // than merely retrying an operation the player already requested.
        public static bool AutoInstallMoodRegulatorAfterMentalBreakdown
          { get; set; }

        // Opt-in because this authorizes a new preventative surgery in
        // response to Core's active Hacked condition.
        public static bool AutoInstallFirewallChipWhenHacked { get; set; }

        public static float AutomaticRetryChanceThreshold =>
            AutomaticRetryChanceThresholdPercent / 100f;

        public static void ResetSaveSpecificDefaults()
        {
            ShowDisabledSlots = false;
            DisabledSlotIds.Clear();
            QualityThresholdPercent = DefaultQualityThresholdPercent;
            AllowTierUpgrades = true;
            AllowQualityUpgrades = true;
            RetryPolicy = CARAURetryPolicy.Manual;
            AutomaticRetryLimit = DefaultAutomaticRetryLimit;
            AutomaticRetryChanceThresholdPercent =
                DefaultAutomaticRetryChanceThresholdPercent;
            AutoInstallMoodRegulatorAfterMentalBreakdown = false;
            AutoInstallFirewallChipWhenHacked = false;
        }

        public static CARAUSettingsData CreateSaveData()
        {
            return new CARAUSettingsData
            {
                SchemaVersion = CARAUSettingsData.CurrentSchemaVersion,
                QualityThresholdPercent = ClampQualityThreshold(
                    QualityThresholdPercent
                ),
                ShowDisabledSlots = ShowDisabledSlots,
                DisabledSlotIds = DisabledSlotIds
                    .OrderBy(slotId => slotId)
                    .ToList(),
                RetryPolicy = IsKnownRetryPolicy(RetryPolicy)
                    ? RetryPolicy
                    : CARAURetryPolicy.Manual,
                AutomaticRetryLimit = ClampAutomaticRetryLimit(
                    AutomaticRetryLimit
                ),
                AutomaticRetryChanceThresholdPercent =
                    ClampPercentage(
                        AutomaticRetryChanceThresholdPercent
                    ),
                AutoInstallMoodRegulatorAfterMentalBreakdown =
                    AutoInstallMoodRegulatorAfterMentalBreakdown,
                AutoInstallFirewallChipWhenHacked =
                    AutoInstallFirewallChipWhenHacked,
                AllowTierUpgrades = AllowTierUpgrades,
                AllowQualityUpgrades = AllowQualityUpgrades
            };
        }

        public static void ApplySaveData(CARAUSettingsData data)
        {
            ResetSaveSpecificDefaults();
            if (data == null)
            {
                return;
            }

            QualityThresholdPercent = ClampQualityThreshold(
                data.QualityThresholdPercent
            );
            ShowDisabledSlots = data.ShowDisabledSlots;

            if (data.DisabledSlotIds != null)
            {
                DisabledSlotIds.UnionWith(data.DisabledSlotIds);
            }

            // Zero is a valid schema-2 value for both controls. Missing fields
            // from schema 1 also deserialize as zero, so schema-aware migration
            // is required to preserve the accepted defaults.
            if (data.SchemaVersion < 2)
            {
                return;
            }

            RetryPolicy = IsKnownRetryPolicy(data.RetryPolicy)
                ? data.RetryPolicy
                : CARAURetryPolicy.Manual;
            AutomaticRetryLimit = ClampAutomaticRetryLimit(
                data.AutomaticRetryLimit
            );
            AutomaticRetryChanceThresholdPercent = ClampPercentage(
                data.AutomaticRetryChanceThresholdPercent
            );

            if (data.SchemaVersion < 3)
            {
                return;
            }

            AutoInstallMoodRegulatorAfterMentalBreakdown =
                data.AutoInstallMoodRegulatorAfterMentalBreakdown;

            // Missing bool fields deserialize as false. Existing saves must
            // retain CARAU's pre-schema-4 behavior with both upgrade paths
            // enabled unless the player explicitly changes them.
            if (data.SchemaVersion < 4)
            {
                return;
            }

            AllowTierUpgrades = data.AllowTierUpgrades;
            AllowQualityUpgrades = data.AllowQualityUpgrades;

            if (data.SchemaVersion < 5)
            {
                return;
            }

            AutoInstallFirewallChipWhenHacked =
                data.AutoInstallFirewallChipWhenHacked;
        }

        public static void DeselectAllSlots(IEnumerable<int> slotIds)
        {
            foreach (var slotId in slotIds)
            {
                DisabledSlotIds.Add(slotId);
            }
        }

        private static int ClampQualityThreshold(int value)
        {
            return ClampPercentage(value);
        }

        public static int ClampAutomaticRetryLimit(int value)
        {
            return Math.Max(
                MinimumAutomaticRetryLimit,
                Math.Min(MaximumAutomaticRetryLimit, value)
            );
        }

        public static int ClampPercentage(int value)
        {
            return Math.Max(0, Math.Min(100, value));
        }

        public static bool IsKnownRetryPolicy(CARAURetryPolicy policy)
        {
            return policy == CARAURetryPolicy.Manual ||
                   policy == CARAURetryPolicy.TransientOnly ||
                   policy == CARAURetryPolicy.TransientAndSurgical;
        }

        public static bool IsSlotEnabled(int slotTypeId)
        {
            return !DisabledSlotIds.Contains(slotTypeId);
        }

        public static void SetSlotEnabled(int slotTypeId, bool enabled)
        {
            if (enabled)
            {
                DisabledSlotIds.Remove(slotTypeId);
                return;
            }

            DisabledSlotIds.Add(slotTypeId);
        }

        public static void SelectAllSlots()
        {
            DisabledSlotIds.Clear();
        }

        public static readonly HashSet<CARAUDebugCategory> EnabledDebugCategories = new()
        {
          CARAUDebugCategory.Error,
          CARAUDebugCategory.Warning
        };

        public static bool IsDebugCategoryEnabled(CARAUDebugCategory category)
        {
            return EnabledDebugCategories.Contains(category);
        }

        public static void SetDebugCategoryEnabled(CARAUDebugCategory category, bool enabled)
        {
            if (enabled)
            {
                EnabledDebugCategories.Add(category);
                return;
            }

            EnabledDebugCategories.Remove(category);
        }
    }

    [MessagePackObject]
    public sealed class CARAUSettingsData
    {
        public const int CurrentSchemaVersion = 5;

        [Key(0)]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [Key(1)]
        public int QualityThresholdPercent { get; set; } =
            CARAUSettings.DefaultQualityThresholdPercent;

        [Key(2)]
        public bool ShowDisabledSlots { get; set; }

        [Key(3)]
        public List<int> DisabledSlotIds { get; set; } = new();

        [Key(4)]
        public CARAURetryPolicy RetryPolicy { get; set; } =
            CARAURetryPolicy.Manual;

        [Key(5)]
        public int AutomaticRetryLimit { get; set; } =
            CARAUSettings.DefaultAutomaticRetryLimit;

        [Key(6)]
        public int AutomaticRetryChanceThresholdPercent { get; set; } =
            CARAUSettings.DefaultAutomaticRetryChanceThresholdPercent;

        [Key(7)]
        public bool AutoInstallMoodRegulatorAfterMentalBreakdown
          { get; set; }

        [Key(8)]
        public bool AllowTierUpgrades { get; set; } = true;

        [Key(9)]
        public bool AllowQualityUpgrades { get; set; } = true;

        [Key(10)]
        public bool AutoInstallFirewallChipWhenHacked { get; set; }
    }
}
