using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AircraftDamage
    {
        // CE can leave fractional HP in parts its hit selection already treats as dead.
        // The observed Mosquito plateau was 1.54% above VF's 1% destruction cutoff.
        private const float TerminalIntegrity = .02f;

        internal struct HitState
        {
            internal Pawn Aircraft;
            internal float Before;
            internal DamageInfo Incoming;
            internal bool CriticalBefore;
        }

        internal static void Install(Harmony harmony)
        {
            var stats = AccessTools.TypeByName("Vehicles.VehicleStatHandler")
                ?? throw new TypeLoadException("Vehicles.VehicleStatHandler");
            var apply = AccessTools.DeclaredMethod(stats, "ApplyDamage", new[] { typeof(DamageInfo), typeof(IntVec2) })
                ?? throw new MissingMethodException(stats.FullName, "ApplyDamage");
            harmony.Patch(apply,
                prefix: new HarmonyMethod(typeof(AircraftDamage), nameof(BeforeApply)) { priority = Priority.First + 200 },
                postfix: new HarmonyMethod(typeof(AircraftDamage), nameof(AfterApply)) { priority = Priority.Last });
            VehicleEmp.Install(harmony, stats);
            Log.Message("[FpsBridge] AIRCRAFT_DAMAGE READY; native armor; terminal component integrity <=2%");
        }

        internal static (float health, float maximum) ComponentHealth(Pawn aircraft)
        {
            float health = 0f, maximum = 0f;
            object stats = VehicleApi.Get(aircraft, "statHandler");
            if (stats == null) return (health, maximum);
            foreach (object part in VehicleApi.List(stats, "components"))
            {
                health += VehicleApi.Float(part, "Health");
                maximum += VehicleApi.Float(part, "MaxHealth");
            }
            return (health, maximum);
        }

        public static void BeforeApply(object __instance, DamageInfo dinfo, out HitState __state)
        {
            __state = default;
            if (!DebugSettings.enableDamage || dinfo.Amount <= 0f || dinfo.Def?.harmsHealth != true && dinfo.Def != DamageDefOf.EMP) return;
            if (VehicleApi.Get(__instance, "vehicle") is not Pawn { Spawned: true, Dead: false } aircraft
                || !AerialFlight.Supports(aircraft)
                || !DebugSettings.enablePlayerDamage && aircraft.Faction == Faction.OfPlayer) return;
            var before = ComponentHealth(aircraft);
            if (before.maximum > 0f)
                __state = new HitState { Aircraft = aircraft, Before = before.health, Incoming = dinfo,
                    CriticalBefore = AircraftSystems.Flight(aircraft).critical };
        }

        public static void AfterApply(HitState __state)
        {
            Pawn aircraft = __state.Aircraft;
            if (aircraft == null || !aircraft.Spawned || aircraft.Dead || aircraft.Destroyed) return;
            var after = ComponentHealth(aircraft);
            bool airborne = AerialFlight.Airborne(aircraft);
            bool failedSystem = airborne && !__state.CriticalBefore && AircraftSystems.Flight(aircraft).critical;
            if (!(after.health < __state.Before) || !(after.maximum > 0f)
                || !failedSystem && after.health > after.maximum * TerminalIntegrity) return;
            Log.Message($"[FpsBridge] AIRCRAFT_TERMINAL aircraft={aircraft.ThingID}; airborne={airborne}; "
                + $"criticalSystem={failedSystem}; componentHp={__state.Before:0.###}->{after.health:0.###}/{after.maximum:0.###}");
            // Strike's existing kill interception owns the crash, wreck and evacuation.
            aircraft.Kill(__state.Incoming);
        }
    }
}
