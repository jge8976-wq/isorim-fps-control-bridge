using System;
using System.Collections.Generic;
using HarmonyLib;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class FloorSpace
    {
        private static readonly List<FloorBand> bands = new List<FloorBand>();
        private static Map cachedMap;
        private static int frame = -1;
        private static bool bound;
        private static Func<Map, int> minimum, maximum;
        private static Func<IntVec3, int, Map, IntVec3> cellOnFloor;
        private static Func<IntVec3, int, Map, bool> planeSolid;
        private static Func<IntVec3, Map, bool> hasFloor;
        private static Func<Projectile, bool> removeNativeFlight, removeNativeCross;
        private static System.Reflection.MethodInfo floorJump;
        public static bool ProjectZReady { get; private set; }

        public static void Bind()
        {
            if (bound) return;
            bound = true;
            Type pz = AccessTools.TypeByName("ProjectZ.ProjectZ");
            if (pz == null) return;
            try
            {
                Type levels = AccessTools.TypeByName("ProjectZ.Levels"), column = AccessTools.TypeByName("ProjectZ.Column");
                minimum = AccessTools.Method(levels, "Min", new[] { typeof(Map) }).CreateDelegate<Func<Map, int>>();
                maximum = AccessTools.Method(levels, "Max", new[] { typeof(Map) }).CreateDelegate<Func<Map, int>>();
                cellOnFloor = AccessTools.Method(pz, "CellOnFloor", new[] { typeof(IntVec3), typeof(int), typeof(Map) }).CreateDelegate<Func<IntVec3, int, Map, IntVec3>>();
                planeSolid = AccessTools.Method(column, "PlaneSolid", new[] { typeof(IntVec3), typeof(int), typeof(Map) }).CreateDelegate<Func<IntVec3, int, Map, bool>>();
                hasFloor = AccessTools.Method(pz, "HasFloor", new[] { typeof(IntVec3), typeof(Map) }).CreateDelegate<Func<IntVec3, Map, bool>>();
                Type flights = AccessTools.TypeByName("ProjectZ.Combat.Flights");
                object nativeFlights = AccessTools.Field(flights, "table").GetValue(null);
                removeNativeFlight = nativeFlights.GetType().GetMethod("Remove", new[] { typeof(Projectile) }).CreateDelegate<Func<Projectile, bool>>(nativeFlights);
                object nativeCross = AccessTools.Field(flights, "cross").GetValue(null);
                removeNativeCross = nativeCross.GetType().GetMethod("Remove", new[] { typeof(Projectile) }).CreateDelegate<Func<Projectile, bool>>(nativeCross);
                // Optional: ProjectZ's own "show the target's floor and centre on where it is drawn" camera jump.
                floorJump = AccessTools.Method(AccessTools.TypeByName("ProjectZ.Patches.FloorJump"), "Apply", new[] { typeof(IntVec3), typeof(Map), typeof(bool) });
                ProjectZReady = true;
                Log.Message("[FpsBridge] PROJECTZ_COMPAT READY; coordinates=world; slabs=Column.PlaneSolid; source=" + pz.Assembly.Location);
            }
            catch (Exception e) { Log.Error("[FpsBridge] ProjectZ API mismatch; compatibility unavailable: " + e); }
        }

        // Leaving the controlled pawn upstairs: IsoRim jumps the camera to the pawn's raw map cell, which for an upper
        // floor is the annex rows north of the map (ProjectZ then clamps it to the top edge). Show the pawn's floor and
        // centre on where it is drawn instead, as ProjectZ does for its own jumps.
        public static void KeepCameraOn(Pawn pawn)
        {
            if (!ProjectZReady || floorJump == null || pawn == null || !pawn.Spawned || pawn.Map != Find.CurrentMap) return;
            floorJump.Invoke(null, new object[] { pawn.Position, pawn.Map, false });
        }

        // ---- sounds upstairs
        // ProjectZ keeps upper floors in annex rows north of the map; a sound made there is placed far from the listener
        // over the ground rows and is not heard (gunfire, impacts, hits upstairs). One-shot sounds made on an upper or
        // lower floor are moved to the ground cell under them, where that floor is drawn. Ground sounds are untouched.
        private static AccessTools.StructFieldRef<Verse.Sound.SoundInfo, TargetInfo> soundMaker;

        public static void InstallSound(Harmony harmony)
        {
            soundMaker = AccessTools.StructFieldRefAccess<Verse.Sound.SoundInfo, TargetInfo>("<Maker>k__BackingField");
            var play = AccessTools.Method(typeof(Verse.Sound.SoundStarter), "PlayOneShot", new[] { typeof(SoundDef), typeof(Verse.Sound.SoundInfo) })
                ?? throw new MissingMethodException("SoundStarter.PlayOneShot");
            harmony.Patch(play, prefix: new HarmonyMethod(typeof(FloorSpace), nameof(SoundOnDrawnFloor)) { priority = Priority.Last });
        }

        // Within this distance of a possessed pawn on an upper/lower floor, sounds are played at the camera.
        private const float HearingRange = 45f;
        private static float nextSoundLog;

        public static void SoundOnDrawnFloor(ref Verse.Sound.SoundInfo info)
        {
            if (!ProjectZReady || soundMaker == null || info.IsOnCamera) return;
            TargetInfo maker = soundMaker(ref info);
            Map map = maker.Map;
            if (map == null || map != Find.CurrentMap || !maker.Cell.IsValid) return;
            IntVec3 cell = maker.Cell;
            if (!cell.InBounds(map)) return;
            FloorBand band = Band(map, cell);
            // A possessed pawn off the ground floor: IsoRim keeps the game's audio listener (the map camera) at the
            // pawn's raw annex cell, clamped to the map's top edge, so neither the raw nor the drawn cell is heard.
            // The game always places a world sound at its maker's cell (no on-camera override), so the maker is moved
            // to the same offset from the listener as the sound has from the pawn in drawn space (floor height folded
            // into the distance): the game's own distance falloff then applies as the player hears it.
            Thing listener = RemoteTurret.On ? TurretControl.Current?.Thing : Bridge.ShootingPawn;
            if (listener != null && listener.Spawned && listener.Map == map)
            {
                FloorBand own = Band(map, listener.Position);
                if (own.Shift != 0)
                {
                    float dx = cell.x - listener.Position.x, dz = (cell.z - band.Shift) - (listener.Position.z - own.Shift), dy = band.Height - own.Height;
                    float flat = Mathf.Sqrt(dx * dx + dz * dz), distance = Mathf.Sqrt(flat * flat + dy * dy);
                    if (distance > HearingRange || Find.Camera == null) return;
                    if (flat > .01f) { dx *= distance / flat; dz *= distance / flat; } else dx = distance;
                    Vector3 ear = Find.Camera.transform.position;
                    IntVec3 at = new IntVec3(Mathf.RoundToInt(ear.x + dx), 0, Mathf.RoundToInt(ear.z + dz)).ClampInsideMap(map);
                    soundMaker(ref info) = new TargetInfo(at, map);
                    if (FpsMod.Settings.verboseLog && Time.unscaledTime >= nextSoundLog)
                    {
                        nextSoundLog = Time.unscaledTime + 2f;
                        Log.Message("[FpsBridge] SOUND_REMAP ear=" + ear.ToString("F1") + "; pawn=" + listener.Position + "; maker=" + cell + " -> " + at + "; distance=" + distance.ToString("F1"));
                    }
                    return;
                }
            }
            if (band.Shift == 0) return;
            IntVec3 ground = new IntVec3(cell.x, 0, cell.z - band.Shift);
            if (!ground.InBounds(map)) return;
            soundMaker(ref info) = new TargetInfo(ground, map);
        }

        public static IReadOnlyList<FloorBand> Bands(Map map)
        {
            Bind();
            if (map == cachedMap && frame == Time.frameCount) return bands;
            cachedMap = map; frame = Time.frameCount; bands.Clear();
            IFloorLayout layout = Api.Floors;
            int rows = layout?.GroundRows(map) ?? 0;
            if (rows <= 0) { bands.Add(new FloorBand(0, 0, map.Size.z, 0f)); return bands; }
            int count = Math.Min(32, layout.FloorCount(map));
            float storey = View3D.WallHeight + .02f;
            if (count > 0) { layout.Floor(map, 1, out float h, out _); storey = h; }
            int lo = ProjectZReady ? Math.Max(-32, minimum(map)) : 0;
            int hi = ProjectZReady ? Math.Min(32, maximum(map)) : count;
            for (int k = lo; k <= hi; k++)
            {
                int shift = 0; float height = k * storey;
                if (k > 0) layout.Floor(map, k, out height, out shift);
                else if (k < 0) { IntVec3 c = cellOnFloor(new IntVec3(0, 0, 0), k, map); if (!c.IsValid) continue; shift = c.z; }
                // Floor()'s bool is VISIBILITY, never physical existence: hidden floors still stop/hit bullets.
                bands.Add(new FloorBand(k, shift, rows, height));
            }
            return bands;
        }

        // The same column one floor up (ProjectZ), Invalid without one.
        public static IntVec3 CellAbove(Map map, IntVec3 c)
        {
            if (!ProjectZReady || cellOnFloor == null) return IntVec3.Invalid;
            FloorBand b = Band(map, c);
            IntVec3 up = cellOnFloor(new IntVec3(c.x, 0, c.z - b.Shift), b.Level + 1, map);
            return up.IsValid && up.InBounds(map) ? up : IntVec3.Invalid;
        }
        public static bool HasFloorAt(Map map, IntVec3 c) => ProjectZReady && hasFloor != null && c.InBounds(map) && hasFloor(c, map);

        public static FloorBand Band(Map map, IntVec3 cell)
        {
            foreach (FloorBand band in Bands(map)) if (band.Contains(cell.z)) return band;
            return bands[0];
        }
        public static Vector3 Offset(Thing thing)
        {
            if (thing?.Map == null) return Vector3.zero;
            FloorBand band = Band(thing.Map, thing.Position);
            Vector3 extra = Api.Floors?.ThingOffset(thing) ?? Floors.ThingOffset(thing);
            return new Vector3(extra.x, band.Height + extra.y, -band.Shift + extra.z);
        }
        public static V3 Spot(Thing thing)
        {
            if (thing is Pawn vehicle && AerialFlight.TryGet(vehicle, out var flight)) return flight.Motor.Position;
            Vector3 p = thing.DrawPos, o = Offset(thing);
            return new V3(p.x + o.x, o.y, p.z + o.z);
        }
        public static bool InWorld(Map map, V3 p)
        { int rows = Bands(map)[0].Rows; return p.X >= 0 && p.X < map.Size.x && p.Z >= 0 && p.Z < rows; }

        public static IntVec3 Cell(Map map, V3 world, Thing surface = null)
        {
            FloorBand chosen = surface != null ? Band(map, surface.Position) : Bands(map)[0];
            if (surface == null)
                foreach (FloorBand band in Bands(map))
                    if (band.Height <= world.Y + .001f && band.Height >= chosen.Height) chosen = band;
            return new IntVec3(Mathf.FloorToInt(world.X), 0, Mathf.FloorToInt(world.Z) + chosen.Shift);
        }
        public static Vector3 Logical(Map map, V3 world, IntVec3 like)
        { FloorBand band = Band(map, like); return new Vector3(world.X, 0f, world.Z + band.Shift); }
        public static void OwnFlight(Projectile p)
        {
            if (!ProjectZReady) return;
            removeNativeFlight(p); removeNativeCross(p);
        }

        // A grenade hitting the underside explodes in the room below. A downward hit rests on the floor above;
        // a roof without an actual upper floor belongs to the room below, rather than to an open-air storage band.
        public static IntVec3 ContactCell(Map map, V3 world, float velocityY, Thing surface = null)
        {
            if (surface != null) return Cell(map, world, surface);
            V3 at = world;
            foreach (FloorBand band in Bands(map))
            {
                if (Math.Abs(world.Y - band.Height) > .003f) continue;
                bool above = velocityY <= 0f;
                IntVec3 upper = new IntVec3(Mathf.FloorToInt(world.X), 0, Mathf.FloorToInt(world.Z) + band.Shift);
                if (above && band.Level != 0 && ProjectZReady && (!upper.InBounds(map) || !hasFloor(upper, map))) above = false;
                at.Y += above ? .005f : -.005f;
                break;
            }
            return Cell(map, at);
        }

        // 0.6.0: every solid horizontal plane the segment crosses, with a stable identity (level and cell) so one slab
        // counts once. Bottom: the lowest closed ground of the map (or the ground without ProjectZ), which always stops
        // a round; world height 0 is not assumed to be the bottom when ProjectZ has levels below it.
        internal struct PlaneHit { public float T; public int Level, X, Z; public bool Bottom, Thick, Roof; }   // Roof: a thin building roof, not a floor slab

        public static void PlaneCrossings(Map map, V3 a, V3 b, List<PlaneHit> into)
        {
            var all = Bands(map);
            bool layered = ProjectZReady && all.Count > 1;
            int lowest = int.MaxValue;
            foreach (FloorBand band in all) lowest = Math.Min(lowest, band.Level);
            foreach (FloorBand band in all)
            {
                if (layered)
                {
                    Test(band.Height, band.Level, band.Level == lowest);
                    if (band.Equals(all[all.Count - 1])) Test(band.Height + View3D.WallHeight + .02f, band.Level + 1, false);
                }
                else { Test(band.Height, band.Level, true); Test(band.Height + View3D.WallHeight, band.Level + 1, false); }
            }

            void Test(float height, int level, bool bottom)
            {
                if (!FloorGeometry.Plane(a, b, height, out float t)) return;
                V3 at = V3.Lerp(a, b, t);
                if (!InWorld(map, at)) return;
                IntVec3 g = new IntVec3(Mathf.FloorToInt(at.X), 0, Mathf.FloorToInt(at.Z));
                bool solid, thick = false, thin = false;
                if (layered) solid = bottom || planeSolid(g, level, map);
                else if (bottom) solid = true;
                else
                {
                    IntVec3 lower = Cell(map, new V3(at.X, height - .01f, at.Z));
                    RoofDef roof = lower.InBounds(map) ? map.roofGrid.RoofAt(lower) : null;
                    solid = roof != null && !Api.RoofHidden(roof);
                    thick = roof != null && roof.isThickRoof;
                    thin = RoofWear.Breakable(roof)&&!roof.isThickRoof;
                }
                if (solid) into.Add(new PlaneHit { T = t, Level = level, X = g.x, Z = g.z, Bottom = bottom, Thick = thick, Roof = thin });
            }
        }

        public static bool PlaneContact(Map map, V3 a, V3 b, ref float nearest, out V3 normal,
            ISet<IntVec3> passedRoofs = null)
        {
            normal = new V3(0f, b.Y > a.Y ? -1f : 1f, 0f);
            bool found = false;
            float best = nearest;
            var all = Bands(map);
            foreach (FloorBand band in all)
            {
                if (ProjectZReady && all.Count > 1)
                {
                    Test(band.Height, band.Level, false);
                    if (band.Equals(all[all.Count - 1])) Test(band.Height + View3D.WallHeight + .02f, band.Level + 1, false);
                }
                else { Test(band.Height, band.Level, true); Test(band.Height + View3D.WallHeight, band.Level + 1, false); }
            }
            nearest = best;
            return found;

            void Test(float height, int level, bool ground)
            {
                if (!FloorGeometry.Plane(a, b, height, out float t) || t >= best) return;
                V3 at = V3.Lerp(a, b, t);
                if (!InWorld(map, at)) return;
                IntVec3 g = new IntVec3(Mathf.FloorToInt(at.X), 0, Mathf.FloorToInt(at.Z));
                bool solid;
                if (ProjectZReady && all.Count > 1)
                    solid = level == 0 || planeSolid(g, level, map);
                else
                {
                    IntVec3 lower = Cell(map, new V3(at.X, height - .01f, at.Z));
                    RoofDef roof = lower.InBounds(map) ? map.roofGrid.RoofAt(lower) : null;
                    solid = ground || (roof != null && !Api.RoofHidden(roof));
                }
                if (!solid) return;
                if (passedRoofs != null && !ground && level != 0)
                {
                    foreach (FloorBand lower in all)
                    {
                        if (lower.Level + 1 != level) continue;
                        var roofCell = new IntVec3(g.x, 0, g.z + lower.Shift);
                        if (roofCell.InBounds(map) && CeStructureRoof.ShouldIgnore(map, roofCell, passedRoofs)) return;
                        break;
                    }
                }
                best = t;
                found = true;
            }
        }
    }
}
