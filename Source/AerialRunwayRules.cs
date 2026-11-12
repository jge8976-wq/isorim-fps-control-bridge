using System;

namespace HSK.FpsBridge
{
    public enum AerialRunwayMode { None, TakeoffRoll, TakeoffClimb, LandingGlide, LandingRoll }
    internal static class AerialRunwayRules
    {
        internal const float PreviewHeight=5f, HoverClearance=.25f, ClimbHeight=5f;
        internal static bool TakingOff(AerialRunwayMode mode)=>mode==AerialRunwayMode.TakeoffRoll||mode==AerialRunwayMode.TakeoffClimb;
        internal static float Roll(float maximum)=>TakeoffLength(maximum)*.6f;
        internal static float TakeoffLength(float maximum)=>Math.Clamp(8f+maximum*.5f,10f,20f);
        internal static float LandingLength(float maximum)=>Math.Max(14f,maximum*maximum*.25f/6f+6f);
        internal static float FlatSpeed(AerialMotor m)=>MathF.Sqrt(m.Velocity.X*m.Velocity.X+m.Velocity.Z*m.Velocity.Z);
        private static float Toward(float value,float goal,float step)=>value+Math.Clamp(goal-value,-step,step);
        internal static AerialMotor Taxi(AerialMotor m,float dt,int forward,int turn,float maximum)
        {
            if(!m.Finite||dt<=0||dt>.05f)return m;
            float a=m.Yaw*MathF.PI/180f,limit=Math.Min(3f,maximum*.25f);
            float speed=m.Velocity.X*MathF.Sin(a)+m.Velocity.Z*MathF.Cos(a);
            speed=Toward(speed,Math.Clamp(forward,-1,1)*limit*(forward<0?.45f:1f),3f*dt);
            var n=m;n.Yaw=(m.Yaw+turn*38f*Math.Clamp(speed/Math.Max(.1f,limit),-1f,1f)*dt+360f)%360f;
            a=n.Yaw*MathF.PI/180f;n.Velocity=new V3(MathF.Sin(a)*speed,0,MathF.Cos(a)*speed);
            n.Position=m.Position+n.Velocity*dt;n.Pitch=n.Roll=n.Throttle=0;return AerialFixedWing.Synchronize(n,false);
        }
        internal static AerialMotor Automatic(AerialMotor m,float dt,AerialFlightKind kind,float maximum,
            V3 start,V3 end,float yaw,ref AerialRunwayMode mode,out bool complete)
        {
            complete=false;if(!m.Finite||dt<=0||dt>.05f)return m;
            var n=m;float a=yaw*MathF.PI/180f,fx=MathF.Sin(a),fz=MathF.Cos(a),k=1-MathF.Exp(-4f*dt);
            n.Yaw=yaw;n.Roll=0;
            float speed=FlatSpeed(m);
            if(mode==AerialRunwayMode.TakeoffRoll)
            {
                float target=maximum*.72f,roll=Roll(maximum);
                float acceleration=target*target/(2f*roll);
                float nextSpeed=Toward(speed,target,acceleration*dt);
                float progress=(m.Position.X-start.X)*fx+(m.Position.Z-start.Z)*fz;
                float distance=Math.Min(roll,progress+(speed+nextSpeed)*.5f*dt);
                n.Position=new V3(start.X+fx*distance,start.Y,start.Z+fz*distance);
                n.Velocity=new V3(fx*nextSpeed,0,fz*nextSpeed);n.Pitch=0;n.Throttle=.72f;
                if(distance>=roll-.0001f)mode=AerialRunwayMode.TakeoffClimb;
            }
            else if(mode==AerialRunwayMode.TakeoffClimb)
            {
                if(kind==AerialFlightKind.FixedWing)
                {
                    float length=MathF.Sqrt((end.X-start.X)*(end.X-start.X)+(end.Z-start.Z)*(end.Z-start.Z));
                    float roll=Roll(maximum),progress=(m.Position.X-start.X)*fx+(m.Position.Z-start.Z)*fz;
                    float phase=Math.Clamp((progress-roll)/Math.Max(.01f,length-roll),0f,1f);
                    float phaseEnd=Math.Min(1f,phase+maximum*dt/Math.Max(.01f,length-roll));
                    float steepest=Math.Clamp(.5f,phase,phaseEnd);
                    float slope=6f*steepest*(1f-steepest)*(end.Y-start.Y)/Math.Max(.01f,length-roll);
                    float horizontalCap=maximum/MathF.Sqrt(1f+slope*slope);
                    speed=Math.Min(horizontalCap,Toward(speed,maximum*.72f,maximum*.3f*dt));
                    float distance=Math.Min(length,progress+speed*dt);
                    float fraction=Math.Clamp((distance-roll)/Math.Max(.01f,length-roll),0f,1f);
                    // Smooth altitude at both ends keeps long airframes clear during rotation.
                    float smooth=fraction*fraction*(3f-2f*fraction);
                    n.Position=new V3(start.X+fx*distance,start.Y+(end.Y-start.Y)*smooth,start.Z+fz*distance);
                    n.Velocity=(n.Position-m.Position)*(1f/dt);
                    n.Pitch=0;n.Throttle=.72f;
                    if(distance>=length-.0001f){n.Position=end;n.Velocity=new V3(fx*speed,0,fz*speed);complete=true;mode=AerialRunwayMode.None;}
                }
                else
                {
                    float wanted=Math.Min(Math.Min(3.5f,maximum),Math.Max(.5f,(end.Y-m.Position.Y)*2f));
                    n.Velocity=new V3(0,m.Velocity.Y+(wanted-m.Velocity.Y)*k,0);
                    n.Position=m.Position+n.Velocity*dt;n.Pitch=0;
                    if(n.Position.Y>=end.Y-.02f){n.Position.Y=end.Y;n.Velocity.Y=0;complete=true;mode=AerialRunwayMode.None;}
                }
            }
            else if(mode==AerialRunwayMode.LandingGlide)
            {
                float distance=(start.X-m.Position.X)*fx+(start.Z-m.Position.Z)*fz;
                speed=Toward(speed,Math.Min(maximum*.8f,Math.Max(3f,maximum*.5f)),maximum*.35f*dt);
                float drop=Math.Max(0,m.Position.Y-start.Y);
                float vy=-Math.Clamp(drop/Math.Max(.25f,distance)*speed,.12f,3f);
                n.Velocity=new V3(fx*speed,vy,fz*speed);n.Position=m.Position+n.Velocity*dt;
                n.Pitch=MathF.Atan2(-vy,Math.Max(.1f,speed))*180f/MathF.PI*Math.Clamp((n.Position.Y-start.Y)/1.5f,0,1);
                if(n.Position.Y<=start.Y+.02f||distance<=.05f)
                {n.Position.Y=start.Y;n.Pitch=0;n.Velocity.Y=0;mode=AerialRunwayMode.LandingRoll;}
            }
            else if(mode==AerialRunwayMode.LandingRoll)
            {
                speed=Toward(speed,0,3f*dt);n.Velocity=new V3(fx*speed,0,fz*speed);n.Position=m.Position+n.Velocity*dt;n.Position.Y=start.Y;n.Pitch=0;
                if(speed<.03f){n=n.Brake();complete=true;mode=AerialRunwayMode.None;}
            }
            return kind==AerialFlightKind.FixedWing?AerialFixedWing.Synchronize(n,mode==AerialRunwayMode.TakeoffClimb||complete&&n.Position.Y>start.Y+.1f):n;
        }
    }
}
