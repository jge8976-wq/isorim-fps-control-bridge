using System;

namespace HSK.FpsBridge
{
    // No engine or CE dependency: CE elevation is radians and clockwise heading is NEGATIVE Unity yaw.
    internal static class CeNativeRules
    {
        internal static bool TryAngles(float x, float y, float z, out float elevation, out float rotation)
        {
            elevation = rotation = 0;
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) return false;
            double horizontal = Math.Sqrt((double)x * x + (double)z * z);
            if (horizontal < 0.00001) return false;
            elevation = (float)Math.Atan2(y, horizontal);
            rotation = (float)(-Math.Atan2(x, z) * 180 / Math.PI);
            return true;
        }

        internal static float HeadingDelta(float value, float basis)
        {
            float delta = (value - basis) % 360;
            if (delta > 180) delta -= 360;
            if (delta < -180) delta += 360;
            return delta;
        }

        internal static bool WantsTrigger(string mode, bool held, bool pressed) =>
            mode == "AutoFire" ? held || pressed : pressed;

        internal static int MagazineBurst(int rounds,int consumedPerShot) => Math.Max(1,rounds/Math.Max(1,consumedPerShot));
        internal static (float x,float y,float z) Pellet(float x,float y,float z,int index,int count,float degrees)
        {
            double length=Math.Sqrt(x*x+y*y+z*z);if(length<.00001)return(0,0,1);
            double fx=x/length,fy=y/length,fz=z/length;
            double horizontal=Math.Sqrt(fx*fx+fz*fz),rx=horizontal>.00001?fz/horizontal:1,rz=horizontal>.00001?-fx/horizontal:0;
            double ux=-fy*rz,uy=fz*rx-fx*rz,uz=fy*rx;
            double angle=index*2.399963229728653, radius=Math.Tan(Math.Clamp(degrees,0,45)*Math.PI/180)*Math.Sqrt((index+.5)/Math.Max(1,count));
            double a=Math.Cos(angle)*radius,b=Math.Sin(angle)*radius;
            double dx=fx+rx*a+ux*b,dy=fy+uy*b,dz=fz+rz*a+uz*b,n=Math.Sqrt(dx*dx+dy*dy+dz*dz);
            return((float)(dx/n),(float)(dy/n),(float)(dz/n));
        }

        internal static (float x,float y,float z,float verticalVelocity) FlightStep(float x,float y,float z,
            float vx,float vy,float vz,float gravity)
        {
            float vertical=vy-gravity;
            return(x+vx,y+vertical,z+vz,vertical);
        }

        internal static bool WantsReload(bool input, bool paused, bool busy, bool magazine,
            int rounds, int capacity, bool useAmmo, bool hasAmmo, bool manual, int now, int lastRequest) =>
            input && !paused && !busy && magazine && capacity > 0 && rounds < capacity
            && (!useAmmo || hasAmmo) && (manual || rounds == 0 && (long)now - lastRequest >= 60);
    }
}
