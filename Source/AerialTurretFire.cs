
using System;
using System.Collections.Generic;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialTurretFire
    {
        private sealed record Flash(Map Map, V3 Position, V3 Direction, int Tick);
        private static readonly Dictionary<Building_Turret, Flash> flashes = new();
        [ThreadStatic] private static Building_Turret bursting;

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Verb), "TryCastNextBurstShot"),
                prefix: new HarmonyMethod(typeof(AerialTurretFire), nameof(BeginBurst)),
                finalizer: new HarmonyMethod(typeof(AerialTurretFire), nameof(EndBurst)));
            var fleck = AccessTools.Method(typeof(FleckManager), "CreateFleck", new[] { typeof(FleckCreationData) });
            if (fleck != null)
                harmony.Patch(fleck, prefix: new HarmonyMethod(typeof(AerialTurretFire), nameof(KeepNativeFlash)));
        }

        private static void BeginBurst(Verb __instance, out Building_Turret __state)
        {
            __state = bursting;
            bursting = __instance.Caster as Building_Turret;
        }

        private static void EndBurst(Building_Turret __state) => bursting = __state;

        private static bool KeepNativeFlash(FleckCreationData __0)
        {
            if (!View3D.Active || bursting == null || __0.def != FleckDefOf.ShotFlash) return true;
            return !flashes.TryGetValue(bursting, out var flash) || flash.Tick != Find.TickManager.TicksGame;
        }

        internal static V3 Muzzle(Building_Turret turret, V3 bore)
        {
            Vector3 heading = VehicleModels.U(bore).normalized;
            float yaw = Mathf.Atan2(heading.x, heading.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Asin(heading.y) * Mathf.Rad2Deg;
            var geometry = turret.def.GetModExtension<ImportedTurretGeometry>();
            if (geometry != null)
            {
                var local = geometry.pitchPivot + Quaternion.Euler(pitch, 0, 0) * (geometry.muzzle - geometry.pitchPivot);
                return FloorSpace.Spot(turret) + VehicleModels.V(Quaternion.Euler(0, yaw, 0) * local + heading * .04f);
            }
            float barrel = Mathf.Clamp(turret.def.building.turretTopDrawSize * .36f, .35f, 2.4f);
            return VehicleModels.V(AerialCombat.Origin(turret) + heading * barrel);
        }

        internal static void Record(Building_Turret turret, V3 position, V3 velocity)
        {
            if (turret == null || !position.Finite || !velocity.Finite || velocity.Length < .001f) return;
            flashes[turret] = new Flash(turret.Map, position, velocity, Find.TickManager.TicksGame);
            AerialTurretPitch.Record(turret, velocity);
        }

        internal static void Draw(Map map)
        {
            if (!View3D.Active || flashes.Count == 0 || map == null) return;
            int now = Find.TickManager.TicksGame;
            foreach (var (turret, flash) in flashes)
            {
                int age = now - flash.Tick;
                if (age < 0 || age >= 5 || !turret.Spawned)
                {
                    flashes.Remove(turret);
                    continue;
                }
                if (flash.Map != map || turret.Position.Fogged(map)) continue;
                Visuals.GunFlash(flash.Position, flash.Direction, 1f - age / 5f);
            }
        }
    }
}

