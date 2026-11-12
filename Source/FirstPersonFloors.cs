using System;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Delegate every physical coordinate and pointer operation to the original
    // provider. Only its drawing visibility changes during FPS control.
    internal static class FirstPersonFloors
    {
        private sealed class VisibleLayout : IFloorLayout
        {
            internal readonly IFloorLayout Original;
            private readonly Map owner;
            internal VisibleLayout(IFloorLayout original, Map ownerMap) { Original = original; owner = ownerMap; }
            public int GroundRows(Map map) => Original.GroundRows(map);
            public int FloorCount(Map map) => Original.FloorCount(map);
            public bool Floor(Map map, int k, out float height, out int zShift)
            {
                bool shown = Original.Floor(map, k, out height, out zShift);
                return shown || (map == owner && GroundRows(map) > 0 && k >= 1 && k <= FloorCount(map));
            }
            public int PointerFloor(Map map) => Original.PointerFloor(map);
            public Vector3 ThingOffset(Thing t) => Original.ThingOffset(t);
            public int ViewFloor(Map map, int k) => Original.ViewFloor(map, k);
            public bool ShipCell(Map map, IntVec3 c) => Original.ShipCell(map, c);
        }
        private static VisibleLayout owned;
        private static IDisposable walls;
        private static Map map;
        internal static void Sync()
        {
            bool enabled = Find.CurrentMap != null && ((Bridge.Active && ControlCamera.Mode == ControlView.FirstPerson)
                || VehicleControl.On || RemoteTurret.On);
            if (!enabled) { Release(); return; }
            if (map != Find.CurrentMap) { Release(); map = Find.CurrentMap; }
            if (owned != null && !ReferenceEquals(Api.Floors, owned)) owned = null;
            if (owned == null && Api.Floors != null) { owned = new VisibleLayout(Api.Floors, map); Api.Floors = owned; }
            if (walls == null) walls = Api.RequestFullWalls(map, Bridge.ShootingPawn);
        }
        internal static void Release()
        {
            if (owned != null && ReferenceEquals(Api.Floors, owned)) Api.Floors = owned.Original;
            owned = null; walls?.Dispose(); walls = null; map = null;
        }
    }
}
