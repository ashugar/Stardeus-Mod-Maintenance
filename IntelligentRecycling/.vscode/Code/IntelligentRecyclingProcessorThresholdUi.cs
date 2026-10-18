using System.Reflection;
using Game.Components;
using Game.CodeGen;
using Game.Constants;
using Game.Data;
using Game.Systems.UIUX;
using Game.UI;
using Game.Utils;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.IntelligentRecycling {
    /// <summary>
    /// Completes Core's processor UI by exposing the organic threshold that
    /// DisposerTab otherwise hides. The ordinary robotic slider remains owned
    /// by Core; both sliders feed the same bidirectional IR synchronization.
    /// </summary>
    [HarmonyPatch(typeof(DisposerTab), nameof(DisposerTab.LoadContent))]
    internal static class IntelligentRecyclingProcessorThresholdUi {
        private static readonly FieldInfo DisposerField = AccessTools.Field(
            typeof(DisposerTab), "disposer");
        private static readonly FieldInfo LifetimeField = AccessTools.Field(
            typeof(BaseEntityTab), "lifetime");

        [HarmonyPostfix]
        private static void AfterLoadContent(DisposerTab __instance,
                WGroup group, bool __result) {
            if (!__result || __instance == null || group == null
                    || DisposerField == null || LifetimeField == null) {
                return;
            }
            DisposerComp disposer = DisposerField.GetValue(__instance)
                as DisposerComp;
            if (disposer == null
                    || disposer.SlotDesignation
                        != SlotDesignationH.Disposer_Organic) {
                return;
            }
            Lifetime lifetime = LifetimeField.GetValue(__instance) as Lifetime;
            if (lifetime == null) {
                return;
            }

            // Core's settings header is block 0 and its queue is block 1. Insert
            // this missing organic control between them so it appears where the
            // native robotic threshold normally appears.
            WContentBlock block = UISpawn.Block(lifetime, group, 1);
            WSliderWithIconText2 slider =
                UISpawn.Widget<WSliderWithIconText2>(
                    lifetime, "WContent2Text2Slider", block.Container, 0);
            slider.Item.Load(IconId.CCog, "Biological quality threshold",
                Units.Percentage(disposer.PartQualityThreshold));
            slider.Slider.Load(disposer.PartQualityThreshold, value => {
                disposer.PartQualityThreshold = value;
                slider.Item.SetText2(
                    Units.Percentage(disposer.PartQualityThreshold));
            });
            UIUtils.AddTip(lifetime, slider,
                "Shared with Intelligent Recycling and every Nutrient "
                + "Extractor. Qualifying biological body parts at or below "
                + "this Quality may be sent for disposal.");
        }
    }
}
