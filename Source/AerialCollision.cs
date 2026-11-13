using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialCollision
    {
        internal static bool LandingClear(Pawn v,AerialMotor motor)
        {
            if(!Clear(v,motor))return false;
            Bounds b=AircraftFootprint.BoundsAt(v,motor);
            foreach(Pawn p in v.Map.mapPawns.AllPawnsSpawned)
            {
                if(p==v||VehicleApi.IsVehicle(p))continue;
                V3 at=FloorSpace.Spot(p);
                if(at.Y>b.max.y+.1f||at.Y+1.8f<b.min.y)continue;
                if(at.X>b.min.x-.3f&&at.X<b.max.x+.3f&&at.Z>b.min.z-.3f&&at.Z<b.max.z+.3f)return false;
            }
            return true;
        }
        private static readonly HashSet<Thing> seen = new HashSet<Thing>();
        private static Func<IntVec3, int, Map, bool> solidPlane;
        private static bool bound;
        private static bool Plane(Map map, int x, int z, FloorBand band)
        {
            // ProjectZ's PlaneSolid describes slabs/ceilings, not the terrain at the bottom of the map.
            if (!FloorSpace.Bands(map).Any(b => b.Level < band.Level)) return true;
            if (!bound)
            {
                bound = true;
                var method = AccessTools.Method(AccessTools.TypeByName("ProjectZ.Api"), "PlaneSolid", new[] { typeof(IntVec3), typeof(int), typeof(Map) });
                if (method != null) solidPlane = method.CreateDelegate<Func<IntVec3, int, Map, bool>>();
            }
            if (solidPlane != null) return solidPlane(new IntVec3(x, 0, z), band.Level, map);
            return band.Level == 0;
        }
        // A platform supports a landing only when every covered column has a surface at the same height.
        internal static float Support(Pawn v, Bounds shape, float below)
        {
            Map map = v.Map;
            float support = float.NegativeInfinity;
            foreach (FloorBand band in FloorSpace.Bands(map))
            {
                if (band.Height <= below && band.Height > support && AllSurface(map, shape, band.Height, band, false)) support = band.Height;
                float roof = band.Height + Api.WallHeight;
                if (roof <= below && roof > support && AllSurface(map, shape, roof, band, true)) support = roof;
            }
            return support;
        }
        // AGL is a downward point measurement, not proof that a whole runway can support this aircraft.
        // Water, a map-edge wing overhang or a gap under one wing must not turn the altimeter into infinity.
        internal static float Altitude(Pawn v,Bounds box)
        {
            Map map=v.Map;float x=box.center.x,z=box.center.z;
            if(!float.IsFinite(x)||!float.IsFinite(z)||x<0||z<0||x>=map.Size.x||z>=FloorSpace.Bands(map)[0].Rows)return float.NaN;
            float floor=float.NegativeInfinity;
            foreach(FloorBand band in FloorSpace.Bands(map))
            {
                IntVec3 c=new IntVec3(Mathf.FloorToInt(x),0,Mathf.FloorToInt(z)+band.Shift);
                if(!c.InBounds(map))continue;
                if(band.Height<=box.min.y+.06f&&band.Height>floor&&Plane(map,c.x,Mathf.FloorToInt(z),band))floor=band.Height;
                float roof=band.Height+Api.WallHeight;
                if(roof<=box.min.y+.06f&&roof>floor&&map.roofGrid.RoofAt(c)!=null)floor=roof;
            }
            return float.IsFinite(floor)?Math.Max(0,box.min.y-floor):float.NaN;
        }
        // Sweep the descending hull base against occupied floor columns. A
        // partial platform can stop a falling aircraft even when it cannot land there.
        internal static IntVec3 CrashContact(Pawn vehicle, Vector3 next, float pitch)
        {
            if (!AerialFlight.TryGet(vehicle, out var flight)) return IntVec3.Invalid;
            var before = flight.Motor;
            var after = before;
            after.Position = VehicleModels.V(next);
            after.Pitch = pitch;
            Bounds from = AircraftFootprint.BoundsAt(vehicle, before);
            Bounds to = AircraftFootprint.BoundsAt(vehicle, after);
            Map map = vehicle.Map;
            IntVec3 best = IntVec3.Invalid;
            float highest = float.NegativeInfinity;
            foreach (var band in FloorSpace.Bands(map))
            {
                if (band.Height <= highest || band.Height > from.min.y + .05f || band.Height < to.min.y) continue;
                float step = Mathf.InverseLerp(from.min.y, to.min.y, band.Height);
                Vector3 center = Vector3.Lerp(from.center, to.center, step);
                Vector3 half = Vector3.Lerp(from.extents, to.extents, step);
                float closest = float.PositiveInfinity;
                for (int x = Mathf.FloorToInt(center.x - half.x + .02f); x <= Mathf.FloorToInt(center.x + half.x - .02f); x++)
                for (int z = Mathf.FloorToInt(center.z - half.z + .02f); z <= Mathf.FloorToInt(center.z + half.z - .02f); z++)
                {
                    var cell = new IntVec3(x, 0, z + band.Shift);
                    if (z < 0 || z >= band.Rows || !cell.InBounds(map) || !Plane(map, x, z, band)
                        || band.Level != 0 && !FloorSpace.HasFloorAt(map, cell)) continue;
                    float distance = (x + .5f - center.x) * (x + .5f - center.x) + (z + .5f - center.z) * (z + .5f - center.z);
                    if (distance >= closest) continue;
                    closest = distance;
                    highest = band.Height;
                    best = cell;
                }
            }
            return best;
        }

        private static bool AllSurface(Map map, Bounds box, float height, FloorBand band, bool roof)
        {
            for (int x = Mathf.FloorToInt(box.min.x + .02f); x <= Mathf.FloorToInt(box.max.x - .02f); x++)
                for (int z = Mathf.FloorToInt(box.min.z + .02f); z <= Mathf.FloorToInt(box.max.z - .02f); z++)
                {
                    IntVec3 c = new IntVec3(x, 0, z + band.Shift);
                    if (!c.InBounds(map) || z < 0 || z >= band.Rows) return false;
                    if (roof) { if (map.roofGrid.RoofAt(c) == null) return false; }
                    else if (!Plane(map, x, z, band)) return false;
                    if (!roof && band.Level == 0 && c.GetTerrain(map).IsWater) return false;
                }
            return true;
        }
        internal static bool Clear(Pawn v,AerialMotor motor)=>ClearOn(v,motor,v.Map);
        internal static bool ClearOn(Pawn v,AerialMotor motor,Map map)=>ClearEnvironment(v,motor,map)&&VehicleContacts.Clear(v,motor,map);
        internal static bool ClearEnvironment(Pawn v, AerialMotor motor,Map map)
        {
            Bounds box = AerialFlight.Supports(v) ? AircraftFootprint.BoundsAt(v, motor) : AerialGeometry.BoundsAt(v, motor);
            var bands = FloorSpace.Bands(map);
            float margin = AerialFlight.Supports(v) ? 0 : .02f;
            if (box.min.x < margin || box.min.z < margin || box.max.x > map.Size.x - margin || box.max.z > bands[0].Rows - margin) return false;
            seen.Clear();
            foreach (FloorBand band in bands)
            {
                for (int x = Mathf.FloorToInt(box.min.x + .005f); x <= Mathf.FloorToInt(box.max.x - .005f); x++)
                    for (int z = Mathf.FloorToInt(box.min.z + .005f); z <= Mathf.FloorToInt(box.max.z - .005f); z++)
                    {
                        IntVec3 cell = new IntVec3(x, 0, z + band.Shift);
                        if (!cell.InBounds(map)) continue;
                        if (box.min.y < band.Height - .002f && box.max.y > band.Height + .002f && Plane(map, x, z, band)) return false;
                        float roof = band.Height + Api.WallHeight;
                        if (box.min.y < roof - .002f && box.max.y > roof + .002f && map.roofGrid.RoofAt(cell) != null) return false;
                        foreach (Thing thing in cell.GetThingList(map))
                        {
                            if (thing == v || !thing.Spawned || thing.Destroyed || !seen.Add(thing)) continue;
                            if (thing is Pawn p)
                            {
                                if (!VehicleApi.IsVehicle(p)) continue;
                                continue;
                            }
                            if(thing is Plant tree&&tree.def.plant.IsTree)
                            {
                                var trunk=new Bounds(new Vector3(cell.x+.5f,band.Height+Api.WallHeight*1.5f,cell.z-band.Shift+.5f),new Vector3(.65f,Api.WallHeight*3f,.65f));
                                if(box.Intersects(trunk))return false;
                            }
                            if (!(thing is Building) || thing.def.fillPercent < .1f || thing is Building_Door door && door.Open) continue;
                            CellRect area = thing.OccupiedRect();
                            float height = Api.WallHeight * Math.Max(.1f, thing.def.fillPercent);
                            var obstacle = new Bounds(new Vector3(area.minX + area.Width * .5f, band.Height + height * .5f, area.minZ - band.Shift + area.Height * .5f),
                                new Vector3(area.Width - .01f, height, area.Height - .01f));
                            if (box.Intersects(obstacle)) return false;
                        }
                    }
            }
            return true;
        }
    }
}
