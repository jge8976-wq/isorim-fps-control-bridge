using System;

namespace HSK.FpsBridge
{
    internal static class AimGeometry
    {
        // Segment/slab intersection, shared with the offline edge-case tests.
        public static bool Slab(float origin, float direction, float min, float max, ref float near, ref float far)
        {
            if (!float.IsFinite(origin) || !float.IsFinite(direction)) return false;
            if (Math.Abs(direction) < 0.000001f) return origin >= min && origin <= max;
            float a = (min - origin) / direction, b = (max - origin) / direction;
            if (a > b) (a, b) = (b, a);
            near = Math.Max(near, a); far = Math.Min(far, b);
            return near <= far;
        }
    }
}
