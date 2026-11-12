using System;
using Verse;
namespace HSK.FpsBridge
{
    // Resolve translation independently of rotation. A rejected wing rotation must not
    // discard a safe climb/reverse/sideways move and lock the aircraft against a wall.
    internal static class AerialSlide
    {
        internal static AerialMotor Resolve(Pawn v,AerialMotor before,AerialMotor desired)
        {
            var result=before;
            var rotated=desired;rotated.Position=before.Position;
            if(AerialCollision.Clear(v,rotated))result=rotated;
            result.Velocity=desired.Velocity;
            // Lift first; then retain both horizontal components whenever they fit.
            for(int axis=0;axis<3;axis++)
            {
                var test=result;
                if(axis==0)test.Position.Y=desired.Position.Y;
                else if(axis==1)test.Position.X=desired.Position.X;
                else test.Position.Z=desired.Position.Z;
                if(AerialCollision.Clear(v,test))result=test;
                else if(axis==0)result.Velocity.Y=0;
                else if(axis==1)result.Velocity.X=0;
                else result.Velocity.Z=0;
            }
            // Once clear of the obstacle, apply the desired attitude if it fits too.
            var turned=desired;turned.Position=result.Position;turned.Velocity=result.Velocity;
            if(AerialCollision.Clear(v,turned))result=turned;
            return result;
        }
    }
}
