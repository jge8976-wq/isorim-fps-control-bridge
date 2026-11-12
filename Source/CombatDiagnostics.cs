using HarmonyLib;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class CombatDiagnostics
    {
        private static float nextDamage, nextLaunch, nextContact;

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Thing), nameof(Thing.TakeDamage), new[] { typeof(DamageInfo) }),
                prefix: new HarmonyMethod(typeof(CombatDiagnostics), nameof(BeforeDamage)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(CombatDiagnostics), nameof(AfterDamage)));
        }

        private static bool IsObserved(Thing source) => source != null
            && (source == Bridge.ShootingPawn || source is Pawn v && VehicleControl.Owns(v) || CeCompat.InFpsImpact);

        public static void BeforeDamage(Thing __instance, DamageInfo dinfo, out float? __state)
        {
            __state = null;
            if (Time.unscaledTime >= nextDamage && IsObserved(dinfo.Instigator)
                && __instance is Pawn vehicle && VehicleApi.IsVehicle(vehicle))
                __state = AircraftDamage.ComponentHealth(vehicle).health;
        }

        public static void AfterDamage(Thing __instance, DamageInfo dinfo, DamageWorker.DamageResult __result, float? __state)
        {
            Thing source = dinfo.Instigator;
            if (__result == null || Time.unscaledTime < nextDamage || !IsObserved(source)) return;
            nextDamage = Time.unscaledTime + .25f;
            float dealt = __result.totalDamageDealt;
            string components = "";
            if (__state.HasValue && __instance is Pawn vehicle)
            {
                var after = AircraftDamage.ComponentHealth(vehicle);
                // VF absorbs the original Thing hit after applying native component damage.
                dealt = Mathf.Max(0f, __state.Value - after.health);
                components = $"; componentHp={__state.Value:0.###}->{after.health:0.###}/{after.maximum:0.###}"
                    + $"; nativeDealt={__result.totalDamageDealt:0.###}; destroyed={vehicle.Destroyed}";
            }
            Log.Message("[FpsBridge] DAMAGE_RESULT source=" + source.ThingID + "; target=" + __instance.ThingID
                + "; damage=" + dinfo.Def?.defName + "; incoming=" + dinfo.Amount.ToString("0.##")
                + "; dealt=" + dealt.ToString("0.###") + "; enableDamage=" + DebugSettings.enableDamage
                + "; enablePlayerDamage=" + DebugSettings.enablePlayerDamage + components);
        }

        internal static void CeContact(Thing projectile, Thing source, Thing target)
        {
            if (Time.unscaledTime < nextContact) return;
            nextContact = Time.unscaledTime + .25f;
            Log.Message("[FpsBridge] CE_CONTACT projectile=" + projectile.def.defName + "; source=" + source?.ThingID
                + "; target=" + (target?.ThingID ?? "ground-or-roof") + "; launched=" + CeCompat.FpsLaunched
                + "; moved=" + CeCompat.FpsMoved + "; drawn=" + CeCompat.FpsDraws + "; contacts=" + CeCompat.FpsContacts);
        }

        internal static void CeLaunch(Thing projectile, Thing source, V3 start)
        {
            if (Time.unscaledTime < nextLaunch) return;
            nextLaunch = Time.unscaledTime + .25f;
            Log.Message("[FpsBridge] CE_LAUNCH projectile=" + projectile.def.defName + "; source=" + source?.ThingID
                + $"; origin=({start.X:F2},{start.Y:F2},{start.Z:F2}); tick={Find.TickManager.TicksGame}");
        }
    }
}
