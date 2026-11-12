using System;

namespace HSK.FpsBridge
{
    internal static class AerialBoundaryRules
    {
        internal static bool Bounce(ref AerialMotor motor,V3 min,V3 max,float width,float depth)
        {
            const float margin=0f;
            bool x=min.X<margin||max.X>width-margin,z=min.Z<margin||max.Z>depth-margin;
            if(!x&&!z)return false;
            if(x)
            {
                float shift=min.X<margin?margin-min.X:width-margin-max.X;
                motor.Position.X+=shift;motor.Velocity.X=Math.Sign(shift)*Math.Max(1.5f,Math.Abs(motor.Velocity.X)*.8f);
            }
            if(z)
            {
                float shift=min.Z<margin?margin-min.Z:depth-margin-max.Z;
                motor.Position.Z+=shift;motor.Velocity.Z=Math.Sign(shift)*Math.Max(1.5f,Math.Abs(motor.Velocity.Z)*.8f);
            }
            motor.Yaw=(MathF.Atan2(motor.Velocity.X,motor.Velocity.Z)*180f/MathF.PI+360f)%360f;
            motor.Roll=0;return true;
        }
    }
}
