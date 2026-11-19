using ColonistAndBotsAutoUpgrade.Models;
using MessagePack;
using System.Reflection;

CARAUSettings.ResetSaveSpecificDefaults();
Check.Equal(false, CARAUSettings.AllowEnemyConversion, "default master");
Check.Equal(true, CARAUSettings.AllowEnemyConversionObeyChip, "default Obey");
Check.Equal(false, CARAUSettings.AllowEnemyConversionCoreSwap, "default Core");
Check.Equal(
    EnemyConversionPriority.ObeyChipThenCoreSwap,
    CARAUSettings.ConversionPriority,
    "default order"
);

CARAUSettings.SetEnemyConversionEnabled(true);
CARAUSettings.SetEnemyConversionEnabled(false);
CARAUSettings.SetEnemyConversionEnabled(true);
Check.ConversionState(
    true,
    true,
    false,
    "master re-enable preserves Obey-only"
);

CARAUSettings.SetEnemyConversionCoreSwapEnabled(true);
CARAUSettings.SetEnemyConversionObeyChipEnabled(false);
Check.ConversionState(
    true,
    false,
    true,
    "one enabled route keeps master enabled"
);

CARAUSettings.SetEnemyConversionEnabled(false);
CARAUSettings.SetEnemyConversionEnabled(true);
Check.ConversionState(
    true,
    false,
    true,
    "master re-enable preserves Core-only"
);

CARAUSettings.SetEnemyConversionCoreSwapEnabled(false);
Check.ConversionState(
    false,
    false,
    false,
    "last route disables master"
);

CARAUSettings.SetEnemyConversionEnabled(true);
Check.ConversionState(
    true,
    true,
    false,
    "explicit master re-enable restores Obey only"
);

CARAUSettings.ResetSaveSpecificDefaults();
CARAUSettings.SetEnemyConversionEnabled(true);
CARAUSettings.SetEnemyConversionObeyChipEnabled(false);
Check.ConversionState(
    false,
    false,
    false,
    "last Obey route disables master"
);

CARAUSettings.ResetSaveSpecificDefaults();
CARAUSettings.SetEnemyConversionObeyChipEnabled(false);
CARAUSettings.SetEnemyConversionCoreSwapEnabled(true);
Check.ConversionState(
    false,
    false,
    true,
    "route enable does not enable master"
);

for (var legacySchema = 0; legacySchema <= 5; legacySchema++)
{
    CARAUSettings.ApplySaveData(new CARAUSettingsData
    {
        SchemaVersion = legacySchema,
        QualityThresholdPercent = 80,
        AllowTierUpgrades = true,
        AllowQualityUpgrades = true
    });
    Check.Equal(
        false,
        CARAUSettings.AllowEnemyConversion,
        $"schema-{legacySchema} master"
    );
    Check.Equal(
        true,
        CARAUSettings.AllowEnemyConversionObeyChip,
        $"schema-{legacySchema} Obey"
    );
    Check.Equal(
        false,
        CARAUSettings.AllowEnemyConversionCoreSwap,
        $"schema-{legacySchema} Core"
    );
    Check.Equal(
        EnemyConversionPriority.ObeyChipThenCoreSwap,
        CARAUSettings.ConversionPriority,
        $"schema-{legacySchema} order"
    );
}

CARAUSettings.ApplySaveData(new CARAUSettingsData
{
    SchemaVersion = 6,
    QualityThresholdPercent = 80,
    AllowTierUpgrades = true,
    AllowQualityUpgrades = true,
    AllowEnemyConversion = true,
    AllowEnemyConversionObeyChip = false,
    AllowEnemyConversionCoreSwap = false
});
Check.ConversionState(
    false,
    false,
    false,
    "schema-6 no-route normalization"
);

CARAUSettings.AllowEnemyConversion = true;
var inconsistentSaved = CARAUSettings.CreateSaveData();
Check.ConversionData(
    inconsistentSaved,
    false,
    false,
    false,
    "saved master requires an enabled route"
);

CARAUSettings.ApplySaveData(new CARAUSettingsData
{
    SchemaVersion = 6,
    QualityThresholdPercent = 80,
    AllowTierUpgrades = true,
    AllowQualityUpgrades = true,
    AllowEnemyConversion = true,
    AllowEnemyConversionObeyChip = false,
    AllowEnemyConversionCoreSwap = true,
    ConversionPriority = EnemyConversionPriority.CoreSwapThenObeyChip
});
Check.Equal(true, CARAUSettings.AllowEnemyConversion, "schema-6 master");
Check.Equal(false, CARAUSettings.AllowEnemyConversionObeyChip, "schema-6 Obey");
Check.Equal(true, CARAUSettings.AllowEnemyConversionCoreSwap, "schema-6 Core");
Check.Equal(
    EnemyConversionPriority.CoreSwapThenObeyChip,
    CARAUSettings.ConversionPriority,
    "schema-6 order"
);

