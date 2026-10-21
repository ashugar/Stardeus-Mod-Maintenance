using System;
using System.Reflection;
using Game;
using Game.CodeGen;
using Game.Constants;
using Game.Data;
using Game.Systems.Quests;
using Game.Systems.Ships;
using Game.Systems.UIUX;
using Game.UI;
using Game.Utils;
using HarmonyLib;
using KL.Utils;
using UnityEngine;

namespace Game.Systems.QuestRequestPreview {
    /// <summary>
    /// Adds a neutral way to close quest offers without changing quest state.
    /// </summary>
    public sealed class QuestRequestPreviewSys : GameSystem {
        public const string SysId = "QuestRequestPreviewSys";
        private const string HarmonyId = "jleas.stardeus.questrequestpreview";
        private Harmony harmony;

        public override string Id => SysId;
        public override bool SkipInSandbox => false;

        /// <summary>
        /// Registers the mod system so Stardeus creates it for each game state.
        /// </summary>
        [SelfInit]
        private static void Register() {
            GameSystems.Register(SysId, () => new QuestRequestPreviewSys());
        }

        /// <summary>
        /// Applies this assembly's Harmony patches when the mod system starts.
        /// </summary>
        protected override void OnInitialize() {
            Harmony localHarmony = null;
            try {
                localHarmony = new Harmony(HarmonyId);
                ValidateCoreContracts();
                localHarmony.PatchAll(typeof(QuestRequestPreviewSys).Assembly);
                harmony = localHarmony;

                var version = typeof(QuestRequestPreviewSys)
                    .Assembly.GetName().Version.ToString(3);
                D.Warn("Quest Request Preview {0} initialized.", version);
            } catch (Exception initializationError) {
                Exception cleanupError = null;
                try {
                    localHarmony?.UnpatchAll(HarmonyId);
                } catch (Exception unpatchError) {
                    cleanupError = unpatchError;
                }

                harmony = null;
                var cleanupDetail = cleanupError == null
                    ? string.Empty
                    : " Cleanup also failed: " + cleanupError;
                D.Err(
                    "Quest Request Preview disabled because its required UI "
                    + "contracts or Harmony patches are unavailable: "
                    + initializationError
                    + cleanupDetail);
            }
        }

        /// <summary>
        /// Verifies the exact public rc341 UI contracts used by patched code
        /// before any Harmony patch is applied.
        /// </summary>
        private static void ValidateCoreContracts() {
            var afterShowPopup = RequirePublicStaticVoid(
                typeof(UIUtils),
                nameof(UIUtils.AfterShowPopup),
                typeof(RectTransform),
                typeof(bool),
                typeof(float),
                typeof(bool),
                typeof(bool),
                typeof(bool));
            var popupParameters = afterShowPopup.GetParameters();
            RequireOptionalDefault(popupParameters[1], false);
            RequireOptionalDefault(popupParameters[2], 1f);
            RequireOptionalDefault(popupParameters[3], true);
            RequireOptionalDefault(popupParameters[4], false);
            RequireOptionalDefault(popupParameters[5], true);

            RequirePublicStaticVoid(
                typeof(QuestTrackerHandoff),
                nameof(QuestTrackerHandoff.FocusTracker),
                typeof(Quest));
        }

        /// <summary>
        /// Resolves an exact public static void method or rejects the Core
        /// contract without invoking it through reflection.
        /// </summary>
        /// <param name="declaringType">Type that must declare the method.</param>
        /// <param name="methodName">Required method name.</param>
        /// <param name="parameterTypes">Required parameter types in order.</param>
        /// <returns>The validated method metadata.</returns>
        private static MethodInfo RequirePublicStaticVoid(
                Type declaringType,
                string methodName,
                params Type[] parameterTypes) {
            var method = declaringType.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Static,
                null,
                parameterTypes,
                null);
            if (method == null
                    || method.DeclaringType != declaringType
                    || method.ReturnType != typeof(void)
                    || method.IsGenericMethodDefinition) {
                throw new MissingMethodException(
                    declaringType.FullName,
                    methodName);
            }

            return method;
        }

