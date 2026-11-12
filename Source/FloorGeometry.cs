using System;

namespace HSK.FpsBridge
{
    // Engine-free coordinate contract: all rays/volumes use visible x/z and absolute height. Map rows are storage only.
    internal readonly struct FloorBand
    {
        public readonly int Level, Shift, Rows;
        public readonly float Height;
        public FloorBand(int level, int shift, int rows, float height) { Level = level; Shift = shift; Rows = rows; Height = height; }
        public bool Contains(int z) => z >= Shift && z < Shift + Rows;
        public V3 ToWorld(V3 logical) => new V3(logical.X, logical.Y + Height, logical.Z - Shift);
        public V3 ToLogical(V3 world) => new V3(world.X, world.Y - Height, world.Z + Shift);
    }

    internal static class FloorGeometry
    {
        public static bool Plane(V3 a, V3 b, float height, out float t)
        {
            t = 0f;
            float dy = b.Y - a.Y;
            if (Math.Abs(dy) < 1e-7f) return false;
            t = (height - a.Y) / dy;
            return t > 0f && t <= 1f;
        }
        // minAlong: how far toward the other end the pawn must already be. Going down from the exit, the cell ahead is the
        // open stairwell, which the avatar's physics will not enter: pushing toward it anywhere on the exit cell steps down.
        public const float DescendAlong = -.45f;

        public static bool ApproachingStep(V3 at, V3 direction, V3 source, V3 destination, float minAlong = .36f)
        {
            V3 d = destination - source; d.Y = 0f;
            float length = d.Length;
            if (length < .5f || length > 1.5f) return false;
            d = d * (1f / length);
            V3 rel = at - source; rel.Y = 0f;
            float along = V3.Dot(rel, d);
            float side = Math.Abs(rel.X * d.Z - rel.Z * d.X);
            return along >= minAlong && along <= .85f && side < .42f && V3.Dot(direction, d) > .45f;
        }
    }
}
