using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialBoundary
    {
        private static bool bound;
        private static Action<Pawn,IntVec3,Map> transfer;
        internal static Bounds Box(Pawn v,AerialMotor m)
        {
            return AircraftFootprint.BoundsAt(v,m);
        }
        // An installed RimExodus must own a real exit and grant before it may transfer a vehicle.
        // Loading a map mod alone never disables the safety boundary or invents a caravan conversion.
        private static bool TryTransfer(Pawn v,AerialFlightRecord r,Bounds box)
        {
            if(!bound)
            {
                bound=true;var method=AccessTools.Method(AccessTools.TypeByName("RimExodus.SeamlessMapTransferTrigger"),"TryTriggerTransfer",new[]{typeof(Pawn),typeof(IntVec3),typeof(Map)});
                if(method!=null)transfer=method.CreateDelegate<Action<Pawn,IntVec3,Map>>();
            }
            if(transfer==null)return false;
            ThingDef exit=DefDatabase<ThingDef>.GetNamedSilentFail("RimExodus_SeamlessEnterSpot");if(exit==null)return false;
            Map source=v.Map;float relativeHeight=r.Motor.Position.Y-r.HomeHeight;
            foreach(Thing spot in source.listerThings.ThingsOfDef(exit))
            {
                FloorBand band=FloorSpace.Band(source,spot.Position);
                float x=spot.Position.x+.5f,z=spot.Position.z-band.Shift+.5f;
                if(x<box.min.x-1||x>box.max.x+1||z<box.min.z-1||z>box.max.z+1)continue;
                transfer(v,spot.Position,source);
                if(!v.Spawned||v.Map==source)continue;
                Vector3 at=(Vector3)VehicleApi.Call(v,"TrueCenter");FloorBand next=FloorSpace.Band(v.Map,v.Position);
                r.HomeHeight=next.Height-AerialGeometry.LocalBounds(v).min.y;
                var m=r.Motor;m.Position=new V3(at.x,r.HomeHeight+relativeHeight,at.z-next.Shift);r.Motor=m;
                r.Committed=false;r.Navigate=r.Orbit=r.Cruise=false;AerialRunway.Cancel(r);
                r.Status=L.T("已通过无缝地图出口","Transferred through seamless exit");return true;
            }
            return false;
        }
        // Returns true only when an actual native seamless transfer changed the aircraft's map.
        internal static bool Enforce(Pawn v,AerialFlightRecord r,ref AerialMotor next)
        {
            Bounds box=Box(v,next);float width=v.Map.Size.x,depth=FloorSpace.Bands(v.Map)[0].Rows;
            if(box.min.x>=0&&box.min.z>=0&&box.max.x<=width&&box.max.z<=depth)return false;
            if(TryTransfer(v,r,box)){next=r.Motor;return true;}
            if(!AerialBoundaryRules.Bounce(ref next,VehicleModels.V(box.min),VehicleModels.V(box.max),width,depth))return false;
            // Recheck the native construction footprint after the heading changes.
            Bounds turned=Box(v,next);
            float dx=turned.min.x<0?-turned.min.x:turned.max.x>width?width-turned.max.x:0;
            float dz=turned.min.z<0?-turned.min.z:turned.max.z>depth?depth-turned.max.z:0;
            next.Position.X+=dx;next.Position.Z+=dz;
            AerialRunway.Cancel(r);r.Navigate=r.Orbit=r.Cruise=false;r.AimYaw=next.Yaw;
            if(VehicleControl.Owns(v)&&VehicleControl.PilotLook)VehicleControl.SetLook(next.Yaw,next.Pitch);
            r.Status=L.T("地图边界：向内回弹","Map boundary: bounced inward");return false;
        }
    }
}
