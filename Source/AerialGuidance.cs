using System;

namespace HSK.FpsBridge
{
    internal static class AerialGuidance
    {
        internal static float AiRadius(float speed, float span) =>
            Math.Max(6f, Math.Max(span * 1.1f, speed / (WingYawRate * MathF.PI / 180f) * 1.4f));
        internal static float Agility => FpsMod.Settings?.aircraftAgility ?? .6f;
        internal static float WingYawRate => AerialFixedWing.AiYawRate * Agility;
        internal static float Radius(float speed, float span) => Math.Max(9f, Math.Max(span * 1.2f, speed / (32f * MathF.PI / 180f) * 1.4f));
        internal static float Heading(AerialMotor m, V3 target, float radius, bool orbit)
        {
            float dx=m.Position.X-target.X,dz=m.Position.Z-target.Z,d=MathF.Sqrt(dx*dx+dz*dz);
            if(!orbit)return MathF.Atan2(-dx,-dz)*180f/MathF.PI;
            if(d<.01f){dx=0;dz=1;d=1;}
            float correction=Math.Clamp((radius-d)/Math.Max(3f,radius*.4f),-1.5f,1.5f);
            return MathF.Atan2(dz+dx*correction,-dx+dz*correction)*180f/MathF.PI;
        }
        internal static float Delta(float a,float b)=>(b-a+540f)%360f-180f;
        internal static AerialMotor Step(AerialMotor motor, float dt, V3 target, float radius, bool orbit,
            float height,float maximum,float ceiling,AerialFlightKind kind,bool grounded,bool powered)
        {
            if(!motor.Finite||!target.Finite||!float.IsFinite(height)||dt<=0||dt>.05f)return motor;
            if(!powered)return motor.StepKind(dt,0,0,0,maximum,ceiling,kind,grounded,false);
            float heading=Heading(motor,target,radius,orbit),error=Delta(motor.Yaw,heading);
            var driven=motor;
            if(kind==AerialFlightKind.FixedWing)driven.Throttle=Math.Clamp(motor.Throttle+Math.Clamp(.65f-motor.Throttle,-.42f*dt,.42f*dt),0,1);
            var next=driven.StepKind(dt,0,0,
                Math.Abs(height-motor.Position.Y)<.12f?0:height>motor.Position.Y?1:-1,maximum,ceiling,kind,grounded,powered);
            float turn=Math.Clamp(error,-32f*dt,32f*dt);next.Yaw=(motor.Yaw+turn+360f)%360f;
            if(kind==AerialFlightKind.FixedWing)
            {
                float speed=MathF.Sqrt(next.Velocity.X*next.Velocity.X+next.Velocity.Z*next.Velocity.Z),a=next.Yaw*MathF.PI/180f;
                next.Velocity.X=MathF.Sin(a)*speed;next.Velocity.Z=MathF.Cos(a)*speed;
                next.Position.X=motor.Position.X+next.Velocity.X*dt;next.Position.Z=motor.Position.Z+next.Velocity.Z*dt;
                next.Roll+=(-Math.Clamp(error/25f,-1,1)*25f-next.Roll)*(1-MathF.Exp(-3f*dt));
            }
            else
            {
                float dx=target.X-motor.Position.X,dz=target.Z-motor.Position.Z,d=MathF.Sqrt(dx*dx+dz*dz);
                float speed=orbit?maximum*.7f:Math.Min(maximum*.7f,d*1.5f);
                float angle=heading*MathF.PI/180f;
                float k=1-MathF.Exp(-3f*dt);
                next.Velocity.X=motor.Velocity.X+(MathF.Sin(angle)*speed-motor.Velocity.X)*k;
                next.Velocity.Z=motor.Velocity.Z+(MathF.Cos(angle)*speed-motor.Velocity.Z)*k;
                next.Position.X=motor.Position.X+next.Velocity.X*dt;next.Position.Z=motor.Position.Z+next.Velocity.Z*dt;
            }
            float vertical=Math.Clamp((height-motor.Position.Y)*1.2f,-2.5f,3.5f);
            next.Velocity.Y=motor.Velocity.Y+(vertical-motor.Velocity.Y)*(1-MathF.Exp(-3f*dt));
            next.Position.Y=Math.Min(ceiling,motor.Position.Y+next.Velocity.Y*dt);
            if(kind==AerialFlightKind.FixedWing)next.Pitch=-MathF.Atan2(next.Velocity.Y,Math.Max(.5f,MathF.Sqrt(next.Velocity.X*next.Velocity.X+next.Velocity.Z*next.Velocity.Z)))*180f/MathF.PI;
            return next;
        }
        internal static float Impact(float speed,float footprint) => speed<2.5f?0f:Math.Clamp(speed*speed*MathF.Sqrt(Math.Max(1,footprint))*1.5f,20f,1600f);
        internal static AerialMotor MouseStep(AerialMotor m,float dt,int forward,int side,int climb,float maximum,float ceiling,AerialFlightKind kind,bool grounded,bool powered,float aimYaw,float aimPitch,bool boost=false)
        {
            if(!powered||!float.IsFinite(aimYaw)||!float.IsFinite(aimPitch))return m.StepKind(dt,forward,side,climb,maximum,ceiling,kind,grounded,powered);
            if(kind==AerialFlightKind.FixedWing&&!grounded&&powered)return AerialFixedWing.Step(m,dt,forward,side,climb,maximum,ceiling,aimYaw,aimPitch,boost);
            var next=m.StepKind(dt,forward,kind==AerialFlightKind.DropPod?side:0,climb,maximum,ceiling,kind,grounded,powered);
            float turn=Math.Clamp(Delta(m.Yaw,aimYaw),-(kind==AerialFlightKind.FixedWing?32f:65f)*dt,(kind==AerialFlightKind.FixedWing?32f:65f)*dt);
            if(grounded&&kind==AerialFlightKind.FixedWing)turn*=.6f;
            next.Yaw=(m.Yaw+turn+360f)%360f;
            float wanted=Math.Clamp(aimPitch,-30f,35f),k=1-MathF.Exp(-3f*dt);
            if(climb!=0)wanted=-climb*18f;
            next.Pitch=grounded?next.Pitch:m.Pitch+(wanted-m.Pitch)*k;
            if(kind!=AerialFlightKind.DropPod)
            {
                float speed=MathF.Sqrt(next.Velocity.X*next.Velocity.X+next.Velocity.Z*next.Velocity.Z),a=next.Yaw*MathF.PI/180f;
                if(kind==AerialFlightKind.Rotorcraft&&forward<0)speed=-speed;
                next.Velocity.X=MathF.Sin(a)*speed;next.Velocity.Z=MathF.Cos(a)*speed;
                if(kind==AerialFlightKind.Rotorcraft)
                {
                    float tx=(MathF.Sin(a)*forward+MathF.Cos(a)*side)*maximum,tz=(MathF.Cos(a)*forward-MathF.Sin(a)*side)*maximum;
                    float magnitude=MathF.Sqrt(tx*tx+tz*tz);if(magnitude>maximum){tx*=maximum/magnitude;tz*=maximum/magnitude;}
                    next.Velocity.X=m.Velocity.X+(tx-m.Velocity.X)*k;next.Velocity.Z=m.Velocity.Z+(tz-m.Velocity.Z)*k;
                }
                next.Position.X=m.Position.X+next.Velocity.X*dt;next.Position.Z=m.Position.Z+next.Velocity.Z*dt;
                if(!grounded&&climb==0)
                {
                    float vy=Math.Clamp(-MathF.Sin(next.Pitch*MathF.PI/180f)*Math.Abs(speed),-4.5f,4.5f);
                    next.Velocity.Y=m.Velocity.Y+(vy-m.Velocity.Y)*k;next.Position.Y=Math.Min(ceiling,m.Position.Y+next.Velocity.Y*dt);
                }
                next.Roll=m.Roll+(-Math.Clamp(Delta(m.Yaw,aimYaw)/25f,-1,1)*25f-m.Roll)*k;
            }
            return next;
        }
    }
}
