using System;
using System.Linq;
using IsoRim;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // The optional VF adapter supplies only a native arrival mode. Flight, crew and camera stay in the bridge.
    public static class AerialTactical
    {
        public static bool Available(Pawn v)=>v!=null&&AerialFlight.Supports(v)&&v.Faction==Faction.OfPlayer
            &&VehicleApi.Handlers(v).Any(h=>VehicleApi.List(h,"thingOwner").OfType<Pawn>().Any(p=>p.Faction==Faction.OfPlayer&&p.RaceProps.Humanlike&&VehicleApi.OperatorCapable(p,false)));
        public static string Label=>L.T("战术部署：地图边缘空中待命","Tactical deployment: airborne at map edge");
        private static bool TrySite(Pawn v,Map map,out IntVec3 cell,out AerialMotor motor)
        {
            cell=IntVec3.Invalid;motor=default;
            float width=map.Size.x,depth=FloorSpace.Bands(map)[0].Rows;
            float inset=Math.Max(10,Math.Max(v.def.size.x,v.def.size.z)+3);
            for(int lane=0;lane<4;lane++)for(int along=0;along<Math.Max(width,depth);along+=4)
            {
                float offset=along==0?0:((along/4%2==0)?1:-1)*((along+4)/8)*4;
                float x=lane==0?inset:lane==1?width-inset:width*.5f+offset;
                float z=lane==2?inset:lane==3?depth-inset:depth*.5f+offset;
                float yaw=lane==0?90:lane==1?270:lane==2?0:180;
                var candidate=new AerialMotor(new V3(x,0,z),yaw);
                Bounds local=AircraftFootprint.BoundsAt(v,candidate);
                float ground=FloorSpace.Bands(map).FirstOrDefault(b=>b.Level==0).Height;
                candidate.Position.Y=ground+8-local.min.y;
                Bounds bounds=AerialBoundary.Box(v,candidate);
                if(bounds.min.x<.12f||bounds.min.z<.12f||bounds.max.x>width-.12f||bounds.max.z>depth-.12f)continue;
                IntVec3 at=new IntVec3(Mathf.FloorToInt(x),0,Mathf.FloorToInt(z)+FloorSpace.Bands(map)[0].Shift);
                Rot4 rot=Rot4.FromAngleFlat(yaw);CellRect occupied=GenAdj.OccupiedRect(at,rot,v.def.size);
                if(!occupied.InBounds(map)||occupied.Cells.Any(c=>c.GetThingList(map).Any(t=>t is Building||t is Pawn)))continue;
                // Native Spawn must not wipe ground structures, even if our visible aircraft is above them.
                if(!AerialCollision.ClearOn(v,candidate,map))continue;
                cell=at;motor=candidate;return true;
            }
            return false;
        }
        public static bool Deploy(Pawn v,Map map)
        {
            if(!Available(v)||map==null||v.Spawned||!TrySite(v,map,out IntVec3 cell,out AerialMotor motor))return false;
            if(v.ParentHolder is ThingOwner holder)holder.Remove(v);
            // The native arrival action has already detached its world holder without destroying the vehicle.
            if(v.ParentHolder is IThingHolder owner)owner.GetDirectlyHeldThings()?.Remove(v);
            if(v.IsWorldPawn())Find.WorldPawns.RemovePawn(v);
            foreach(Pawn p in VehicleApi.Handlers(v).SelectMany(h=>VehicleApi.List(h,"thingOwner").OfType<Pawn>()))
                if(p.IsWorldPawn())Find.WorldPawns.RemovePawn(p);
            GenSpawn.Spawn(v,cell,map,Rot4.FromAngleFlat(motor.Yaw),WipeMode.Vanish);
            if(!v.Spawned)return false;
            AerialFlight.Store.Flights.Remove(v);AerialFlight.Begin(v);
            var r=AerialFlight.Store.Flights[v];motor.Position.Y=r.HomeHeight+8;
            r.Motor=motor;r.Airborne=true;r.EngineStopped=false;r.TacticalCameraPending=true;
            r.Status=L.T("战术部署：空中待命","Tactical deployment: airborne ready");
            VehicleApi.Draft(v,true);VehicleApi.Call(VehicleApi.Get(v,"vehiclePather"),"StopDead");
            VehicleApi.AerialPosition(v,AerialFlight.RawRoot(v,r),motor.Yaw,0);
            AerialArrival.Stamp(v,r);AerialOrders.Hold(v,r);
            map.fogGrid.FloodUnfogAdjacent(cell);Current.Game.CurrentMap=map;CameraJumper.TryHideWorld();View3D.Active=true;
            Log.Message("[FpsBridge] AERIAL_TACTICAL_DEPLOY vehicle="+v.ThingID+"; map="+map.uniqueID+"; AGL="+AerialRunway.Agl(v,r).ToString("0.0"));return true;
        }
        internal static void Frame()
        {
            if(AerialFlight.Store==null||LongEventHandler.ShouldWaitForEvent||VehicleControl.On)return;
            foreach(var p in AerialFlight.Store.Flights.ToArray())
            {
                if(!p.Value.TacticalCameraPending||!p.Key.Spawned||p.Key.Map!=Find.CurrentMap)continue;
                AerialPlaytest.ResetSession();Find.Selector.ClearSelection();Find.Selector.Select(p.Key,false,false);
                VehicleControl.TrySelected();
                if(VehicleControl.Owns(p.Key)){VehicleControl.TacticalCamera();p.Value.TacticalCameraPending=false;}
            }
        }
    }
}
