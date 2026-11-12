using System;
using System.Collections.Generic;
using IsoRim;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialRangeOverlay
    {
        internal sealed record Entry(object Gun, float Maximum, bool DropOnly, Color Color);
        private static readonly Color[] colors =
        {
            new(.30f, .85f, 1f, .72f), new(1f, .72f, .25f, .72f), new(.51f, 1f, .43f, .72f),
            new(1f, .41f, .64f, .72f), new(.72f, .62f, 1f, .72f), new(.99f, .53f, .32f, .72f)
        };
        private static Pawn selected;
        private static IReadOnlyList<Entry> entries = Array.Empty<Entry>();
        private static int refreshedAt;
        internal static int DrawCount;

        internal static List<Entry> Weapons(Pawn aircraft)
        {
            var result = new List<Entry>();
            foreach (var gun in VehicleApi.Turrets(aircraft))
            {
                float maximum = AerialArmament.AiRange(gun);
                if (!float.IsFinite(maximum) || maximum <= 0f) continue;
                result.Add(new Entry(gun, maximum, AerialArmament.Bomb(gun), colors[result.Count % colors.Length]));
            }
            return result;
        }

        internal static void Draw()
        {
            Pawn aircraft = Find.Selector?.SingleSelectedThing as Pawn;
            if (!FpsMod.Settings.showAircraftRanges || !View3D.Active || WorldRendererUtility.WorldSelected || FirstPerson.On || aircraft?.Spawned != true
                || aircraft.Map != Find.CurrentMap || !VehicleApi.IsVehicle(aircraft)
                || VehicleApi.Get(aircraft.def, "type")?.ToString() != "Air" || aircraft.Position.Fogged(aircraft.Map))
            {
                selected = null;
                return;
            }
            if (selected != aircraft || Time.frameCount >= refreshedAt + 15)
            {
                selected = aircraft;
                entries = Weapons(aircraft);
                refreshedAt = Time.frameCount;
            }
            foreach (var entry in entries)
            {
                Vector3 muzzle = AerialFlight.Supports(aircraft) ? VehicleModels.Muzzle(aircraft, entry.Gun)
                    : VehicleModels.U(FloorSpace.Spot(aircraft)) + Vector3.up;
                if (entry.DropOnly)
                {
                    ThingDef shell = VehicleWeapons.ProjectileFor(entry.Gun);
                    if (AerialFlight.Airborne(aircraft) && shell?.projectile != null
                        && AerialWeaponSolution.TryDropImpact(aircraft, entry.Gun, shell, VehicleModels.V(muzzle), out V3 impact)
                        && Visuals.AircraftRangeSphere(VehicleModels.U(impact), Math.Max(.8f, CeCompat.VehicleBlastRadius(shell)), entry.Color))
                        DrawCount++;
                }
                bool drawn = AerialFlight.Airborne(aircraft)&&AerialAiAim.FixedWing(aircraft)
                    ? Visuals.AircraftFiringSector(muzzle,AerialAiAim.Forward(aircraft),entry.Maximum)
                    : Visuals.AircraftRangeSphere(muzzle, entry.Maximum, entry.Color);
                if(drawn) DrawCount++;
            }
        }
    }
}
