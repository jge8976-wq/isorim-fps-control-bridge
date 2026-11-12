using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class FurnitureSurfaces
    {
        internal const float SeatHeight = .5f;
        internal static bool IsSeat(Thing t) => t is Building && t.def.building?.isSittable == true
            && !(t is Building_Door) && t.Spawned && !t.Destroyed;
        internal static Thing At(Map map,IntVec3 cell)
        {
            if(map==null || !cell.InBounds(map))return null;
            foreach(Thing t in cell.GetThingList(map))if(IsSeat(t))return t;
            return null;
        }
        internal static bool Covers(Thing seat,Vector3 point)
        {
            if(!IsSeat(seat))return false;
            CellRect r=seat.OccupiedRect();
            return PawnPoseRules.SeatContains(point.x-(r.minX+r.maxX+1)*.5f,point.z-(r.minZ+r.maxZ+1)*.5f,
                (r.Width*.5f)-.12f,(r.Height*.5f)-.12f);
        }
        internal static void Volumes(Thing seat,List<HitVolume> into)
        {
            CellRect r=seat.OccupiedRect();Vector3 offset=FloorSpace.Offset(seat);
            float x=(r.minX+r.maxX+1)*.5f+offset.x,z=(r.minZ+r.maxZ+1)*.5f+offset.z;
            float hx=r.Width*.5f-.12f,hz=r.Height*.5f-.12f;
            into.Add(BodyVolumes.Box(HitRegion.Solid,new V3(x,offset.y+SeatHeight-.045f,z),new V3(hx,.045f,hz),0));
            foreach(float dx in new[]{-hx+.045f,hx-.045f})foreach(float dz in new[]{-hz+.045f,hz-.045f})
                into.Add(BodyVolumes.Box(HitRegion.Solid,new V3(x+dx,offset.y+.225f,z+dz),new V3(.045f,.225f,.045f),0));
        }
    }
}
