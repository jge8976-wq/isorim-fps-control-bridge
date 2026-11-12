using System;

namespace HSK.FpsBridge
{
    public enum AerialFlightKind { Rotorcraft, FixedWing, DropPod }
    internal struct AerialDoubleTap
    {
        private float last;
        private bool pending;
        internal bool Press(bool down,float now,bool enabled)
        {
            if(!enabled || !float.IsFinite(now)){pending=false;return false;}
            if(!down)return false;
            bool hit=pending&&now>=last&&now-last<=.34f;
            pending=!hit;last=now;return hit;
        }
    }
    internal static class AerialSafety
    {
        internal static bool ProjectionStep(V3 native, V3 proposed, float budget)
        {
            if (!native.Finite || !proposed.Finite || !float.IsFinite(budget) || budget < 0f) return false;
            float x=proposed.X-native.X,z=proposed.Z-native.Z;
            return x*x+z*z<=budget*budget;
        }
        internal static bool QuickMachineGun(bool aircraft) => !aircraft;
    }
    // True space, cells/second. No frame counters, map rows or Unity draw-layer heights in the motor.
    internal struct AerialMotor
    {
        internal V3 Position, Velocity;
        internal float Yaw, Pitch, Roll, Throttle;
        internal float WingAirSpeed,WingSink;internal bool WingAirReady;
        internal AerialMotor(V3 position, float yaw) { Position = position; Yaw = yaw; Velocity = default; Pitch = Roll = Throttle = 0f; WingAirSpeed=WingSink=0;WingAirReady=false; }
        internal bool Finite => Position.Finite && Velocity.Finite && float.IsFinite(Yaw) && float.IsFinite(Pitch) && float.IsFinite(Roll) && float.IsFinite(Throttle);
        internal AerialMotor Brake() { var copy = this; copy.Velocity = default; return copy; }
        internal AerialMotor Step(float dt, int forward, int turn, int climb, float maximumSpeed, float ceiling, bool powered = true, bool emergencyBoost = false)
        {
            if (!Finite || !float.IsFinite(dt) || dt <= 0f || dt > .05f) return this;
            var next = this;
            forward = Math.Clamp(forward, -1, 1); turn = Math.Clamp(turn, -1, 1); climb = Math.Clamp(climb, -1, 1);
            maximumSpeed = emergencyBoost ? Math.Max(.1f, maximumSpeed) : Math.Clamp(maximumSpeed, .1f, 24f);
            next.Yaw = (Yaw + (powered ? turn * 65f * dt : 0f) + 360f) % 360f;
            float yaw = next.Yaw * MathF.PI / 180f;
            float speed = powered ? forward * maximumSpeed * (forward < 0 ? .45f : 1f) : 0f;
            var target = new V3(MathF.Sin(yaw) * speed, powered ? climb * (climb < 0 ? 2.5f : 3.5f) : -1.2f, MathF.Cos(yaw) * speed);
            float smooth = 1f - MathF.Exp(-3f * dt);
            next.Velocity = Velocity + (target - Velocity) * smooth;
            next.Position = Position + next.Velocity * dt;
            if (next.Position.Y > ceiling) { next.Position.Y = ceiling; next.Velocity.Y = Math.Min(0f, next.Velocity.Y); }
            next.Pitch += ((powered ? forward * -9f : 0f) - Pitch) * smooth;
            next.Roll += ((powered ? -turn * 14f : 0f) - Roll) * smooth;
            return next;
        }
        internal AerialMotor StepKind(float dt,int forward,int side,int climb,float maximum,float ceiling,AerialFlightKind kind,bool grounded,bool powered,bool emergencyBoost=false)
        {
            if(kind==AerialFlightKind.Rotorcraft)return Step(dt,forward,side,climb,maximum,ceiling,powered,emergencyBoost);
            if(!Finite||!float.IsFinite(dt)||dt<=0||dt>.05f)return this;
            var next=this;float k=1f-MathF.Exp(-3f*dt);float a=Yaw*MathF.PI/180f;
            if(kind==AerialFlightKind.DropPod)
            {
                float limit=maximum;
                var target=new V3((MathF.Sin(a)*forward+MathF.Cos(a)*side)*limit,
                    powered?climb*(climb<0?2.8f:4f):-1.6f,(MathF.Cos(a)*forward-MathF.Sin(a)*side)*limit);
                if(!powered){target.X=target.Z=0;}
                float horizontal=MathF.Sqrt(target.X*target.X+target.Z*target.Z);
                if(horizontal>limit){target.X*=limit/horizontal;target.Z*=limit/horizontal;}
                next.Velocity=Velocity+(target-Velocity)*k;next.Position=Position+next.Velocity*dt;
                next.Pitch=Pitch*(1f-k);next.Roll=Roll*(1f-k);
            }
            else
            {
                next.Throttle=Math.Clamp(Throttle+forward*dt*.42f,0f,1f);
                float targetSpeed=maximum*next.Throttle;
                if(!grounded)targetSpeed=Math.Max(maximum*.38f,targetSpeed);
                if(!powered)targetSpeed=grounded?0f:maximum*.32f;
                float speed=MathF.Sqrt(Velocity.X*Velocity.X+Velocity.Z*Velocity.Z);
                speed+=(targetSpeed-speed)*k;
                next.Yaw=(Yaw+side*(grounded?35f:32f)*Math.Clamp(speed/(maximum*.4f),0f,1f)*dt+360)%360;
                float pitchTarget=climb*-18f;
                bool lift=!grounded || powered&&climb>0&&speed>=maximum*.32f;
                next.Pitch=grounded&&!lift?0f:Pitch+(pitchTarget-Pitch)*k;
                next.Roll=grounded?0f:Roll+(-side*25f-Roll)*k;
                a=next.Yaw*MathF.PI/180f;
                float vy=lift?(powered?-MathF.Sin(next.Pitch*MathF.PI/180f)*speed:-Math.Max(.8f,speed*.18f)):0f;
                next.Velocity=new V3(MathF.Sin(a)*speed,Velocity.Y+(vy-Velocity.Y)*k,MathF.Cos(a)*speed);
                next.Position=Position+next.Velocity*dt;
            }
            if(next.Position.Y>ceiling){next.Position.Y=ceiling;next.Velocity.Y=Math.Min(0,next.Velocity.Y);}
            return next;
        }
        internal AerialMotor LandingStep(float dt,V3 target,bool fixedWing)
        {
            if(!Finite||dt<=0||dt>.05f)return this;
            var next=this;float dx=target.X-Position.X,dz=target.Z-Position.Z;
            float distance=MathF.Sqrt(dx*dx+dz*dz),k=1f-MathF.Exp(-4f*dt);
            float speed=Math.Min(fixedWing?5f:3f,distance*1.4f);
            var desired=distance>.01f?new V3(dx/distance*speed,0,dz/distance*speed):default;
            float height=Math.Max(0,Position.Y-target.Y);
            desired.Y=distance>.5f?0f:-Math.Min(height<1.2f?.48f:1.8f,Math.Max(.12f,height*1.4f));
            if(fixedWing&&distance>.5f)desired.Y=-Math.Clamp(height/Math.Max(.25f,distance)*speed,.1f,3f);
            next.Velocity=Velocity+(desired-Velocity)*k;next.Position=Position+next.Velocity*dt;
            if(next.Position.Y<target.Y){next.Position.Y=target.Y;next.Velocity.Y=0;}
            next.Pitch=Pitch*(1f-k);next.Roll=Roll*(1f-k);next.Throttle=0;
            return next;
        }
    }
}
