using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Components;
using Game.Constants;
using HarmonyLib;

namespace Game.Systems.QuantumStorage
{
    internal static class QuantumStorageReflection
    {
        private static readonly FieldInfo RawMaterialsField = AccessTools.Field(typeof(MatStorageComp), "rawMaterials");
        private static readonly FieldInfo ReservationsField = AccessTools.Field(typeof(MatStorageComp), "reservations");
        private static readonly FieldInfo BaseCapacityField = AccessTools.Field(typeof(MatStorageComp), "baseCapacity");
        private static readonly FieldInfo UsedCapacityField = AccessTools.Field(typeof(MatStorageComp), "usedCapacity");
        private static readonly MethodInfo UpdateProgressBarMethod = AccessTools.Method(typeof(MatStorageComp), "UpdateProgressBar");

        public static bool HasUsedCapacityCache => UsedCapacityField != null;

        public static void Validate()
        {
            if (RawMaterialsField == null || ReservationsField == null || BaseCapacityField == null || UpdateProgressBarMethod == null)
            {
                throw new MissingMemberException("Quantum Storage could not bind the current MatStorageComp lifecycle fields.");
            }
        }

        public static Dictionary<MatType, int> GetContents(MatStorageComp storage)
        {
            return (Dictionary<MatType, int>)RawMaterialsField.GetValue(storage);
        }

        public static void Bind(MatStorageComp storage, Dictionary<MatType, int> contents, MatStorageReservations reservations)
        {
            RawMaterialsField.SetValue(storage, contents);
            ReservationsField.SetValue(storage, reservations);
        }

        public static int GetBaseCapacity(MatStorageComp storage)
        {
            return (int)BaseCapacityField.GetValue(storage);
        }

        public static void SetUsedCapacity(MatStorageComp storage, int usedCapacity)
        {
            UsedCapacityField?.SetValue(storage, usedCapacity);
        }

        public static void RefreshDisplay(MatStorageComp storage)
        {
            UpdateProgressBarMethod.Invoke(storage, null);
            storage.OnChange.Invoke();
        }
    }
}