        /// <summary>
        /// Requires an optional parameter to retain the compiler default used
        /// by this mod's direct source call.
        /// </summary>
        /// <param name="parameter">Parameter metadata being validated.</param>
        /// <param name="expectedDefault">Required default value.</param>
        private static void RequireOptionalDefault(
                ParameterInfo parameter,
                object expectedDefault) {
            if (!parameter.IsOptional
                    || !parameter.HasDefaultValue
                    || !Equals(parameter.DefaultValue, expectedDefault)) {
                throw new MissingMethodException(
                    "Required optional default is unavailable for "
                    + parameter.Member.DeclaringType?.FullName
                    + "."
                    + parameter.Member.Name
                    + " parameter "
                    + parameter.Name
                    + ".");
            }
        }

        /// <summary>
        /// Removes patches owned by this mod when the current game state unloads.
        /// </summary>
        public override void Unload() {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
        }
    }

    /// <summary>
    /// Owns the shared presentation for state-neutral quest-preview actions.
    /// </summary>
    internal static class QuestPreviewUi {
        private const string NeutralCloseTip =
            "Close this preview without accepting or rejecting the quest.";

        /// <summary>
        /// Appends a standard Close button that invokes only the supplied UI
        /// close action and therefore does not change quest state.
        /// </summary>
        /// <param name="lifetime">Lifetime that owns the spawned button.</param>
        /// <param name="footer">Footer that receives the button.</param>
        /// <param name="siblingIndex">Desired button position in the footer.</param>
        /// <param name="closeAction">State-neutral action that closes the UI.</param>
        internal static void AddNeutralCloseButton(
                Lifetime lifetime,
                WGroup footer,
                int siblingIndex,
                Action closeAction) {
            var closeButton = UISpawn.Widget<WButtonText>(
                lifetime,
                UIPrefabId.WPanelButton,
                footer.Container,
                siblingIndex);
            closeButton.Load(T.Close, closeAction);
            UIUtils.AddTip(lifetime, closeButton, NeutralCloseTip);
        }
    }

    /// <summary>
    /// Defines which incoming offers can safely receive the mod's neutral
    /// preview controls without implying a nonexistent way to reopen them.
    /// </summary>
    internal static class QuestPreviewEligibility {
        /// <summary>
        /// Identifies personal favors created by one-shot colonist story events.
        /// Core ends and hides those events as soon as their Respond/Ignore
        /// prompt appears, so an unanswered preview has no later entry point.
        /// </summary>
        /// <param name="quest">Incoming quest being evaluated.</param>
        /// <returns>
        /// <see langword="true"/> when the provider is Core's colonist provider.
        /// </returns>
        internal static bool IsOneShotColonistRequest(Quest quest) {
            return quest?.Data?.Provider is ColonistQuestProvider;
        }
    }

    /// <summary>
    /// Keeps undecided Signal offers in Core's existing serialized quest list
    /// without changing their <see cref="QuestState"/>.
    /// </summary>
    internal static class UndecidedSignalPersistence {
        /// <summary>
        /// Determines whether a quest is an undecided Signal offer that needs
        /// save/load preservation while its preview remains unanswered.
        /// </summary>
        /// <param name="quest">Quest being evaluated.</param>
        /// <returns>
        /// <see langword="true"/> only for an Undefined quest whose definition
        /// carries the Signal flag.
        /// </returns>
        internal static bool IsUndecidedSignal(Quest quest) {
            return quest?.Data?.Def != null
                && quest.Data.State == QuestState.Undefined
                && quest.Data.Def.Flags.HasFlag(QuestFlags.Signal);
        }

        /// <summary>
        /// Adds an undecided Signal offer to Core's save-backed player quest
        /// collection once, leaving the quest state and active lists unchanged.
        /// </summary>
        /// <param name="state">Current game state that owns the quest list.</param>
        /// <param name="quest">Signal offer that must survive save/load.</param>
        internal static void EnsureRegistered(GameState state, Quest quest) {
            if (!IsUndecidedSignal(quest)) {
                return;
            }

            var allPlayerQuests = state.Query.GetAllPlayerQuests.Ask();
            if (!allPlayerQuests.Contains(quest)) {
                allPlayerQuests.Add(quest);
            }
        }

        /// <summary>
        /// Removes the temporary save registration before Core accepts or the
        /// mod rejects the offer, allowing those vanilla paths to own the list.
        /// </summary>
        /// <param name="state">Current game state that owns the quest list.</param>
        /// <param name="quest">Signal offer whose temporary registration ends.</param>
        internal static void RemoveRegistration(GameState state, Quest quest) {
            state.Query.GetAllPlayerQuests.Ask().Remove(quest);
        }
    }

    /// <summary>
    /// Rebuilds only the incoming quest-offer popup. Core's original close callback
    /// rejects Signal quests; this version makes close/Escape neutral and provides
    /// an explicit Reject button for Signal quests instead.
    /// </summary>
    [HarmonyPatch(
        typeof(QuestsHandler),
        "ShowNewQuest",
        new[] { typeof(Quest) })]
    internal static class NewQuestPreviewPatch {
        /// <summary>
        /// Replaces Core's incoming-offer popup for valid new quests so closing
        /// is neutral while Accept and explicit Signal rejection remain intact.
        /// </summary>
        /// <param name="q">Quest Core asked to present.</param>
        /// <param name="__result">Result returned to Core when this patch handles the popup.</param>
        /// <returns>
        /// <see langword="false"/> after building the replacement popup;
        /// otherwise <see langword="true"/> to preserve Core's fallback path.
        /// </returns>
        [HarmonyPrefix]
        private static bool Prefix(Quest q, ref bool __result) {
            var state = A.S;
            if (state == null || q?.Data == null) {
                return true;
            }

            // Personal favors are emitted by one-shot story events that end and
            // hide their log entry before opening the quest preview. Leave the
            // entire popup on Core's path instead of offering a neutral action
            // that cannot lead back to the same unanswered request later.
            if (QuestPreviewEligibility.IsOneShotColonistRequest(q)) {
                return true;
            }

            var alreadyRegistered = state.Query.GetAllPlayerQuests.Ask().Contains(q);
            var isUndecidedSignal =
                UndecidedSignalPersistence.IsUndecidedSignal(q);

            // Preserve Core's already-added recovery path for ordinary quests.
            // An Undefined Signal may already be registered solely so Core will
            // serialize it; it still needs this Accept/Close/Reject preview.
            if (alreadyRegistered && !isUndecidedSignal) {
                return true;
            }

            // Register before displaying the popup so saving while it is open or
            // after a neutral close preserves the complete offer and provider.
            UndecidedSignalPersistence.EnsureRegistered(state, q);

            state.Query.UpdateQuestSeen.Ask(q.Data);

            var lifetime = Lifetime.Create("QuestRequestPreview.NewQuest");
            var panel = UISpawn.Widget<WPanel>(
                lifetime,
                UIPrefabId.WListChoicePopup_Wide,
                UIPoolRoot.PopupContainer,
                0);
            panel.Rect.SetAsLastSibling();

            // WPanel.TryCloseWidget invokes this header callback, so both the
            // visible close control and Escape now leave QuestState unchanged.
            panel.Load(T.QuestNewLabel, lifetime.Dispose);
            QuestDetailsUI.LoadBlock(lifetime, panel.Content, q);

            int footerIndex = 0;
            var acceptButton = UISpawn.Widget<WButtonText>(
                lifetime,
                UIPrefabId.WPanelButton_Confirm,
                panel.Footer.Container,
                footerIndex++);
            acceptButton.Load(
                T.VerbAccept,
                () => AcceptQuest(state, q, lifetime));

            QuestPreviewUi.AddNeutralCloseButton(
                lifetime,
                panel.Footer,
                footerIndex++,
                lifetime.Dispose);

            // Preserve the ability to deliberately reject Signal quests. Core
            // previously attached this state transition to closing the popup.
            if (q.Data.Def.Flags.HasFlag(QuestFlags.Signal)) {
                var rejectButton = UISpawn.Widget<WButtonText>(
                    lifetime,
                    UIPrefabId.WPanelButton_Red,
                    panel.Footer.Container,
                    footerIndex++);
                rejectButton.Load(
                    T.Reject,
                    () => RejectSignalQuest(state, q, lifetime));
            }

            panel.ShowFooter(footerIndex > 0);
            UIUtils.AfterShowPopup(panel.Rect, true);
            __result = true;
            return false;
        }

        /// <summary>
        /// Runs the complete vanilla acceptance path, including the NPC ship
        /// side effects that make an accepted quest provider resume idle behavior.
        /// </summary>
        /// <param name="state">Current game state used for quest queries.</param>
        /// <param name="quest">Quest being accepted.</param>
        /// <param name="popupLifetime">Lifetime of the incoming-offer popup.</param>
        private static void AcceptQuest(
                GameState state,
                Quest quest,
                Lifetime popupLifetime) {
            UndecidedSignalPersistence.RemoveRegistration(state, quest);
            state.Query.AcceptQuest.Ask(quest);
            popupLifetime.Dispose();
            QuestTrackerHandoff.FocusTracker(quest);

            if (quest.Data.Provider is NPCShip ship) {
                if (ship.IsInHyperspace) {
                    ship.ExitHyperspace();
                }
                if (ship.WantsHyperspace) {
                    ship.SetWantsHyperspace(false);
                }
                NPCShipBehavior_Region_Idle.AssignTo(ship);
            }
        }

        /// <summary>
        /// Closes the popup and performs the explicit rejection that Core used
        /// to hide inside the close callback for Signal quests.
        /// </summary>
        /// <param name="state">Current game state that owns the quest list.</param>
        /// <param name="quest">Signal quest being rejected.</param>
        /// <param name="popupLifetime">Lifetime of the incoming-offer popup.</param>
        private static void RejectSignalQuest(
                GameState state,
                Quest quest,
                Lifetime popupLifetime) {
            UndecidedSignalPersistence.RemoveRegistration(state, quest);
            popupLifetime.Dispose();
            quest.Data.SetState(QuestState.Rejected);
        }
    }

    /// <summary>
    /// Preserves Signals from saves created before undecided offers were added
    /// to Core's serialized quest list. Their missing offer can then be rebuilt
    /// by Core the next time the player responds to the Signal.
    /// </summary>
    [HarmonyPatch(typeof(QuestSys), "RemoveOrphanedSignalOffers")]
    internal static class LegacySignalRecoveryPatch {
        /// <summary>
        /// Suppresses Core's load-time deletion of a Signal whose old, undecided
        /// QuestData was not written by an earlier Quest Request Preview build.
        /// </summary>
        /// <returns>
        /// Always <see langword="false"/> so Core leaves the recoverable Signal
        /// in the world; its normal Respond action clears the stale quest ID and
        /// creates a replacement offer.
        /// </returns>
        [HarmonyPrefix]
        private static bool Prefix() {
            return false;
        }
    }

    /// <summary>
    /// Adds the same explicit neutral close action to available quest and Quest
    /// Hub details in the main Quests window. The callback closes only the subpanel.
    /// </summary>
    [HarmonyPatch(
        typeof(QuestsWidget),
        "LoadQuestFooter",
        new[] { typeof(Lifetime), typeof(WBase), typeof(Quest) })]
    internal static class AvailableQuestPreviewPatch {
        /// <summary>
        /// Appends a neutral Close action after Core builds the footer for an
        /// available main quest or a quest supplied by the Quest Hub.
        /// </summary>
        /// <param name="__instance">Quests widget whose footer was built.</param>
        /// <param name="q">Quest displayed in the details subpanel.</param>
        /// <param name="___footLife">Core's private lifetime for footer controls.</param>
        /// <param name="___ctx">Core's private quest-view context.</param>
        [HarmonyPostfix]
        private static void Postfix(
                QuestsWidget __instance,
                Quest q,
                Lifetime ___footLife,
                QuestContext ___ctx) {
            if (__instance?.SubPanel?.Footer == null || q?.Data == null) {
                return;
            }

            // Match Core's own isHubQuest test so unrelated Undefined quests do
            // not receive a footer action they would not otherwise have.
            var isHubQuest = ___ctx?.HubQuests != null
                && q.Data.State == QuestState.Undefined;
            if (!q.IsAvailable && !isHubQuest) {
                return;
            }

            if (___footLife == null || ___footLife.IsDisposed) {
                D.Err("Quest Request Preview could not use the quest footer lifetime.");
                return;
            }

            var footer = __instance.SubPanel.Footer;
            // Core's local footer index is not exposed; childCount appends after
            // whichever vanilla actions LoadQuestFooter created for this offer.
            int footerIndex = footer.Container.childCount;
            QuestPreviewUi.AddNeutralCloseButton(
                ___footLife,
                footer,
                footerIndex,
                __instance.SubPanel.Close);
            __instance.SubPanel.ShowFooter(true);
        }
    }
}
