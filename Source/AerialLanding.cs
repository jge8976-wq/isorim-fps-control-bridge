using System;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialLanding
    {
        internal static bool Request(Pawn v,AerialFlightRecord r)
        {
            if(!r.Airborne){AerialFlight.StopEngine(v,r);r.Status=L.T("已着陆，发动机关闭","Landed, engine stopped");return true;}
            if(AerialFlight.Profile(v).kind==AerialFlightKind.FixedWing)return AerialRunway.RequestLanding(v,r);
            r.Navigate=r.Orbit=false;
            var profile=AerialFlight.Profile(v);
            Vector3 forward=Quaternion.Euler(0,r.Motor.Yaw,0)*Vector3.forward;
            float height=Math.Max(0,AircraftFootprint.BoundsAt(v,r.Motor).min.y-r.HomeHeight);
            Vector3 survey=VehicleModels.U(r.Motor.Position);
            if(profile.kind==AerialFlightKind.FixedWing)survey+=forward*Mathf.Clamp(height*2.5f,14f,65f);
            for(int radius=0;radius<=10;radius++)
                for(int x=-radius;x<=radius;x++)for(int z=-radius;z<=radius;z++)
                {
                    if(Math.Max(Math.Abs(x),Math.Abs(z))!=radius)continue;
                    var m=r.Motor;m.Pitch=m.Roll=0;m.Position.X=survey.x+x;m.Position.Z=survey.z+z;
                    Bounds b=AircraftFootprint.BoundsAt(v,m);
                    float surface=AerialCollision.Support(v,b,AircraftFootprint.BoundsAt(v,r.Motor).min.y+.06f);
                    if(!float.IsFinite(surface))continue;
                    m.Position.Y+=surface+.012f-b.min.y;
                    if(!AerialCollision.LandingClear(v,m)||!Approach(v,r.Motor,m))continue;
                    if(profile.kind==AerialFlightKind.FixedWing&&!Runway(v,m,forward))continue;
                    r.LandingTarget=m.Position;r.Landing=true;r.LandInputLatch=true;
                    r.Status=L.T("自动着陆：进近","Auto landing: approach");return true;
                }
            r.Status=L.T("没有安全落点／净空，请手动移到开阔处","No clear landing area / approach; move to open ground");return false;
        }
        private static bool Runway(Pawn v,AerialMotor touchdown,Vector3 forward)
        {
            for(int i=-6;i<=10;i+=2)
            {
                var m=touchdown;m.Position+=VehicleModels.V(forward*i);
                Bounds b=AircraftFootprint.BoundsAt(v,m);float h=AerialCollision.Support(v,b,b.min.y+.05f);
                if(!float.IsFinite(h)||Math.Abs(h-(b.min.y-.012f))>.08f||!AerialCollision.LandingClear(v,m))return false;
            }
            return true;
        }
        private static bool Approach(Pawn v,AerialMotor from,AerialMotor to)
        {
            var m=from;m.Pitch=m.Roll=0;
            V3 above=to.Position;above.Y=Math.Max(from.Position.Y,to.Position.Y+.2f);
            int steps=Math.Max(1,(int)Math.Ceiling((above-from.Position).Length/.5f));
            for(int i=1;i<=steps;i++){m.Position=V3.Lerp(from.Position,above,i/(float)steps);if(!AerialCollision.Clear(v,m))return false;}
            steps=Math.Max(1,(int)Math.Ceiling(Math.Abs(above.Y-to.Position.Y)/.3f));
            for(int i=1;i<=steps;i++){m.Position=V3.Lerp(above,to.Position,i/(float)steps);if(!AerialCollision.Clear(v,m))return false;}
            return true;
        }
    }
}
