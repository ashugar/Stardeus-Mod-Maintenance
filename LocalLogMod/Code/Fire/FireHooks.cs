using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.Firex;
using HarmonyLib;
using KL.Grid;
using LocalLogMod.Logging;

namespace LocalLogMod.FireLogging {
    [HarmonyPatch]
    internal static class FireHooks {
        private sealed class DamageContext {
            internal float Amount;
            internal float HealthBefore;
            internal DamageType Type;
        }

        [ThreadStatic] private static DamageContext activeDamage;
        [ThreadStatic] private static Entity activeSpreadSource;

        [HarmonyPatch(typeof(FlammableComp), nameof(FlammableComp.ReactToDamage))]
        [HarmonyPrefix]
        private static void BeforeDamage(float dmgAmt, float curHealth, DamageType type,
                out DamageContext __state) {
            __state = activeDamage;
            activeDamage = new DamageContext {
                Amount = dmgAmt,
                HealthBefore = curHealth,
                Type = type
            };
        }

        [HarmonyPatch(typeof(FlammableComp), nameof(FlammableComp.ReactToDamage))]
        [HarmonyPostfix]
        private static void AfterDamage(DamageContext __state) {
            activeDamage = __state;
        }

        [HarmonyPatch(typeof(FirexSys), "TrySpreadFire")]
        [HarmonyPrefix]
        private static void BeforeSpread(FirexSys __instance, Fire fire, out Entity __state) {
            __state = activeSpreadSource;
            activeSpreadSource = fire.EntityId == 0
                ? null
                : __instance?.S?.FindEntity<Entity>(fire.EntityId);
        }

        [HarmonyPatch(typeof(FirexSys), "TrySpreadFire")]
        [HarmonyPostfix]
        private static void AfterSpread(Entity __state) {
            activeSpreadSource = __state;
        }

        [HarmonyPatch(typeof(FlammableComp), nameof(FlammableComp.Update))]
        [HarmonyPrefix]
        private static void BeforeUpdate(FlammableComp __instance, out bool __state) {
            __state = __instance.IsOnFire;
        }

        [HarmonyPatch(typeof(FlammableComp), nameof(FlammableComp.Update))]
        [HarmonyPostfix]
        private static void AfterUpdate(FlammableComp __instance, bool __state) {
            bool isOnFire = __instance.IsOnFire;
            if (__state == isOnFire) { return; }
            string caller = CallerChain();
            if (isOnFire) {
                Write("ignited", __instance, Cause(caller), caller);
            } else {
                Write("extinguished", __instance, "firefighting_or_fire_decay", caller);
            }
        }

        [HarmonyPatch(typeof(FlammableComp), nameof(FlammableComp.CreateExplosion))]
        [HarmonyPrefix]
        private static void BeforeExplosion(FlammableComp __instance, out bool __state) {
            __state = __instance.HasExploded;
        }

        [HarmonyPatch(typeof(FlammableComp), nameof(FlammableComp.CreateExplosion))]
        [HarmonyPostfix]
        private static void AfterExplosion(FlammableComp __instance, bool destroySelf, bool __state) {
            if (__state) { return; }
            Write("explosion_created", __instance, "explosion", CallerChain(),
                " destroySelf=" + destroySelf);
        }

        internal static void Snapshot(GameState state) {
            if (state?.Components == null) { return; }
            foreach (FlammableComp flam in state.Components.AllByType<FlammableComp>()) {
                if (flam?.IsOnFire == true) {
                    Write("snapshot_on_fire", flam, "restored_or_already_active", "snapshot");
                }
            }
        }

