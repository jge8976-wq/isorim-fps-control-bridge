using System;
using System.Numerics;
namespace HSK.FpsBridge
{
    internal static class VehicleImpulseRules
    {
        internal static void Bounce(ref Vector3 a,ref Vector3 b,Vector3 normal,float massA,float massB)
        {
            if(normal.LengthSquared()<1e-10f)normal=Vector3.UnitX;normal=Vector3.Normalize(normal);
            float ia=1/Math.Max(1,massA),ib=1/Math.Max(1,massB),closing=Vector3.Dot(a-b,normal);
            float impulse=(Math.Max(0,-closing)*1.35f+.3f)/(ia+ib);
            a+=normal*(impulse*ia);b-=normal*(impulse*ib);
        }
        internal static float Damage(float relativeSpeed,float massA,float massB)=>Math.Clamp(1+relativeSpeed*relativeSpeed*MathF.Sqrt(Math.Min(massA,massB))*.035f,1,1600);
        internal static bool Rearm(bool contacting,int lastContact,int now)=>!contacting&&now-lastContact>=3;
    }
}
