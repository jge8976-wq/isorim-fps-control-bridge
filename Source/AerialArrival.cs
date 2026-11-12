using System;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // World transport, seamless transfer and save/load can replace the native map/cell independently of our motor.
    // Rebase once from the native arrival, never project stale source-map coordinates back into the destination.
    internal static class AerialArrival
    {
        private static bool busy;
        internal static void Frame()
        {
            if(AerialFlight.Store==null)return;
            foreach(var p in new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Pawn,AerialFlightRecord>>(AerialFlight.Store.Flights))
                if(p.Key.Spawned&&!p.Key.Destroyed&&!p.Key.Dead)Sync(p.Key,p.Value);
        }
        internal static void Stamp(Pawn v,AerialFlightRecord r)
        {r.NativeMapId=v.Map.uniqueID;r.NativeCell=v.Position;r.NativeStamped=true;}
        internal static void Initial(Pawn v,AerialFlightRecord r)
        {
            if(busy)return;busy=true;
            try{Stamp(v,r);RepairEdge(v,r);}
            finally{busy=false;}
        }
        internal static void Sync(Pawn v,AerialFlightRecord r)
        {
            if(busy||v.Map==null)return;
            bool changed=r.NativeMapId>=0&&r.NativeMapId!=v.Map.uniqueID;
            if(!changed&&r.NativeStamped&&r.NativeCell==v.Position)return;
            busy=true;
            try
            {
                Vector3 native=(Vector3)VehicleApi.Call(v,"TrueCenter");FloorBand band=FloorSpace.Band(v.Map,v.Position);
                float dx=r.Motor.Position.X-native.x,dz=r.Motor.Position.Z-(native.z-band.Shift);
                if(changed||dx*dx+dz*dz>6.25f)
                {
                    float height=Math.Clamp(r.Motor.Position.Y-r.HomeHeight,0,AerialFlight.Profile(v).maximumHeight);
                    r.HomeHeight=band.Height-AerialGeometry.LocalBounds(v).min.y;
                    var m=r.Motor.Brake();m.Position=new V3(native.x,r.HomeHeight+(r.Airborne?Math.Max(.25f,height):0),native.z-band.Shift);
                    if(!r.Airborne){m.Yaw=VehicleApi.NativeYaw(v);m.Pitch=m.Roll=0;}
                    r.Motor=m;r.Committed=false;r.Navigate=r.Orbit=r.Cruise=false;AerialRunway.Cancel(r);
                    r.GodForward=r.GodTurn=r.GodClimb=0;r.GodClimbFrame=-100;
                    Log.Message("[FpsBridge] AERIAL_MAP_REBASE vehicle="+v.ThingID+"; map="+v.Map.uniqueID+"; airborne="+r.Airborne+"; relativeHeight="+height.ToString("0.00"));
                }
                Stamp(v,r);if(!r.Airborne)RepairEdge(v,r);
            }
            finally{busy=false;}
        }
        internal static bool Fits(Pawn v,AerialMotor m)
        {
            Bounds b=AerialBoundary.Box(v,m);
            return b.min.x>=.12f&&b.min.z>=.12f&&b.max.x<=v.Map.Size.x-.12f&&b.max.z<=FloorSpace.Bands(v.Map)[0].Rows-.12f;
        }
        private static bool RepairEdge(Pawn v,AerialFlightRecord r)
        {
            if(r.Airborne||Fits(v,r.Motor))return false;
            var seed=r.Motor;seed.Pitch=seed.Roll=0;Bounds b=AerialBoundary.Box(v,seed);
            float width=v.Map.Size.x,depth=FloorSpace.Bands(v.Map)[0].Rows;
            // A native edge arrival may face out of the map. Point the recovery pose inward so a fixed wing
            // has room for its next taxi/takeoff instead of immediately requesting an out-of-map runway.
            if(b.min.x<.12f)seed.Yaw=90;else if(b.max.x>width-.12f)seed.Yaw=270;
            else if(b.min.z<.12f)seed.Yaw=0;else if(b.max.z>depth-.12f)seed.Yaw=180;
            b=AerialBoundary.Box(v,seed);
            if(b.size.x>width-.24f||b.size.z>depth-.24f)return false;
            seed.Position.X+=b.min.x<.12f?.12f-b.min.x:b.max.x>width-.12f?width-.12f-b.max.x:0;
            seed.Position.Z+=b.min.z<.12f?.12f-b.min.z:b.max.z>depth-.12f?depth-.12f-b.max.z:0;
            int reach=24;
            int outside=0,unsupported=0,obstructed=0,projection=0;
            for(int radius=0;radius<=reach;radius++)
                for(int x=-radius;x<=radius;x++)for(int z=-radius;z<=radius;z++)
                {
                    if(Math.Max(Math.Abs(x),Math.Abs(z))!=radius)continue;
                    var m=seed;m.Position.X+=x;m.Position.Z+=z;
                    if(!Fits(v,m)){outside++;continue;}
                    Bounds shape=AerialGeometry.BoundsAt(v,m);
                    float floor=AerialCollision.Support(v,shape,FloorSpace.Band(v.Map,v.Position).Height+.08f);
                    if(!float.IsFinite(floor)){unsupported++;continue;}
                    m.Position.Y+=floor+.012f-shape.min.y;
                    if(!AerialCollision.LandingClear(v,m)){obstructed++;continue;}
                    if(!VehicleApi.AerialPosition(v,AerialFlight.Raw(v,VehicleModels.U(m.Position)),m.Yaw,0)){projection++;continue;}
                    r.Motor=m.Brake();r.HomeHeight=m.Position.Y;r.Committed=false;r.EngineStopped=true;
                    VehicleApi.Call(v,"Notify_Teleported",false,true);Stamp(v,r);
                    r.Status=L.T("运输抵达：已校正边缘净空","Transport arrival: edge clearance restored");
                    Log.Message("[FpsBridge] AERIAL_ARRIVAL_CLEARANCE vehicle="+v.ThingID+"; map="+v.Map.uniqueID+"; native="+v.Position);return true;
                }
            r.Status=L.T("抵达位置过窄：附近没有安全起飞位置","Arrival area too narrow: no nearby safe takeoff position");
            Log.Warning("[FpsBridge] AERIAL_ARRIVAL_BLOCKED vehicle="+v.ThingID+"; outside="+outside+"; unsupported="+unsupported+"; obstructed="+obstructed+"; projection="+projection+"; seed="+seed.Position.X+","+seed.Position.Y+","+seed.Position.Z);return false;
        }
    }
}
