using System.Collections.Generic;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Sight in height over the ground floor (x/z in cells, y in true cells over the ground), for what the game itself
    // judges flat: who can see and shoot a pawn standing on a wall or roof top, and where rounds meet a roof.
    //   - a wall (impassable, filling its cell, or natural rock) is solid up to wall height;
    //   - a roof IsoRim draws is solid from just under wall height up to its outer surface (RoofShape: the slope or
    //     the flat slab), so a ridge hides what is behind it;
    //   - any other roof (not drawn, ProjectZ floors, thick rock) is a thin slab at wall height.
    internal static class Sight3D
    {
        private const float Step = .2f, Slab = .06f;

        public static bool Blocked(Map map, Vector3 p, bool walls = true)
        {
            IntVec3 c = new IntVec3(Mathf.FloorToInt(p.x), 0, Mathf.FloorToInt(p.z));
            if (!c.InBounds(map) || FloorSpace.Band(map, c).Level != 0 || p.y < 0f) return false;
            float wall = View3D.WallHeight;
            if (walls && p.y < wall && c.GetEdifice(map) is Building b && !(b is Building_Door) && b.def.passability == Traversability.Impassable
                && (b.def.fillPercent >= .99f || b.def.building?.isNaturalRock == true)) return true;
            return RoofAt(map, c, p.x, p.z, out float lo, out float hi) && p.y >= lo && p.y <= hi;
        }

        // The solid band of the roof over cell c at x/z, if it has one.
        private static bool RoofAt(Map map, IntVec3 c, float x, float z, out float lo, out float hi)
        {
            float wall = View3D.WallHeight;
            lo = wall - .005f; hi = wall + Slab;   // from the ceiling (where IsoRim lays a roof's underside) up
            if (RoofShape.Covers(map, c)) { hi = RoofShape.HeightAt(map, x, z); return true; }
            return map.roofGrid.RoofAt(c) != null;
        }

        // True when nothing solid lies between a (an eye on the ground of its cell) and b (a point over a top of height
        // bTop in its cell): in a's own cell only a roof overhead counts, in b's cell whatever is under the top.
        public static bool Clear(Map map, Vector3 a, Vector3 b, float bTop)
        {
            IntVec3 ca = new IntVec3(Mathf.FloorToInt(a.x), 0, Mathf.FloorToInt(a.z)), cb = new IntVec3(Mathf.FloorToInt(b.x), 0, Mathf.FloorToInt(b.z));
            float length = (b - a).magnitude;
            int samples = Mathf.Max(1, Mathf.CeilToInt(length / Step));
            for (int i = 1; i < samples; i++)
            {
                Vector3 p = Vector3.Lerp(a, b, i / (float)samples);
                IntVec3 c = new IntVec3(Mathf.FloorToInt(p.x), 0, Mathf.FloorToInt(p.z));
                if (c == cb) { if (p.y < bTop - .05f) return false; continue; }
                if (Blocked(map, p, walls: c != ca)) return false;
            }
            return true;
        }

        // Where a round from a to b first meets a roof (fraction of the way), +inf for none. Ground-floor rounds only.
        public static float RoofContact(Map map, V3 a, V3 b, ISet<IntVec3> passedRoofs = null)
        {
            return RoofSegmentContact.Try(map, a, b, 1, out float t, out _, passedRoofs) ? t : float.PositiveInfinity;
        }

        // Eye of a shooter or watcher standing on the ground at its own cell.
        public static Vector3 EyeOf(Thing thing, IntVec3 at)
        {
            float h = thing is Pawn p ? Mathf.Clamp(Bindings.PickHeight(p) * .9f, .3f, 3f) : 1f;
            return new Vector3(at.x + .5f, h, at.z + .5f);
        }

        // Whether an eye can see the pawn standing on a top: its head or its chest.
        public static bool SeesOnTop(Map map, Vector3 eye, Pawn target, Vector3 at, float top)
        {
            float standing = Mathf.Clamp(IsoRimMod.Settings.fpEyeHeight, .3f, 3f) * (Stances.For(target) ? Stances.State.Height : 1f);
            Vector3 head = new Vector3(at.x, top + standing, at.z), chest = new Vector3(at.x, top + standing * .55f, at.z);
            return Clear(map, eye, head, top) || Clear(map, eye, chest, top);
        }
    }
}
