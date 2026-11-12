using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HSK.FpsBridge
{
    internal static class VehicleCrewOwnership
    {
        [ThreadStatic] private static int crewTickDepth;
        [ThreadStatic] private static HashSet<Pawn> deferred;

        internal static void Install(Harmony harmony)
        {
            var kill = AccessTools.DeclaredMethod(typeof(Pawn), nameof(Pawn.Kill), new[] { typeof(DamageInfo?), typeof(Hediff) })
                ?? throw new MissingMethodException(typeof(Pawn).FullName, nameof(Pawn.Kill));
            harmony.Patch(kill,
                prefix: new HarmonyMethod(typeof(VehicleCrewOwnership), nameof(BeforeKill)) { priority = Priority.First + 100 },
                postfix: new HarmonyMethod(typeof(VehicleCrewOwnership), nameof(AfterKill)) { priority = Priority.Last });

            var health = typeof(Pawn_HealthTracker);
            var parameters = new[] { typeof(DamageInfo?), typeof(Hediff) };
            var afterHealth = new HarmonyMethod(typeof(VehicleCrewOwnership), nameof(AfterHealth)) { priority = Priority.Last };
            harmony.Patch(AccessTools.DeclaredMethod(health, "CheckForStateChange", parameters), postfix: afterHealth);
            harmony.Patch(AccessTools.DeclaredMethod(health, "MakeDowned", parameters), postfix: afterHealth);
            harmony.Patch(AccessTools.DeclaredMethod(VehicleApi.PawnType, "TickHandlers"),
                prefix: new HarmonyMethod(typeof(VehicleCrewOwnership), nameof(BeginCrewTick)),
                finalizer: new HarmonyMethod(typeof(VehicleCrewOwnership), nameof(EndCrewTick)));
        }

        // Vehicle Framework moves a dying pawn out of its handler during Pawn.Kill.
        private static void BeforeKill(Pawn __instance, out Pawn __state) =>
            __state = __instance.Dead ? null : HostileVehicle(__instance);

        private static void AfterKill(Pawn __instance, Pawn __state)
        {
            if (__instance.Dead) QueueOrResolve(__state);
        }

        private static void AfterHealth(Pawn ___pawn)
        {
            if (HostileVehicle(___pawn) is Pawn vehicle && Incapacitated(___pawn)) QueueOrResolve(vehicle);
        }

        private static Pawn HostileVehicle(Pawn pawn)
        {
            var handler = pawn?.ParentHolder;
            return VehicleApi.RoleType.IsInstanceOfType(handler) && handler.ParentHolder is Pawn vehicle && Hostile(vehicle) ? vehicle : null;
        }

        private static bool Hostile(Pawn vehicle) => vehicle is { Spawned: true, Dead: false, Destroyed: false }
            && vehicle.Faction?.HostileTo(Faction.OfPlayer) == true;

        private static bool Incapacitated(Pawn pawn) => pawn.Dead || pawn.Downed
            || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving)
            || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Consciousness);

        private static void BeginCrewTick() => crewTickDepth++;

        private static void EndCrewTick(Pawn __instance)
        {
            if (--crewTickDepth != 0) return;
            if (deferred != null)
            {
                var ready = deferred;
                deferred = null;
                foreach (Pawn vehicle in ready) Resolve(vehicle);
            }
            if (__instance.IsHashIntervalTick(30) && Hostile(__instance) && !AerialFlight.Airborne(__instance)
                && (VehicleApi.OutOfFuel(__instance) || (__instance.stances?.stunner?.Stunned != true && VehicleApi.ChassisDisabled(__instance))))
                AbandonOnSurface(__instance);
        }

        private static void QueueOrResolve(Pawn vehicle)
        {
            if (vehicle == null) return;
            // Ejection changes the occupied-handler list that VF is iterating.
            if (crewTickDepth > 0) (deferred ??= new HashSet<Pawn>()).Add(vehicle);
            else Resolve(vehicle);
        }

        private static void Resolve(Pawn vehicle)
        {
            if (!Hostile(vehicle)) return;
            var crew = Crew(vehicle);
            if (crew.Any(pawn => !Incapacitated(pawn))) return;
            Release(vehicle, crew);
        }

        internal static void AbandonOnSurface(Pawn vehicle)
        {
            if (!Hostile(vehicle) || AerialFlight.Airborne(vehicle) || AerialCrew.LandingWithoutPilot(vehicle)) return;
            var crew = Crew(vehicle);
            if (crew.Any(pawn => !pawn.Dead)) Release(vehicle, crew);
        }

        private static Pawn[] Crew(Pawn vehicle) => VehicleApi.Handlers(vehicle)
            .SelectMany(handler => VehicleApi.List(handler, "thingOwner").OfType<Pawn>()).ToArray();

        private static void Release(Pawn vehicle, Pawn[] crew)
        {
            vehicle.SetFaction(null);
            if (AerialFlight.TryGet(vehicle, out var flight) && flight.Airborne)
            {
                flight.EnemyPilotLoss = true;
                flight.Navigate = flight.Orbit = flight.Cruise = flight.CombatEnabled = flight.BoostRequested = false;
            }
            else
            {
                vehicle.jobs?.StopAll();
                VehicleApi.Stop(vehicle);
                if (VehicleApi.Drafted(vehicle)) VehicleApi.Draft(vehicle, false);
            }
            foreach (var gun in VehicleApi.Turrets(vehicle))
            {
                VehicleApi.Set(gun, "AutoTarget", false);
                VehicleApi.Call(gun, "SetTarget", LocalTargetInfo.Invalid);
            }
            foreach (Pawn pawn in crew)
                if (!pawn.Dead && pawn.ParentHolder is IThingHolder handler && handler.ParentHolder == vehicle)
                    VehicleApi.Call(vehicle, "DisembarkPawn", pawn);
        }
    }
}