        private static string Cause(string caller) {
            if (activeSpreadSource != null || caller.Contains("TrySpreadFire")) {
                return "spread";
            }
            if (caller.Contains("WithFuseComp.DoMalfunction")) {
                return "device_malfunction";
            }
            if (caller.Contains("ExplosionCell") || caller.Contains("Explosion")) {
                return "explosion";
            }
            if (activeDamage != null || caller.Contains("ReactToDamage")) {
                return "damage";
            }
            if (caller.Contains("ApplyFlammableEffects")) {
                return "temperature_self_combustion";
            }
            if (caller.Contains("StoryEvent")) {
                return "story_event";
            }
            if (caller.Contains("TransferFireOnRemove")) {
                return "fire_transfer_after_removal";
            }
            if (caller.Contains("AINeedTemperature")) {
                return "being_temperature";
            }
            if (caller.Contains("CmdStartFire") || caller.Contains("SandboxAction")) {
                return "command_or_sandbox";
            }
            return "direct_unknown";
        }

        private static void Write(string evt, FlammableComp flam, string cause, string caller,
                string extra = "") {
            SessionLog.Guard("fire." + evt, () => {
                Entity entity = flam?.Entity;
                GameState state = entity?.S;
                int pos = entity?.PosIdx ?? 0;
                float tempK = state == null ? 0f : state.GetTemp(pos);
                float oxygen = state == null ? 0f : state.GetO2(pos);
                float ignitionK = flam?.Flam.SelfCombustTempKelvin ?? 0f;
                var text = new StringBuilder();
                text.Append("event=").Append(evt)
                    .Append(" cause=").Append(cause)
                    .Append(" entity=").Append(SessionLog.Entity(entity))
                    .Append(" position=").Append(pos == 0 ? "invalid" : Pos.String(pos))
                    .Append(" health=").Append(Format(flam?.Health ?? 0f))
                    .Append(" healthPct=").Append(Format(flam?.ObjectHealth ?? 0f))
                    .Append(" tempK=").Append(Format(tempK))
                    .Append(" tempC=").Append(Format(tempK - 273.15f))
                    .Append(" ignitionK=").Append(Format(ignitionK))
                    .Append(" ignitionC=").Append(Format(ignitionK <= 0f ? 0f : ignitionK - 273.15f))
                    .Append(" oxygen=").Append(Format(oxygen))
                    .Append(" fireChance=").Append(Format(flam?.ChanceToCatchOnFire ?? 0f))
                    .Append(" spreadChance=").Append(Format(flam?.SpreadIntoChance ?? 0f))
                    .Append(" sourceEntity=").Append(SessionLog.Entity(activeSpreadSource));
                if (activeDamage != null) {
                    text.Append(" damageType=").Append(activeDamage.Type)
                        .Append(" damageAmount=").Append(Format(activeDamage.Amount))
                        .Append(" healthBefore=").Append(Format(activeDamage.HealthBefore));
                }
                text.Append(extra).Append(" caller=").Append(caller);
                SessionLog.Write("Fire", text.ToString());
            });
        }

        private static string CallerChain() {
            var text = new StringBuilder();
            StackFrame[] frames = new StackTrace(2, false).GetFrames();
            if (frames == null) { return "unknown"; }
            int added = 0;
            foreach (StackFrame frame in frames) {
                MethodBase method = frame.GetMethod();
                Type type = method?.DeclaringType;
                string typeName = type?.FullName ?? string.Empty;
                if (typeName.StartsWith("LocalLogMod.", StringComparison.Ordinal)
                        || typeName.StartsWith("HarmonyLib.", StringComparison.Ordinal)
                        || typeName.StartsWith("System.", StringComparison.Ordinal)
                        || typeName.StartsWith("MonoMod.", StringComparison.Ordinal)
                        || typeName.Contains("DMD<")
                        || method.Name.IndexOf("_Patch", StringComparison.Ordinal) >= 0) { continue; }
                if (added++ > 0) { text.Append('>'); }
                text.Append(typeName).Append('.').Append(method.Name);
                if (added == 10) { break; }
            }
            return text.Length == 0 ? "unknown" : text.ToString();
        }

        private static string Format(float value) {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
