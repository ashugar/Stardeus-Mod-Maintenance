using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Data;
using Game.Systems.Creatures;
using Game.Systems.UIUX;
using HarmonyLib;
using LocalLogMod.Logging;

namespace LocalLogMod.CrewUI {
    [HarmonyPatch]
    internal static class CrewUIHooks {
        private static readonly FieldInfo GroupsField = AccessTools.Field(typeof(BeingGroupsUI), "groups");
        private static readonly FieldInfo StateField = AccessTools.Field(typeof(BeingGroupsUI), "S");

        [HarmonyPatch(typeof(BeingGroupsUI), "RebuildGroups")]
        [HarmonyPostfix]
        private static void RebuildRequested() {
            SessionLog.Guard("crew_ui.rebuild_requested", () => SessionLog.Write("CrewUI",
                "rebuild_requested"));
        }

        [HarmonyPatch(typeof(BeingGroupsUI), "DoRebuildGroups")]
        [HarmonyPostfix]
        private static void GroupsRebuilt(BeingGroupsUI __instance) {
            SessionLog.Guard("crew_ui.rebuild", () => {
                if (StateField == null || GroupsField == null) {
                    throw new MissingFieldException("BeingGroupsUI.S or BeingGroupsUI.groups");
                }
                var state = StateField?.GetValue(__instance) as GameState;
                var groups = GroupsField?.GetValue(__instance) as List<BeingGroup>;
                if (state == null || groups == null) { return; }
                SessionLog.SetTick(state.Ticks);
                SessionLog.Write("CrewUI", "rebuild friendlies=" + state.Situation.Friendlies
                    + " groupCount=" + groups.Count
                    + " groupIcons=" + __instance.SpawnedWidgets.Count
                    + " avatars=" + __instance.SpawnedAvatars.Count);
                foreach (var group in groups) {
                    int visibleMembers = 0;
                    int renderedAvatars = 0;
                    foreach (var member in group.Members) {
                        if (!member.HideAvatarButton) { visibleMembers++; }
                        if (__instance.SpawnedAvatars.ContainsKey(member)) { renderedAvatars++; }
                    }
                    bool icon = __instance.SpawnedWidgets.ContainsKey(group);
                    string result = group.Size == 0 ? "empty"
                        : icon && visibleMembers == 0 ? "group_icon_rendered_all_avatars_hidden"
                        : icon || renderedAvatars > 0 ? "rendered"
                        : !group.IsNotControllable && state.Situation.Friendlies <= 10
                            && visibleMembers == 0 ? "missing_all_hidden_at_10_or_less"
                        : "not_rendered";
                    SessionLog.Write("CrewUI", "group id=" + group.Id
                        + " members=" + group.Size
                        + " visibleMembers=" + visibleMembers
                        + " hiddenMembers=" + (group.Size - visibleMembers)
                        + " icon=" + icon
                        + " renderedAvatars=" + renderedAvatars
                        + " result=" + result);
                    if (group.IsNotControllable) { continue; }
                    foreach (var member in group.Members) {
                        SessionLog.Write("CrewUI", "member group=" + group.Id
                            + " data=" + member.Id
                            + " name=" + member.Mind?.Name?.DisplayName
                            + " species=" + member.Species?.Id
                            + " state=" + member.StateFlags
                            + " incapacitated=" + member.Body?.IsIncapacitated
                            + " hideAvatar=" + member.HideAvatarButton
                            + " avatarRendered=" + __instance.SpawnedAvatars.ContainsKey(member));
                    }
                }
            });
        }

        [HarmonyPatch(typeof(WBeingAvatarGroup), "ApplyToggles")]
        [HarmonyPrefix]
        private static void BeforeVisibilityApply(WBeingAvatarGroup __instance,
                out Dictionary<BeingData, bool> __state) {
            var previous = new Dictionary<BeingData, bool>();
            SessionLog.Guard("crew_ui.visibility_before", () => {
                if (__instance.Group == null) { return; }
                foreach (var member in __instance.Group.Members) {
                    previous[member] = member.HideAvatarButton;
                }
            });
            __state = previous;
        }

        [HarmonyPatch(typeof(WBeingAvatarGroup), "ApplyToggles")]
        [HarmonyPostfix]
        private static void VisibilityApplied(WBeingAvatarGroup __instance,
                Dictionary<BeingData, bool> __state) {
            SessionLog.Guard("crew_ui.visibility_after", () => {
                int changed = 0;
                foreach (var entry in __state) {
                    if (entry.Key.HideAvatarButton == entry.Value) { continue; }
                    changed++;
                    SessionLog.Write("CrewUI", "avatar_visibility_changed group=" + __instance.Group?.Id
                        + " data=" + entry.Key.Id
                        + " name=" + entry.Key.Mind?.Name?.DisplayName
                        + " hideBefore=" + entry.Value
                        + " hideAfter=" + entry.Key.HideAvatarButton);
                }
                SessionLog.Write("CrewUI", "avatar_visibility_applied group=" + __instance.Group?.Id
                    + " changed=" + changed + " members=" + __state.Count);
            });
        }

        [HarmonyPatch(typeof(BeingGroupsUI), "OnGroupChanged")]
        [HarmonyPostfix]
        private static void GroupChanged(BeingData data, int oldGroupId) {
            SessionLog.Guard("crew_ui.group_changed", () => SessionLog.Write("CrewUI",
                "membership_changed data=" + data?.Id
                + " name=" + data?.Mind?.Name?.DisplayName
                + " from=" + oldGroupId + " to=" + data?.GroupId
                + " state=" + data?.StateFlags));
        }

        [HarmonyPatch(typeof(BeingGroupsUI), "OnShipCombatMode")]
        [HarmonyPostfix]
        private static void CombatModeChanged(bool on) {
            SessionLog.Guard("crew_ui.combat_mode", () => SessionLog.Write("CrewUI",
                "ship_combat_mode on=" + on));
        }

        [HarmonyPatch(typeof(BeingGroupsUI), "OnEnable")]
        [HarmonyPostfix]
        private static void WidgetEnabled() {
            SessionLog.Guard("crew_ui.enabled", () => SessionLog.Write("CrewUI", "widget_enabled"));
        }

        [HarmonyPatch(typeof(BeingGroupsUI), "OnDisable")]
        [HarmonyPostfix]
        private static void WidgetDisabled() {
            SessionLog.Guard("crew_ui.disabled", () => SessionLog.Write("CrewUI", "widget_disabled"));
        }
    }
}
