using System;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    public sealed class AerialRunwayMap:MapComponent
    {
        public AerialRunwayMap(Map map):base(map){}
        public override void MapComponentDraw(){if(map==Find.CurrentMap&&AerialFlight.Enabled)AerialRunway.DrawPreview();}
    }
    internal sealed class AerialRunwayPlan
    {
        internal V3 Start,End;
        internal float Yaw,Surface;
        internal bool Landing,Clear;
        internal string Reason;
        internal V3 BlockedAt;
        internal string BlockKind;
    }
    internal static class AerialRunway
    {
        private static Pawn cachedVehicle;
        private static AerialRunwayPlan cached;
        private static V3 cachedAt;
        private static float cachedYaw,cachedUntil;
        internal static float Agl(Pawn v,AerialFlightRecord r)
        {Bounds b=AircraftFootprint.BoundsAt(v,r.Motor);return AerialCollision.Altitude(v,b);} 
        internal static AerialRunwayPlan Plan(Pawn v,AerialFlightRecord r,bool landing)
        {
            var p=new AerialRunwayPlan{Landing=landing,Yaw=r.Motor.Yaw};
            var flat=r.Motor;flat.Pitch=flat.Roll=0;
            Bounds b=AircraftFootprint.BoundsAt(v,flat);
            float surface=AerialCollision.Support(v,b,AircraftFootprint.BoundsAt(v,r.Motor).min.y+.08f);
            p.Surface=surface;
            if(!float.IsFinite(surface))
            {AerialRunwayDiagnostics.FailSurface(v,p,flat,b,surface,"initial-support");return p;}
            float maximum=AerialFixedWing.EffectiveMaximum(AerialFlight.Profile(v).MaximumSpeed(v),VehicleControl.Owns(v)?VehicleControl.Efficiency:1f);
            var f=Quaternion.Euler(0,p.Yaw,0)*Vector3.forward;
            flat.Position.Y+=surface+.012f-b.min.y;
            float height=Agl(v,r);
            p.Start=flat.Position;
            if(landing)
            {
                p.Start+=VehicleModels.V(f*Math.Max(6f,height*3f));
                p.End=p.Start+VehicleModels.V(f*AerialRunwayRules.LandingLength(maximum));
                if(height>AerialRunwayRules.PreviewHeight+.05f){p.Reason=L.T("降至离地5格内再着陆","Descend to within 5 cells AGL to land");return p;}
            }
            else
            {
                p.End=p.Start+VehicleModels.V(f*AerialRunwayRules.TakeoffLength(maximum));p.End.Y+=AerialRunwayRules.ClimbHeight;
            }
            if(AerialFlight.Profile(v).kind!=AerialFlightKind.FixedWing)
            {p.End=p.Start+new V3(0,AerialRunwayRules.ClimbHeight,0);}
            p.Clear=Corridor(v,r,p);return p;
        }
        private static bool Corridor(Pawn v,AerialFlightRecord r,AerialRunwayPlan p)
        {
            bool wing=AerialFlight.Profile(v).kind==AerialFlightKind.FixedWing;
            var m=r.Motor;m.Pitch=m.Roll=0;m.Yaw=p.Yaw;
            V3 groundEnd=p.End;groundEnd.Y=p.Start.Y;
            if(wing)
            {
                int count=Math.Max(1,Mathf.CeilToInt((groundEnd-p.Start).Length/.5f));
                for(int i=0;i<=count;i++)
                {
                    m.Position=V3.Lerp(p.Start,groundEnd,i/(float)count);
                    Bounds box=AircraftFootprint.BoundsAt(v,m);float h=AerialCollision.Support(v,box,p.Surface+.06f);
                    if(!float.IsFinite(h))return AerialRunwayDiagnostics.FailSurface(v,p,m,box,h,"ground-support");
                    if(Math.Abs(h-p.Surface)>.08f)return AerialRunwayDiagnostics.Fail(v,p,m,box,h,"slope","ground-support");
                    if(!AerialCollision.LandingClear(v,m))return AerialRunwayDiagnostics.FailClearance(v,p,m,box,h,"ground-clearance");
                }
            }
            V3 from=p.Landing?r.Motor.Position:p.Start,to=p.Landing?p.Start:p.End;
            if(wing&&!p.Landing)
            {
                var f=Quaternion.Euler(0,p.Yaw,0)*Vector3.forward;
                from=p.Start+VehicleModels.V(f*AerialRunwayRules.Roll(AerialFlight.Profile(v).MaximumSpeed(v)));
            }
            int steps=Math.Max(1,Mathf.CeilToInt((to-from).Length/.35f));
            for(int i=0;i<=steps;i++)
            {
                m.Position=V3.Lerp(from,to,i/(float)steps);
                if(!AerialCollision.LandingClear(v,m))
                {Bounds box=AircraftFootprint.BoundsAt(v,m);return AerialRunwayDiagnostics.FailClearance(v,p,m,box,AerialCollision.Support(v,box,box.min.y+.08f),"approach-climb-clearance");}
            }
            return true;
        }
        internal static bool RequestTakeoff(Pawn v,AerialFlightRecord r)
        {
            if(r.Airborne||r.Automatic||r.Landing)return false;
            if(!AerialCrew.HasPilot(v)){r.Status=L.T("没有可操作的驾驶员，无法起飞","No capable pilot: takeoff unavailable");return false;}
            AerialRunwayPlan plan=Plan(v,r,false);
            if(!plan.Clear){r.Status=plan.Reason;return false;}
            if(!AerialFlight.HasPower(v)){r.Status=L.T("燃油或动力不足，不能起飞","Insufficient fuel / power to take off");return false;}
            Set(r,plan);r.RunwayMode=AerialFlight.Profile(v).kind==AerialFlightKind.FixedWing?AerialRunwayMode.TakeoffRoll:AerialRunwayMode.TakeoffClimb;
            r.TakeoffInputLatch=true;r.EngineStopped=false;r.Navigate=r.Orbit=r.Cruise=r.Landing=false;
            if(!VehicleApi.Drafted(v))VehicleApi.Draft(v,true);
            r.Status=L.T("自动起飞：加速／爬升","Auto takeoff: accelerate / climb");cachedUntil=0;return true;
        }
        internal static bool RequestLanding(Pawn v,AerialFlightRecord r)
        {
            AerialRunwayPlan plan=Plan(v,r,true);
            if(!plan.Clear){r.Status=plan.Reason;return false;}
            Set(r,plan);r.Landing=true;r.LandInputLatch=true;r.RunwayMode=AerialRunwayMode.LandingGlide;
            r.Navigate=r.Orbit=r.Cruise=false;r.Status=L.T("自动着陆：对准跑道进近","Auto landing: runway approach");cachedUntil=0;return true;
        }
        private static void Set(AerialFlightRecord r,AerialRunwayPlan p)
        {r.RunwayStart=p.Start;r.RunwayEnd=p.End;r.RunwayYaw=p.Yaw;r.RunwaySurface=p.Surface;}
        internal static void Cancel(AerialFlightRecord r)
        {r.RunwayMode=AerialRunwayMode.None;r.Landing=r.LandInputLatch=false;cachedUntil=0;}
        internal static string Phase(AerialFlightRecord r)=>r.RunwayMode==AerialRunwayMode.TakeoffRoll?L.T("自动起飞 · 滑跑","AUTO TAKEOFF · ROLL"):
            r.RunwayMode==AerialRunwayMode.TakeoffClimb?L.T("自动起飞 · 爬升","AUTO TAKEOFF · CLIMB"):
            r.RunwayMode==AerialRunwayMode.LandingGlide?L.T("自动着陆 · 进近","AUTO LAND · APPROACH"):
            r.RunwayMode==AerialRunwayMode.LandingRoll?L.T("自动着陆 · 减速","AUTO LAND · ROLLOUT"):null;
        internal static AerialRunwayPlan Preview(Pawn v,AerialFlightRecord r)
        {
            bool wing=AerialFlight.Profile(v).kind==AerialFlightKind.FixedWing;
            if(r.Airborne&&(!wing||Agl(v,r)>AerialRunwayRules.PreviewHeight+.05f)&&!r.Automatic)return null;
            if(cachedVehicle==v&&Time.unscaledTime<cachedUntil&&(cachedAt-r.Motor.Position).Length<.75f&&Math.Abs(Mathf.DeltaAngle(cachedYaw,r.Motor.Yaw))<4f)return cached;
            cachedVehicle=v;cachedAt=r.Motor.Position;cachedYaw=r.Motor.Yaw;cachedUntil=Time.unscaledTime+.15f;
            cached=r.Automatic?new AerialRunwayPlan{Start=r.RunwayStart,End=r.RunwayEnd,Yaw=r.RunwayYaw,Surface=r.RunwaySurface,Landing=r.Landing,Clear=true}
                :Plan(v,r,r.Airborne);
            return cached;
        }
        internal static void DrawPreview()
        {
            Pawn v=VehicleControl.Vehicle??AerialOrders.Selected;
            if(v==null||!View3D.Active||!AerialFlight.TryGet(v,out var r))return;
            // This world guide is depth-independent. Keep it in exterior/god views;
            // driver HUD still reports runway clearance without painting across the cabin.
            if(VehicleControl.DriverCamera&&AerialCockpit.Profile(v)!=null)return;
            var p=Preview(v,r);if(p==null||!float.IsFinite(p.Surface))return;
            Color color=p.Clear?new Color(.15f,1f,.38f,.85f):new Color(1f,.18f,.12f,.85f);
            Vector3 start=VehicleModels.U(p.Start),end=VehicleModels.U(p.End);start.y=end.y=p.Surface+.06f;
            var q=Quaternion.Euler(0,p.Yaw,0);float half=v.def.size.x * .5f;
            if(AerialFlight.Profile(v).kind!=AerialFlightKind.FixedWing){Visuals.GroundRing(start,half,color);return;}
            Vector3 right=q*Vector3.right*half,forward=q*Vector3.forward;
            Visuals.Line(start-right,end-right,color,.08f);Visuals.Line(start+right,end+right,color,.08f);
            Visuals.Line(start-right,start+right,color,.08f);Visuals.Line(end-right,end+right,color,.08f);
            float length=(end-start).magnitude;
            for(float d=2;d<length;d+=4)
            {
                Vector3 at=start+forward*d;Visuals.Line(at-forward*.8f-right*.32f,at,color,.09f);Visuals.Line(at-forward*.8f+right*.32f,at,color,.09f);
            }
        }
    }
}
