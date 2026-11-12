using System;

namespace HSK.FpsBridge
{
    internal static class AerialShotRules
    {
        // Low arc at the unchanged native speed. Includes the straight-up case without dividing by horizontal range.
        internal static bool Velocity(V3 delta,float speed,float gravity,out V3 velocity)
        {
            velocity=default;
            if(!delta.Finite||!float.IsFinite(speed)||speed<=0f||!float.IsFinite(gravity)||gravity<0f)return false;
            double distance2=delta.X*delta.X+(double)delta.Y*delta.Y+delta.Z*delta.Z;
            if(distance2<.000001)return false;
            double t;
            if(gravity<.0000001)t=Math.Sqrt(distance2)/speed;
            else
            {
                double b=speed*(double)speed-gravity*delta.Y,d=b*b-gravity*(double)gravity*distance2;
                if(d<0||b<=0)return false;
                t=Math.Sqrt(2*distance2/(b+Math.Sqrt(d)));
            }
            velocity=new V3((float)(delta.X/t),(float)(delta.Y/t+.5*gravity*t),(float)(delta.Z/t));
            return velocity.Finite;
        }
    }
}
