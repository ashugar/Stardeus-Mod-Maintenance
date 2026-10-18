using Game.Constants;
using UnityEngine;

namespace AutoUtilitiesModFramework;

/// <summary>Displays all consumer/framework mismatches in one native popup.</summary>
internal sealed class CompatibilityAlertPanel
{
    private const string WorkshopUrl =
        "https://steamcommunity.com/sharedfiles/filedetails/?id=3776751765";
    private readonly FrameworkPanel panel;

    internal CompatibilityAlertPanel()
    {
        panel = new FrameworkPanel(new FrameworkPanelOptions
        {
            Id = "AutoUtilitiesModFramework.Compatibility",
            Title = "Auto Utilities Compatibility",
            MinSize = new Vector2(520f, 300f),
            MaxSize = new Vector2(1000f, 800f),
            RememberGeometry = true
        }, Build);
    }

    internal bool IsOpen => panel.IsOpen;
    internal void Show() => panel.Show();
    internal void Toggle() => panel.Toggle();
    internal void Refresh() => panel.Refresh();
    internal void Dispose() => panel.Dispose();

    private static void Build(PanelContentContext ui)
    {
        ui.AddHeader("Framework compatibility notice");
        ui.AddInfo(GraphicsId.IconsColorInfo, "Installed framework",
            AutoUtilitiesApi.FrameworkVersion,
            "The version currently supplied by Auto Utilities Mod Framework.");

        foreach (ConsumerCompatibilityIssue issue in
                 ConsumerCompatibilityService.GetIssues())
        {
            ui.AddInfo(GraphicsId.IconsColorInfo,
                issue.DisplayName + " v" + issue.ConsumerModVersion,
                "Tested AUMF " + issue.ConsumerFrameworkVersion,
                issue.Summary + ". " + issue.DisplayName
                + " was tested with AUMF "
                + issue.ConsumerFrameworkVersion + ", while AUMF "
                + issue.InstalledFrameworkVersion + " is installed.");
        }

        var unavailable = ConsumerCompatibilityService
            .GetUnavailableFeatures();
        if (unavailable.Count > 0)
        {
            ui.AddHeader("Features not validated for older consumers");
            foreach (FrameworkFeatureDefinition feature in unavailable)
            {
                int affected = ConsumerCompatibilityService
                    .CountAffectedConsumers(feature);
                ui.AddInfo(GraphicsId.IconsColorInfo, feature.Name,
                    "AUMF " + feature.IntroducedVersion + " — " + affected
                    + (affected == 1 ? " mod" : " mods"),
                    feature.Description);
            }
        }

        ui.AddButton(GraphicsId.IconsColorInfo,
            "Open Auto Utilities Mod Framework Workshop page",
            "Open", () => Application.OpenURL(WorkshopUrl),
            WorkshopUrl);
    }
}
