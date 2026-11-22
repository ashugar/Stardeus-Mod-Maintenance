using System;
using Game.Components;
using Game.Systems.Nanobots;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.QuantumStorage.Relays
{
    internal static class RelayMaterialSources
    {
        private const string Owner = "jleas.quantumstorage.relay-material-sources";
        private static Harmony harmony;
        private static bool installed, cleanupUnconfirmed;
        internal static string Status => cleanupUnconfirmed ? "unconfirmed" : installed ? "active" : "native";

        internal static void Install()
        {
            try
            {
                Unload();
                if (cleanupUnconfirmed) return;
                var controller = typeof(NanobotSys).Assembly.GetType("Game.Systems.Nanobots.NanobotHaulingController", false);
                var target = controller == null ? null : AccessTools.DeclaredMethod(controller, "AddStorageSources",
                    new[] { typeof(MatStorageComp), typeof(int), typeof(int) });
                if (target == null || target.IsStatic || target.ReturnType != typeof(void))
                    throw new MissingMethodException("Native material source indexing contract");
                harmony = new Harmony(Owner);
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(RelayMaterialSources), nameof(Before)) { priority = HarmonyLib.Priority.First },
                    finalizer: new HarmonyMethod(typeof(RelayMaterialSources), nameof(After)) { priority = HarmonyLib.Priority.Last });
                installed = true;
            }
            catch (Exception)
            {
                Unload();
                if (!cleanupUnconfirmed) Report("Quantum Storage: local Relay material source view unavailable; original native source indexing retained.");
            }
        }

        private static void Before(MatStorageComp storage, out EndpointReadScope<MatStorageComp>.State __state)
        {
            __state = default;
            try { __state = EndpointReadScope<MatStorageComp>.Enter(StorageRelaySys.IsRelay(storage) ? storage : null); }
            catch (Exception) { }
        }

        private static Exception After(Exception __exception, EndpointReadScope<MatStorageComp>.State __state)
        {
            EndpointReadScope<MatStorageComp>.Exit(__state);
            return __exception;
        }

        internal static void Unload()
        {
            try
            {
                harmony?.UnpatchAll(Owner);
                harmony = null; installed = cleanupUnconfirmed = false;
            }
            catch (Exception)
            {
                cleanupUnconfirmed = true;
                Report("Quantum Storage: material source view cleanup unconfirmed; a fresh process is required for a clean source-indexing state.");
            }
            finally { EndpointReadScope<MatStorageComp>.Reset(); }
        }
        private static void Report(string line) { try { D.Warn(line); } catch (Exception) { } }
    }
}
