using System;
namespace HSK.FpsBridge
{
    // Speed is a true-space magnitude, not horizontal speed plus free vertical velocity.
    internal static class AerialFixedWing
    {
        internal const float AiYawRate = 115f;
        internal static float StallSpeed(float maximum)=>maximum*.25f;
        internal static bool Stalled(AerialMotor m,float maximum)=>m.WingAirSpeed<StallSpeed(maximum);
        internal static float Cap(float previous,bool brake,float dt)=>brake?Math.Max(.3f,previous-dt*.35f):1;
        internal static float EffectiveMaximum(float configured,float efficiency)=>configured*Math.Clamp(efficiency,.5f,1);
        internal static AerialMotor Synchronize(AerialMotor m,bool airborne)
        {
            m.WingAirReady=airborne;
            if(!airborne){m.WingAirSpeed=m.WingSink=0;return m;}
            float yaw=m.Yaw*MathF.PI/180,pitch=m.Pitch*MathF.PI/180;
            float projected=m.Velocity.X*MathF.Sin(yaw)+m.Velocity.Z*MathF.Cos(yaw);
            m.WingAirSpeed=Math.Max(0,projected/Math.Max(.1f,MathF.Cos(pitch)));
            m.WingSink=m.Velocity.Y+MathF.Sin(pitch)*m.WingAirSpeed;return m;
        }
        internal static AerialMotor Step(AerialMotor m,float dt,int forward,int side,int climb,float maximum,float ceiling,float aimYaw,float aimPitch,bool boost,float speedCap=-1,bool powered=true,float airframeMaximum=-1,float yawRate=75f,float agility=1f)
        {
            if(!m.Finite||dt<=0||dt>.05f||!float.IsFinite(maximum)||maximum<=0)return m;
            var n=m;float k=1-MathF.Exp(-6f*dt*agility);
            n.Throttle=speedCap<0?1f:Math.Clamp(speedCap/maximum,.3f,1f);
            float factor=boost?2f:1f,limit=speedCap<0?maximum:Math.Clamp(speedCap,0,maximum);
            // The configured maximum is the reference for stall, never the brake cap.
            // Sink velocity is separate: gravity cannot be clamped to zero with thrust.
            float target=powered?Math.Max(maximum*.3f,limit):0;
            float speed=m.WingAirReady?m.WingAirSpeed:MathF.Sqrt(m.Velocity.X*m.Velocity.X+m.Velocity.Z*m.Velocity.Z),acceleration=maximum*.75f*factor;
            speed+=Math.Clamp(target-speed,-acceleration*dt,acceleration*dt);
            if(powered)speed=Math.Min(speed,maximum);
            float yawError=AerialGuidance.Delta(m.Yaw,aimYaw)+Math.Clamp(side,-1,1)*75f;
            n.Yaw=(m.Yaw+Math.Clamp(yawError,-yawRate*dt,yawRate*dt)+360)%360;
            float pitch=climb!=0?-climb*35f:Math.Clamp(aimPitch,-65f,70f);
            n.Pitch=m.Pitch+Math.Clamp((pitch-m.Pitch)*6f,-65f,65f)*dt*agility;
            n.Roll=m.Roll+(-Math.Clamp(yawError/35f,-1,1)*40f-m.Roll)*k;
            float y=n.Yaw*MathF.PI/180f,p=n.Pitch*MathF.PI/180f,c=MathF.Cos(p);
            n.WingAirSpeed=Math.Max(0,speed);n.WingAirReady=true;
            float lift=Math.Clamp(speed/Math.Max(.1f,StallSpeed(airframeMaximum>0?airframeMaximum:maximum)),0,1);lift*=lift;
            n.WingSink=m.WingSink-9.81f*(1-lift)*dt;
            if(lift>.98f)n.WingSink+=Math.Clamp(-n.WingSink,-6f*dt,6f*dt);
            n.WingSink=Math.Clamp(n.WingSink,-45,10);
            n.Velocity=new V3(MathF.Sin(y)*c*speed,-MathF.Sin(p)*speed+n.WingSink,MathF.Cos(y)*c*speed);
            n.Position=m.Position+n.Velocity*dt;
            if(n.Position.Y>ceiling){n.Position.Y=ceiling;n.Velocity.Y=Math.Min(0,n.Velocity.Y);}
            return n;
        }
    }
}
