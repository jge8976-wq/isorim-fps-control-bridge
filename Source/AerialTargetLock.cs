using System;
using System.Collections.Generic;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Explicit opt-in missile extension. Guns and unguided rocket pods never acquire targets.
    internal static class AerialTargetLock
    {
        private static Pawn vehicle;
        private static object gun;
        private static Thing target;
        private static int ticks,lastTick;
        internal static Thing Target=>Ready?target:null;
        internal static bool Ready=>Valid(target,vehicle?.Map)&&gun==VehicleWeapons.Current&&vehicle==VehicleControl.Vehicle
            &&ticks>=Math.Max(1,(int)((AerialArmament.Data(gun)?.lockSeconds??1.5f)*60));
        internal static bool Valid(Thing t,Map map)=>t!=null&&!t.Destroyed&&t.Spawned&&t.Map==map&&!(t is Pawn p&&p.Dead);
        internal static Vector3 Point(Thing t)
        {
            if(t is Pawn v&&AerialFlight.TryGet(v,out var r))return VehicleModels.U(r.Motor.Position)+AerialGeometry.Hull(v)*AerialGeometry.LocalBounds(v).center;
            V3 at=FloorSpace.Spot(t);return VehicleModels.U(at)+Vector3.up*(t is Pawn?.7f:.6f);
        }
        internal static void Reset(){vehicle=null;gun=null;target=null;ticks=0;lastTick=-1;}
        internal static bool EnsureLock(){if(Ready)return true;if(target==null)Toggle();return false;}
        internal static string Hint => AerialArmament.Data(VehicleWeapons.Current)?.lockableMissile!=true?null
            : ControlKeys.Label(ControlAction.AirOrbit)+L.T(" 锁定 · "," lock · ")+(Ready?L.T("已锁定","LOCKED"):target==null?L.T("未锁定","NO LOCK"):L.T("锁定中","ACQUIRING"));
        private static bool Visible(Pawn v,Thing t)
        {
            if(!Valid(t,v?.Map)||t.Position.Fogged(v.Map))return false;
            Vector3 eye=VehicleControl.Eye(),p=Point(t),d=p-eye;
            if(d.sqrMagnitude<.25f)return false;
            Vector3 f=Quaternion.Euler(FirstPerson.Pitch,FirstPerson.Yaw,0)*Vector3.forward;
            if(Vector3.Dot(d.normalized,f)<Mathf.Cos((AerialArmament.Data(VehicleWeapons.Current)?.seekerDegrees??25)*Mathf.Deg2Rad))return false;
            if(Ballistics.SightContact(v,VehicleModels.V(eye),VehicleModels.V(p),out V3 hit,false)
                &&(VehicleModels.U(hit)-p).magnitude>Math.Max(.8f,Math.Max(t.def.size.x,t.def.size.z)*.6f))return false;
            return true;
        }
        internal static void Toggle()
        {
            if(!VehicleControl.Captured)return;
            if(AerialArmament.Data(VehicleWeapons.Current)?.lockableMissile!=true)
            {Reset();Messages.Message(L.T("当前武器不支持导弹锁定。","Selected weapon has no missile seeker."),MessageTypeDefOf.RejectInput,false);return;}
            if(target!=null){Reset();return;}
            Pawn v=VehicleControl.Vehicle;
            if(v?.Map==null){Reset();return;}
            var excluded=new HashSet<Thing>{v,VehicleControl.Crew};
            foreach(object h in VehicleApi.Handlers(v))
                foreach(object occupant in VehicleApi.List(h,"thingOwner"))
                    if(occupant is Thing crew)excluded.Add(crew);
            Thing pick=MissileLockSelection.Choose(v.Map.listerThings.AllThings,
                t=>!excluded.Contains(t)&&(t is Pawn||t is Building&&t.HostileTo(v))&&Visible(v,t),
                t=>t.HostileTo(v),
                t=>IffMarkers.Project(Point(t),out Vector2 at,out _)
                    ?(at-new Vector2(UI.screenWidth*.5f,UI.screenHeight*.5f)).sqrMagnitude:float.NaN);
            if(pick==null){Messages.Message(L.T("准星附近没有可锁定的可见生物或敌方目标。","No visible creature or hostile target near the reticle."),MessageTypeDefOf.RejectInput,false);return;}
            vehicle=v;gun=VehicleWeapons.Current;target=pick;ticks=0;lastTick=Find.TickManager.TicksGame;
        }
        internal static void Frame()
        {
            if(target==null)return;
            if(!VehicleControl.Captured||vehicle!=VehicleControl.Vehicle||gun!=VehicleWeapons.Current||!Visible(vehicle,target)){Reset();return;}
            int now=Find.TickManager.TicksGame;if(!Find.TickManager.Paused)ticks+=Math.Clamp(now-lastTick,0,60);lastTick=now;
        }
        internal static Thing Capture(object fired)=>Ready&&gun==fired?target:
            fired!=null&&AerialWeaponSolution.Owns(fired,out var v)&&!VehicleControl.Owns(v)?AerialFireControl.Captured(fired):null;
        internal static void Draw()
        {
            if(target==null||!Valid(target,vehicle?.Map)||!IffMarkers.Project(Point(target),out Vector2 p,out _))return;
            Color old=GUI.color;try{GUI.color=Ready?Color.green:Color.yellow;Widgets.DrawBox(new Rect(p.x-24,p.y-24,48,48),2);
                Widgets.Label(new Rect(p.x-90,p.y+27,180,28),Ready?L.T("已锁定","LOCKED"):L.T("锁定中 ","ACQUIRING ")+Math.Max(0,(AerialArmament.Data(gun)?.lockSeconds??1.5f)-ticks/60f).ToString("0.0")+"s");}
            finally{GUI.color=old;}
        }
        internal static V3 Steer(Thing target,Map map,V3 pos,V3 velocity,float rate)
        {
            if(!Valid(target,map)||velocity.Length<.00001f)return velocity;
            Vector3 from=VehicleModels.U(velocity),to=Point(target)-VehicleModels.U(pos);
            if(to.sqrMagnitude<.000001f)return velocity;
            float angle=Mathf.Acos(Mathf.Clamp(Vector3.Dot(from.normalized,to.normalized),-1,1));
            float fraction=angle<.00001f?1:Math.Min(1,Math.Max(0,rate)*Mathf.Deg2Rad/60/angle);
            Quaternion turn=Quaternion.Slerp(Quaternion.identity,Quaternion.FromToRotation(from,to),fraction);
            return VehicleModels.V(turn*from);
        }
    }
}