var saved = CARAUSettings.CreateSaveData();
Check.Equal(6, saved.SchemaVersion, "saved schema");
Check.Equal(true, saved.AllowEnemyConversion, "saved master");
Check.Equal(false, saved.AllowEnemyConversionObeyChip, "saved Obey");
Check.Equal(true, saved.AllowEnemyConversionCoreSwap, "saved Core");

var expectedKeys = new Dictionary<string, int>
{
    [nameof(CARAUSettingsData.AllowEnemyConversion)] = 11,
    [nameof(CARAUSettingsData.AllowEnemyConversionObeyChip)] = 12,
    [nameof(CARAUSettingsData.AllowEnemyConversionCoreSwap)] = 13,
    [nameof(CARAUSettingsData.ConversionPriority)] = 14
};
foreach (var pair in expectedKeys)
{
    var property = typeof(CARAUSettingsData).GetProperty(pair.Key);
    var key = property?.GetCustomAttribute<KeyAttribute>();
    Check.Equal(pair.Value, key?.IntKey ?? -1, $"MessagePack key {pair.Key}");
}

var trackedKeys = new Dictionary<string, int>
{
    [nameof(CARAUTrackedOperation.Purpose)] = 29,
    [nameof(CARAUTrackedOperation.ConversionRoute)] = 30,
    [nameof(CARAUTrackedOperation.ConversionOriginalMindId)] = 31,
    [nameof(CARAUTrackedOperation.ConversionIncomingMindId)] = 32,
    [nameof(CARAUTrackedOperation.ConversionExpectedGeneratedMind)] = 33,
    [nameof(CARAUTrackedOperation.ConversionObservedRemovedMindId)] = 34,
    [nameof(CARAUTrackedOperation.ConversionOutgoingCorePartId)] = 35,
    [nameof(CARAUTrackedOperation.ConversionObservedIncomingMindId)] = 36,
    [nameof(CARAUTrackedOperation.ConversionInstallObservedPartId)] = 37
};
foreach (var pair in trackedKeys)
{
    var property = typeof(CARAUTrackedOperation).GetProperty(pair.Key);
    var key = property?.GetCustomAttribute<KeyAttribute>();
    Check.Equal(pair.Value, key?.IntKey ?? -1, $"tracking key {pair.Key}");
}

var allTrackedKeys = typeof(CARAUTrackedOperation)
    .GetProperties()
    .Select(property => property.GetCustomAttribute<KeyAttribute>())
    .Where(key => key != null)
    .Select(key => key.IntKey)
    .OrderBy(key => key)
    .ToArray();
Check.SequenceEqual(
    Enumerable.Range(0, 38),
    allTrackedKeys,
    "append-only tracking keys"
);

CARAUSettings.ApplySaveData(new CARAUSettingsData
{
    SchemaVersion = 6,
    QualityThresholdPercent = 80,
    AllowTierUpgrades = true,
    AllowQualityUpgrades = true,
    AllowEnemyConversion = true,
    AllowEnemyConversionObeyChip = true,
    AllowEnemyConversionCoreSwap = true,
    ConversionPriority = (EnemyConversionPriority)999
});
Check.Equal(
    EnemyConversionPriority.ObeyChipThenCoreSwap,
    CARAUSettings.ConversionPriority,
    "unknown order fallback"
);

Console.WriteLine("Enemy Conversion persistence checks passed.");

static class Check
{
    public static void ConversionState(
      bool master,
      bool obeyChip,
      bool coreSwap,
      string name
    )
    {
        Equal(master, CARAUSettings.AllowEnemyConversion, $"{name} master");
        Equal(
            obeyChip,
            CARAUSettings.AllowEnemyConversionObeyChip,
            $"{name} Obey"
        );
        Equal(
            coreSwap,
            CARAUSettings.AllowEnemyConversionCoreSwap,
            $"{name} Core"
        );
    }

    public static void ConversionData(
      CARAUSettingsData data,
      bool master,
      bool obeyChip,
      bool coreSwap,
      string name
    )
    {
        Equal(master, data.AllowEnemyConversion, $"{name} master");
        Equal(
            obeyChip,
            data.AllowEnemyConversionObeyChip,
            $"{name} Obey"
        );
        Equal(
            coreSwap,
            data.AllowEnemyConversionCoreSwap,
            $"{name} Core"
        );
    }

    public static void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{name}: expected {expected}, found {actual}"
            );
        }
    }

    public static void SequenceEqual<T>(
      IEnumerable<T> expected,
      IEnumerable<T> actual,
      string name
    )
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"{name}: expected [{string.Join(", ", expected)}], " +
                $"found [{string.Join(", ", actual)}]"
            );
        }
    }
}
