using System;
using System.Linq;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialGroundFire
    {
        [ThreadStatic] private static Fire currentFire;
        [ThreadStatic] private static float aircraftFireLift;
        internal static int RejectedDamage, RejectedAttachment;

        private static bool OutOfCombustibleFuel(Pawn vehicle)
        {
            object tank = VehicleApi.Get(vehicle, "CompFueledTravel");
            return tank != null && !VehicleApi.Bool(VehicleApi.Get(tank, "Props"), "ElectricPowered")
                && VehicleApi.Float(tank, "Fuel") <= .0001f;
        }

        private static bool HasFirefoam(Thing vehicle)
        {
            if (!vehicle.Spawned) return false;
            Map map = vehicle.Map;
            foreach (IntVec3 cell in vehicle.OccupiedRect())
            {
                if (!cell.InBounds(map)) continue;
                foreach (Thing thing in cell.GetThingList(map))
                    if (thing.def.filth is { allowsFire: false }) return true;
            }
            return false;
        }

        public static bool BeforeFireCalculations(Fire __instance)
        {
            if (__instance.parent is not Pawn vehicle || !VehicleApi.IsVehicle(vehicle)) return true;
            if (!vehicle.Spawned || vehicle.Destroyed || !AerialFlight.Airborne(vehicle) && HasFirefoam(vehicle))
            {
                __instance.Destroy();
                return false;
            }
            if (OutOfCombustibleFuel(vehicle))
            {
                __instance.fireSize -= .45f;
                if (__instance.fireSize < .1f)
                {
                    __instance.Destroy();
                    return false;
                }
            }
            return true;
        }

        public static bool BeforeVehicleDamage(Pawn __instance, ref DamageInfo dinfo, ref bool absorbed)
        {
            if (dinfo.Def != DamageDefOf.Extinguish) return true;
            if (__instance.GetAttachment(ThingDefOf.Fire) is Fire { Destroyed: false } fire)
                fire.TakeDamage(dinfo);
            absorbed = true;
            return false;
        }

        internal static bool Reaches(Fire source, Thing target)
        {
            if (source.parent != null || target is not Pawn aircraft
                || !AerialFlight.Supports(aircraft) || !AerialFlight.TryGet(aircraft, out var flight)
                || !flight.Airborne || source.Map != aircraft.Map) return true;

            FloorBand floor = FloorSpace.Band(source.Map, source.Position);
            // A ground fire occupies a column above its own floor. Scale its
            // contact height with native fireSize and the existing world wall unit.
            float flameTop = floor.Height + Mathf.Clamp(source.fireSize, .1f, 1.75f) * Api.WallHeight;
            Bounds hull = AircraftFootprint.BoundsAt(aircraft, flight.Motor);
            return hull.min.y <= flameTop + .01f && hull.max.y >= floor.Height - .01f;
        }

        public static void Enter(Fire __instance, out Fire __state)
        {
            __state = currentFire;
            currentFire = __instance;
        }

        public static void Exit(Fire __state) => currentFire = __state;

        public static bool BeforeDamage(Fire __instance, Thing targ)
        {
            if (!Reaches(__instance, targ))
            {
                RejectedDamage++;
                return false;
            }
            if (targ is not Pawn vehicle || !VehicleApi.IsVehicle(vehicle)) return true;
            DamageComponent(vehicle, Math.Max(1, GenMath.RoundRandom((.0125f + .0036f * __instance.fireSize) * 150f)));
            return false;
        }

        private static void DamageComponent(Pawn vehicle, float amount)
        {
            object stats = VehicleApi.Get(vehicle, "statHandler");
            var surviving = VehicleApi.List(stats, "components").Where(part => VehicleApi.Float(part, "Health") > 0f);
            object part = surviving.FirstOrDefault(candidate =>
                string.Equals(VehicleApi.Get(VehicleApi.Get(candidate, "props"), "depth")?.ToString(), "External", StringComparison.Ordinal))
                ?? surviving.FirstOrDefault();
            if (part == null) return;
            VehicleApi.Call(part, "SetHealth", VehicleApi.Float(part, "Health") - amount);
            VehicleApi.Call(stats, "MarkAllDirty");
        }

        public static bool BeforeAttachment(Thing t)
        {
            if (t is Pawn vehicle && VehicleApi.IsVehicle(vehicle)
                && (!AerialFlight.Airborne(vehicle) && HasFirefoam(vehicle)
                    || currentFire != null && OutOfCombustibleFuel(vehicle))) return false;
            if (currentFire == null || Reaches(currentFire, t)) return true;
            RejectedAttachment++;
            return false;
        }

        public static void EnterFireDraw(Thing __0, out float __state)
        {
            __state = aircraftFireLift;
            aircraftFireLift = 0f;
            if (__0 is Fire { parent: Pawn aircraft } && aircraft.Spawned
                && AerialFlight.TryGet(aircraft, out var flight) && flight.Airborne)
                aircraftFireLift = AerialFlight.RawRoot(aircraft, flight).y;
        }

        public static void ExitFireDraw(float __state) => aircraftFireLift = __state;

        public static void LiftFireParticles(ref Vector3 __0) => __0.y += aircraftFireLift;

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Fire), "DoComplexCalcs"),
                prefix: new HarmonyMethod(typeof(AerialGroundFire), nameof(BeforeFireCalculations)) { priority = Priority.First + 300 });
            if (VehicleApi.Ready)
                harmony.Patch(AccessTools.DeclaredMethod(VehicleApi.PawnType, nameof(Thing.PreApplyDamage)),
                    prefix: new HarmonyMethod(typeof(AerialGroundFire), nameof(BeforeVehicleDamage)));
            harmony.Patch(AccessTools.Method(typeof(Fire), "DoComplexCalcs"),
                prefix: new HarmonyMethod(typeof(AerialGroundFire), nameof(Enter)),
                finalizer: new HarmonyMethod(typeof(AerialGroundFire), nameof(Exit)));
            harmony.Patch(AccessTools.Method(typeof(Fire), "DoFireDamage"),
                prefix: new HarmonyMethod(typeof(AerialGroundFire), nameof(BeforeDamage)) { priority = Priority.First + 200 });
            harmony.Patch(AccessTools.Method(typeof(FireUtility), nameof(FireUtility.TryAttachFire)),
                prefix: new HarmonyMethod(typeof(AerialGroundFire), nameof(BeforeAttachment)) { priority = Priority.First + 200 });
            // IsoRim anchors native fire particles to the parent on its floor. Add the
            // aircraft's height above that floor to this draw, including smoke and sparks.
            var burning = AccessTools.Method(typeof(Effects), "BurningThing", new[] { typeof(Thing), typeof(float) });
            var particles = AccessTools.Method(AccessTools.TypeByName("IsoRim.FireFx"), "Draw");
            if (burning == null || particles?.GetParameters().FirstOrDefault()?.ParameterType != typeof(Vector3)) return;
            harmony.Patch(burning,
                prefix: new HarmonyMethod(typeof(AerialGroundFire), nameof(EnterFireDraw)),
                finalizer: new HarmonyMethod(typeof(AerialGroundFire), nameof(ExitFireDraw)));
            harmony.Patch(particles,
                prefix: new HarmonyMethod(typeof(AerialGroundFire), nameof(LiftFireParticles)));
        }
    }
}
