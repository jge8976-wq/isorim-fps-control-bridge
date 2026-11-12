using System;

namespace HSK.FpsBridge
{
    internal static class MeleeReachRules
    {
        // Model coordinates are cells with +Z forward from PrimaryGrip. Rear handles do not add blade reach.
        public static float ForwardLength(float tipZ, float gripZ) => float.IsFinite(tipZ) && float.IsFinite(gripZ)
            ? Math.Clamp(tipZ - gripZ, 0f, 6f) : 0f;
        public static float Reach(float length, bool thrust)
        {
            length = float.IsFinite(length) ? Math.Clamp(length, 0f, 6f) : 0f;
            return (thrust ? .82f : .70f) + length * (thrust ? 1f : .95f);
        }
        public static int SearchRadius(float reach) => (int)Math.Ceiling(float.IsFinite(reach) ? Math.Clamp(reach, 0f, 7f) : 0f) + 2;
    }
}
