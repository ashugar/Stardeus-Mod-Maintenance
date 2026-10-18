using AutoUtilitiesModFramework;
using ColonistAndBotsAutoUpgrade.Models;
using ColonistAndBotsAutoUpgrade.UI;
using Game.Data;
using Game.Systems;
using Game.Utils;
using KL.Utils;
using System.Collections.Generic;
using UnityEngine;

namespace ColonistAndBotsAutoUpgrade.Systems
{
    public sealed class ColonistAndBotsAutoUpgradeSys : GameSystem,
        ISaveableSpecial
    {
        public const string SysId = "ColonistAndBotsAutoUpgradeSys";
        public const string RequiredUtilitiesVersion = "1.4.0";
        private const string SettingsSaveKey =
            "ColonistAndBotsAutoUpgrade.Settings";

        private ColonistAndBotsAutoUpgradePanel panel;
        private IShortcutHandle shortcutHandler;
        private ITopBarWidgetHandle mainWidget;
        private bool settingsLoadedForCurrentGame;

        public override string Id => SysId;
        public override bool SkipInSandbox => false;

        public static ColonistAndBotsAutoUpgradeSys Instance { get; private set; }

        [SelfInit(SelfInit.Stage.CoreDefs, 0)]
        private static void Register()
        {
            GameSystems.Register(SysId, () => new ColonistAndBotsAutoUpgradeSys());
        }

        protected override void OnInitialize()
        {
            if (!AutoUtilitiesApi.TryRegisterConsumer(
                    "ColonistAndBotsAutoUpgrade",
                    "Colonist And Bots Auto Upgrade",
                    CARAUVersion.Current,
                    RequiredUtilitiesVersion))
            {
                return;
            }

            Instance = this;

            shortcutHandler = AutoUtilitiesApi.RegisterShortcut(
              new ShortcutRegistration
              {
                  ConsumerId = "ColonistAndBotsAutoUpgrade",
                  Id = "TogglePanel",
                  DisplayName = "open or close CARAU",
                  PreferenceKey = "carau.ui.shortcut",
                  GameState = S,
                  OnPressed = TogglePanel
              }
            );

            panel = new ColonistAndBotsAutoUpgradePanel(
              S,
              shortcutHandler
            );

            S.Sig.BeingsSelected.AddListener(OnBeingsSelected);
            S.Sig.TilesSelected.AddListener(OnTilesSelected);
            S.Sig.ObjSelected.AddListener(OnObjSelected);
            S.Sig.MultiSelect.AddListener(OnMultiSelect);
            S.Sig.AfterLoadState.AddListener(OnAfterLoadState);
            S.Sig.NewGameInitialized.AddListener(OnNewGameInitialized);

            mainWidget = AutoUtilitiesApi.RegisterTopBarWidget(
              new TopBarWidgetRegistration
              {
                  Id = "ColonistAndBotsAutoUpgrade.Main",
                  Icon = () => "Icons/Color/CARAUIcon",
                  Tint = () => Color.white,
                  Tooltip = () =>
                      "<b>Colonist And Bots Auto Upgrade</b>\n" +
                      "Click to open or close CARAU.",
                  OnClick = TogglePanel,
                  Width = 48f,
                  Order = 390
              }
            );
        }

        private void OnMultiSelect(List<Being> beings, List<Obj> objs, List<Tile> tiles)
        {
            if (beings?.Count > 0)
            {
                OnBeingsSelected(beings);
                return;
            }

            if (tiles?.Count > 0)
            {
                OnTilesSelected(tiles);
                return;
            }

            if (objs?.Count > 0)
            {
                OnObjSelected(objs);
                return;
            }

            // Single-selection signals can be followed by an empty MultiSelect
            // notification. The dedicated selection handler already owns the state.
        }

        private void OnBeingsSelected(List<Being> beings)
        {
            var debugMessage = "Selection changed: None";
            if (beings == null || beings.Count == 0)
            {
                CARAUDebugLog.Add(CARAUDebugCategory.Selection, debugMessage);
                panel?.SetSelection("None", "Nothing selected");
                return;
            }

            if (beings.Count == 1)
            {
                var being = beings[0];
                var name = being.Data?.Name ?? being.ToString();
                debugMessage = $"Selection changed: Being - {name}";

                var body = being.Data?.Body;
                var species = being.Data?.Species;
                var decisionMessage =
                    $"Selection diagnostic | " +
                    $"ModVersion={CARAUVersion.Current} | " +
                    $"Being={name} | " +
                    $"BodyAvailable={body != null} | " +
                    $"BodyAlive={body?.IsAlive.ToString() ?? "unknown"} | " +
                    $"Species={species?.Name ?? "unknown"} | " +
                    $"Biological={species?.IsBiological.ToString() ?? "unknown"}";

                CARAUDebugLog.Add(CARAUDebugCategory.Selection, debugMessage);
                CARAUDebugLog.Add(CARAUDebugCategory.Decision, decisionMessage);
                panel?.SetBeingDebugInfo(being);
                return;
            }

            debugMessage = $"Selection changed: Beings - {beings.Count} selected";
            CARAUDebugLog.Add(CARAUDebugCategory.Selection, debugMessage);
            panel?.SetBeings(beings);
        }

        private void OnTilesSelected(List<Tile> tiles)
        {
            if (tiles == null || tiles.Count == 0)
            {
                panel?.SetSelection("None", "Nothing selected");
                return;
            }

            if (tiles.Count > 1)
            {
                panel?.SetSelection("Tiles", $"{tiles.Count} selected");
                return;
            }

            var obj = EntityUtils.ParentObj(tiles[0]);

            if (obj != null)
            {
                panel?.SetSelection("Object", obj.Definition?.NameT ?? obj.ToString());
                return;
            }

            panel?.SetSelection("Tile", "Empty Tile");
        }

        private void OnObjSelected(List<Obj> objs)
        {
            if (objs == null || objs.Count == 0)
            {
                panel?.SetSelection("None", "Nothing selected");
                return;
            }

            if (objs.Count == 1)
            {
                var obj = objs[0];
                var name = obj.Definition?.NameT ?? obj.ToString();

                panel?.SetSelection("Object", name);
                return;
            }

            panel?.SetSelection("Objects", $"{objs.Count} selected");
        }

        private void TogglePanel()
        {
            var opening = panel?.IsOpen != true;
            CARAUDebugLog.Add(
                CARAUDebugCategory.UI,
                $"CARAU main panel toggled directly: " +
                $"{(opening ? "open" : "close")}"
            );

            if (opening)
            {
                panel?.Show();
                return;
            }

            panel?.Close();
        }

        public void ShowPanel()
        {
            if (panel?.IsOpen == true)
            {
                return;
            }

            panel?.Show();
        }

        public void RefreshTrackedOperationState()
        {
            panel?.RefreshTrackedOperationState();
        }

        public void ResetForGameBoundary()
        {
            panel?.ResetForGameBoundary();
        }

        private void OnAfterLoadState(GameState state)
        {
            var restoredFromSave = settingsLoadedForCurrentGame;
            settingsLoadedForCurrentGame = false;

            if (!restoredFromSave)
            {
                CARAUSettings.ResetSaveSpecificDefaults();
            }

            CARAUDebugLog.Add(
                CARAUDebugCategory.Cleanup,
                "Game-boundary settings reset | " +
                $"Reason={(restoredFromSave ? "loaded save-specific settings" : "loaded save without CARAU settings")} | " +
                $"Schema={(restoredFromSave ? CARAUSettingsData.CurrentSchemaVersion : 0)} | " +
                $"QualityThreshold={CARAUSettings.QualityThresholdPercent}% | " +
                $"RetryPolicy={CARAUSettings.RetryPolicy} | " +
                $"RetryLimit={CARAUSettings.AutomaticRetryLimit} | " +
                $"RetryChanceThreshold=" +
                $"{CARAUSettings.AutomaticRetryChanceThresholdPercent}% | " +
                $"DisabledSlots={CARAUSettings.DisabledSlotIds.Count} | " +
                $"ShowDisabledSlots={CARAUSettings.ShowDisabledSlots}"
            );
        }

        private void OnNewGameInitialized()
        {
            settingsLoadedForCurrentGame = false;
            CARAUSettings.ResetSaveSpecificDefaults();
            CARAUDebugLog.Add(
                CARAUDebugCategory.Cleanup,
                "Game-boundary settings reset | Reason=new game initialized | " +
                $"Schema=0 | QualityThreshold={CARAUSettings.QualityThresholdPercent}% | " +
                $"RetryPolicy={CARAUSettings.RetryPolicy} | " +
                $"RetryLimit={CARAUSettings.AutomaticRetryLimit} | " +
                $"RetryChanceThreshold=" +
                $"{CARAUSettings.AutomaticRetryChanceThresholdPercent}% | " +
                "DisabledSlots=0 | ShowDisabledSlots=False"
            );
        }

        public void SaveSpecial(SystemsDataSpecial sd)
        {
            if (sd == null)
            {
                return;
            }

            sd.ModData ??= new Dictionary<string, byte[]>();
            sd.ModData[SettingsSaveKey] = SaveLoadUtils.Serialize(
                CARAUSettings.CreateSaveData()
            );
        }

        public void LoadSpecial(SystemsDataSpecial sd)
        {
            settingsLoadedForCurrentGame = false;
            CARAUSettings.ResetSaveSpecificDefaults();

            if (sd?.ModData == null ||
                !sd.ModData.TryGetValue(SettingsSaveKey, out var raw) ||
                raw == null ||
                raw.Length == 0)
            {
                return;
            }

            try
            {
                var data = SaveLoadUtils.Deserialize<CARAUSettingsData>(raw);
                if (data == null)
                {
                    return;
                }

                if (data.SchemaVersion > CARAUSettingsData.CurrentSchemaVersion)
                {
                    CARAUDebugLog.Add(
                        CARAUDebugCategory.Warning,
                        "Save-specific settings use a newer schema | " +
                        $"SavedSchema={data.SchemaVersion} | " +
                        $"SupportedSchema={CARAUSettingsData.CurrentSchemaVersion} | " +
                        "Known fields will be loaded safely"
                    );
                }

                var savedPolicyKnown = CARAUSettings.IsKnownRetryPolicy(
                    data.RetryPolicy
                );
                var clampedLimit = CARAUSettings.ClampAutomaticRetryLimit(
                    data.AutomaticRetryLimit
                );
                var clampedChance = CARAUSettings.ClampPercentage(
                    data.AutomaticRetryChanceThresholdPercent
                );
                CARAUSettings.ApplySaveData(data);
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Cleanup,
                    "Save-specific retry settings restored | " +
                    $"SavedSchema={data.SchemaVersion} | " +
                    $"Migration={(data.SchemaVersion < 2 ? "Defaults3And90" : "None")} | " +
                    $"SavedPolicy={data.RetryPolicy} | " +
                    $"PolicyKnown={savedPolicyKnown} | " +
                    $"SavedLimit={data.AutomaticRetryLimit} | " +
                    $"ClampedLimit={clampedLimit} | " +
                    $"SavedChance={data.AutomaticRetryChanceThresholdPercent}% | " +
                    $"ClampedChance={clampedChance}% | " +
                    $"AppliedPolicy={CARAUSettings.RetryPolicy} | " +
                    $"AppliedLimit={CARAUSettings.AutomaticRetryLimit} | " +
                    $"AppliedChance=" +
                    $"{CARAUSettings.AutomaticRetryChanceThresholdPercent}%"
                );
                settingsLoadedForCurrentGame = true;
            }
            catch (System.Exception ex)
            {
                CARAUSettings.ResetSaveSpecificDefaults();
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Error,
                    "Save-specific settings could not be restored; defaults applied | " +
                    $"Error={ex.Message}"
                );
            }
        }

        public override void Unload()
        {
            S.Sig.BeingsSelected.RemoveListener(OnBeingsSelected);
            S.Sig.TilesSelected.RemoveListener(OnTilesSelected);
            S.Sig.ObjSelected.RemoveListener(OnObjSelected);
            S.Sig.MultiSelect.RemoveListener(OnMultiSelect);
            S.Sig.AfterLoadState.RemoveListener(OnAfterLoadState);
            S.Sig.NewGameInitialized.RemoveListener(OnNewGameInitialized);

            panel?.Dispose();
            panel = null;

            mainWidget?.Dispose();
            mainWidget = null;

            shortcutHandler?.Dispose();
            shortcutHandler = null;

            if (ReferenceEquals(Instance, this))
            {
                Instance = null;
            }

            settingsLoadedForCurrentGame = false;
        }
    }
}
