using System;

namespace HSK.FpsBridge
{
    internal static class BeamGeometry
    {
        // Beam range is a true 3D distance. Clip the segment to this map's visible X/Z footprint,
        // never to ProjectZ's vertically stacked storage rows. Collision with Y planes is traced separately.
        public static bool End(V3 start, V3 direction, float range, int columns, int rows, out V3 end)
        {
            end = start;
            if (!start.Finite || !direction.Finite || !float.IsFinite(range) || range <= 0f
                || columns <= 0 || rows <= 0 || start.X < 0f || start.X >= columns || start.Z < 0f || start.Z >= rows) return false;
            float length = direction.Length;
            if (!float.IsFinite(length) || length < .00001f) return false;
            V3 d = direction * (1f / length);
            float distance = range;
            Clip(start.X, d.X, columns, ref distance);
            Clip(start.Z, d.Z, rows, ref distance);
            if (distance <= .00001f) return false;
            end = start + d * distance;
            return end.Finite;
        }

        private static void Clip(float start, float direction, int size, ref float distance)
        {
            if (direction > .000001f) distance = Math.Min(distance, Math.Max(0f, size - .001f - start) / direction);
            else if (direction < -.000001f) distance = Math.Min(distance, Math.Max(0f, start - .001f) / -direction);
        }
    }
}
