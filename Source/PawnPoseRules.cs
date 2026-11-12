using System;

namespace HSK.FpsBridge
{
    internal static class PawnPoseRules
    {
        internal static float Scale(string race) => race != null && race.StartsWith("Ratkin",StringComparison.OrdinalIgnoreCase) ? .5f : 1f;
        // Furniture/floors are world-space heights; never shrink them with the pawn.
        internal static float Eye(float standing,float scale,float posture,float lift,float support)
            => standing*scale*posture + lift + support;
        internal static bool SeatContains(float dx,float dz,float halfX,float halfZ)
            => Math.Abs(dx)<=halfX && Math.Abs(dz)<=halfZ;
    }
}
