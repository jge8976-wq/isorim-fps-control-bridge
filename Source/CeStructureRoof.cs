using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class CeStructureRoof
    {
        internal readonly struct Contact
        {
            internal readonly float T;
            internal readonly IntVec3 Cell;
            internal readonly RoofDef Roof;
            internal readonly bool Slab;

            internal Contact(float t, IntVec3 cell, RoofDef roof, bool slab)
            {
                T = t;
                Cell = cell;
                Roof = roof;
                Slab = slab;
            }

            internal bool Breakable => !Slab && RoofWear.Breakable(Roof);
        }

        private static readonly List<FloorSpace.PlaneHit> Planes = new List<FloorSpace.PlaneHit>();

        // A null-Thing contact can be the ground, the drawn face of a roof or a
        // ProjectZ slab. Resolve the actual lower cell before applying any wear.
        internal static bool TryContact(Map map, V3 from, V3 to, float nearest, out Contact hit,
            ISet<IntVec3> passedRoofs = null)
        {
            hit = default;
            if (map == null || float.IsNaN(nearest) || nearest < 0f) return false;
            float best = nearest;
            bool found = false;
            Contact candidate = default;
            Planes.Clear();
            FloorSpace.PlaneCrossings(map, from, to, Planes);
            foreach (FloorSpace.PlaneHit plane in Planes)
            {
                if (plane.Bottom || plane.T > best + .0001f) continue;
                foreach (FloorBand band in FloorSpace.Bands(map))
                {
                    if (band.Level + 1 != plane.Level) continue;
                    var lower = new IntVec3(plane.X, 0, plane.Z + band.Shift);
                    Consider(plane.T, lower, ground: plane.Level == 0);
                    break;
                }
            }
            // The drawn pitched surface can be above the nominal ceiling plane.
            if (RoofSegmentContact.Try(map, from, to, best + .0001f, out float sloped, out IntVec3 roofCell, passedRoofs))
            {
                Consider(sloped, roofCell);
            }
            hit = candidate;
            return found;

            void Consider(float fraction, IntVec3 cell, bool ground = false)
            {
                if (!cell.InBounds(map) || (found && fraction >= best) || (!ground && ShouldIgnore(map, cell, passedRoofs))) return;
                RoofDef roof = map.roofGrid.RoofAt(cell);
                IntVec3 upper = FloorSpace.CellAbove(map, cell);
                bool slab = ground || RoofWear.StructuralFloor(roof) || FloorSpace.HasFloorAt(map, upper);
                if (roof == null && !slab) return;
                best = Math.Min(best, fraction);
                candidate = new Contact(fraction, cell, roof, slab);
                found = true;
            }
        }

        // A passed roof is local to one ray. Recheck the cell because a roof can
        // collapse between segments, and a real floor there must still block.
        internal static bool ShouldIgnore(Map map, IntVec3 cell, ISet<IntVec3> passedRoofs)
        {
            if (passedRoofs == null || !passedRoofs.Contains(cell)) return false;
            RoofDef roof = map.roofGrid.RoofAt(cell);
            return (roof == null || RoofWear.Breakable(roof)) && !FloorSpace.HasFloorAt(map, FloorSpace.CellAbove(map, cell));
        }

        // Direct rounds and fragments pay their actual damage into the same roof HP pool.
        // The caller still runs the native CE impact once at this contact.
        internal static bool Hit(Map map, in Contact hit, float damage = 50f) =>
            hit.Breakable && RoofWear.Damage(map, hit.Cell, hit.Roof, damage);
    }
}
