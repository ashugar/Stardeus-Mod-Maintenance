using Game.Systems.BodyParts;
using System;
using System.Collections.Generic;

namespace ColonistAndBotsAutoUpgrade.Services
{
    /// <summary>
    /// Shared all-slots-in-a-group evaluation for terminal mechanical body
    /// parts. Multi-Battery and multi-Mobilizer bodies are intentionally
    /// evaluated as complete groups rather than individual slots.
    /// </summary>
    public static class TerminalBodyPartGroupService
    {
        public static string[] GetCompleteTerminalGroupLabels(
          BodyPartSlot[] slots,
          params string[] groupIds
        )
        {
            return GetCompleteGroupLabels(
                slots,
                IsTerminalSlot,
                groupIds
            );
        }

        public static string[] GetCompleteWeldedUnusableGroupLabels(
          BodyPartSlot[] slots,
          params string[] groupIds
        )
        {
            return GetCompleteGroupLabels(
                slots,
                IsWeldedUnusableSlot,
                groupIds
            );
        }

        private static string[] GetCompleteGroupLabels(
          BodyPartSlot[] slots,
          Func<BodyPartSlot, bool> isEligible,
          params string[] groupIds
        )
        {
            var terminalGroups = new List<string>();
            if (slots == null || isEligible == null || groupIds == null)
            {
                return terminalGroups.ToArray();
            }

            for (var groupIndex = 0;
                 groupIndex < groupIds.Length;
                 groupIndex++)
            {
                var groupId = groupIds[groupIndex];
                var slotCount = 0;
                var terminalCount = 0;

                for (var slotIndex = 0;
                     slotIndex < slots.Length;
                     slotIndex++)
                {
                    var slot = slots[slotIndex];
                    if (!string.Equals(
                            slot?.Type?.Id,
                            groupId,
                            StringComparison.Ordinal
                        ))
                    {
                        continue;
                    }

                    slotCount++;
                    if (isEligible(slot))
                    {
                        terminalCount++;
                    }
                }

                if (slotCount > 0 && terminalCount == slotCount)
                {
                    terminalGroups.Add(
                        slotCount == 1
                            ? groupId
                            : $"{groupId} ({slotCount}/{slotCount})"
                    );
                }
            }

            return terminalGroups.ToArray();
        }

        public static bool IsTerminalSlot(BodyPartSlot slot)
        {
            if (slot == null)
            {
                return false;
            }

            if (slot.IsDestroyed)
            {
                return true;
            }

            return !slot.IsPartRemoved &&
                slot.InstalledDef != null &&
                slot.State?.IsWelded == true &&
                !slot.IsUsableSelf;
        }

        public static bool IsWeldedUnusableSlot(BodyPartSlot slot)
        {
            return slot != null &&
                !slot.IsDestroyed &&
                !slot.IsPartRemoved &&
                slot.InstalledDef != null &&
                slot.State?.IsWelded == true &&
                !slot.IsUsableSelf;
        }
    }
}
