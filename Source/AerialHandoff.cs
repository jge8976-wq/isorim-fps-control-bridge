using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialHandoff
    {
        public static void BeforeGizmos(ThingComp __instance)
        {
            if(!(__instance.parent is Pawn v)||!AerialFlight.Supports(v)||VehicleControl.Owns(v)||VehicleApi.Drafted(v)||AerialFlight.Airborne(v))return;
            if(!AerialFlight.TryGet(v,out var r))
            {
                if(Math.Abs(VehicleApi.Float(v,"Angle"))<.001f)return;
                V3 at=FloorSpace.Spot(v);at.Y-=AerialGeometry.LocalBounds(v).min.y;
                r=new AerialFlightRecord{Motor=new AerialMotor(at,VehicleApi.NativeYaw(v)),HomeHeight=at.Y};
            }
            Release(v,r);
        }
        public static bool BeforeNativePath(object __instance)
        {
            Pawn v=VehicleApi.Get(__instance,"vehicle") as Pawn;
            if(!AerialFlight.Supports(v))return true;
            bool local=AerialFlight.TryGet(v,out var r);
            if(VehicleControl.Owns(v)||local&&(r.Airborne||r.Automatic||r.Landing))return false;
            if(!EnsureNativeGrid(v))return false;
            if(!local)return true;
            // A drafted aircraft on the ground still needs native right-click orders.
            // Remove the saved local pose too, so it cannot pin DrawPos while the pather moves.
            return Release(v,r,true);
        }
        private static bool EnsureNativeGrid(Pawn v)
        {
            try
            {
                Type mappingType=AccessTools.TypeByName("Vehicles.VehiclePathingSystem");
                object mapping=v.Map.GetComponent(mappingType);
                object data=AccessTools.Property(mappingType,"Item").GetValue(mapping,new object[]{v.def});
                object path=VehicleApi.Get(data,"VehiclePathGrid"),updater=VehicleApi.Get(data,"VehicleRegionAndRoomUpdater");
                // VF can defer the airframe's grid until first use. Its StartPath assumes Init
                // has already happened, so request the existing synchronous initialization API.
                if(!VehicleApi.Bool(path,"Enabled")||!VehicleApi.Bool(updater,"Enabled"))
                {
                    Type urgency=AccessTools.TypeByName("Vehicles.DeferredGridGeneration+Urgency");
                    AccessTools.Method(mappingType,"RequestGridsFor",new[]{AccessTools.TypeByName("Vehicles.VehicleDef"),urgency})
                        .Invoke(mapping,new object[]{v.def,Enum.Parse(urgency,"Urgent")});
                }
                return VehicleApi.Bool(path,"Enabled")&&VehicleApi.Bool(updater,"Enabled")
                    &&VehicleApi.Get(VehicleApi.Get(data,"VehicleRegionGrid"),"DirectGrid")!=null;
            }
            catch(Exception e)
            {
                Log.WarningOnce("[FpsBridge] Native aircraft ground path initialization failed: "+e,19271870);
                return false;
            }
        }
        internal static bool Release(Pawn v,AerialFlightRecord r,bool allowDraftedGround=false)
        {
            if(v==null||!v.Spawned||VehicleControl.Owns(v)||VehicleApi.Drafted(v)&&!allowDraftedGround||r.Airborne||r.Automatic||r.Landing)return false;
            // Native CompVehicleLauncher refuses every residual Angle, including a saved diagonal Rot8.
            // Restore a valid cardinal footprint before removing the local pose, even when the game is paused.
            foreach(float yaw in new[]{0f,90f,180f,270f}.OrderBy(a=>Math.Abs(Mathf.DeltaAngle(r.Motor.Yaw,a))))
            {
                if(!VehicleApi.FaceClear(v,yaw))continue;
                var aligned=r.Motor;aligned.Yaw=yaw;aligned.Pitch=aligned.Roll=0;
                Bounds box=AircraftFootprint.BoundsAt(v,aligned);
                float support=AerialCollision.Support(v,box,Math.Max(FloorSpace.Band(v.Map,v.Position).Height+.08f,box.min.y+.08f));
                if(!float.IsFinite(support))continue;
                aligned.Position.Y+=support+.012f-box.min.y;
                if(!AerialCollision.Clear(v,aligned))continue;
                VehicleApi.Call(VehicleApi.Get(v,"vehiclePather"),"StopDead");
                Vector3 root=AerialFlight.Raw(v,VehicleModels.U(aligned.Position));
                if(!VehicleApi.AerialPosition(v,root,yaw,Mathf.DeltaAngle(r.Motor.Yaw,yaw)))continue;
                VehicleApi.Set(v,"Angle",0f);
                AerialFlight.Store.Flights.Remove(v);
                VehicleApi.Call(v,"Notify_Teleported",false,true);
                VehicleApi.Set(v,"Angle",0f);
                Log.Message("[FpsBridge] AERIAL_NATIVE_HANDOFF vehicle="+v.ThingID+"; cardinal="+yaw+"; angle=0; transport=restored");
                return true;
            }
            r.Status=L.T("周围空间不足，无法对齐原版运输朝向","Not enough room to align for native transport");return false;
        }
        internal static void Frame()
        {
            if(AerialFlight.Store==null)return;
            foreach(var p in AerialFlight.Store.Flights.ToArray())
                if(!p.Key.Destroyed&&!p.Key.Dead&&p.Key.Spawned)Release(p.Key,p.Value);
        }
    }
}
