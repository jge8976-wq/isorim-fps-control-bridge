using System;

namespace HSK.FpsBridge
{
    // Real-time tap/hold arbitration, independent of game speed. A cancelled press never becomes an interaction.
    internal sealed class LeanKeys
    {
        public const float HoldSeconds = .2f;
        private bool previousE, pending, held, blockE, blockQ;
        private float began;
        public bool TapE { get; private set; }
        public int Direction { get; private set; }

        public void Cancel(bool q, bool e)
        {
            previousE = e; blockE = e; blockQ = q;
            pending = held = TapE = false; Direction = 0;
        }

        public void Step(bool allowed, bool q, bool e, float now)
        {
            if (!allowed || !float.IsFinite(now)) { Cancel(q, e); return; }
            TapE = false;
            if (!q) blockQ = false;
            if (!e) blockE = false;
            if (e && !previousE && !blockE) { pending = true; held = false; began = now; }
            if (pending && now - began >= HoldSeconds) held = true;
            if (!e && previousE)
            {
                TapE = pending && !held;
                pending = held = false;
            }
            bool left = q && !blockQ, right = e && pending && held;
            Direction = left == right ? 0 : left ? -1 : 1;
            previousE = e;
        }
    }

    // One foot-anchored shear for the drawn figure and fallback hit volumes; the eye uses its full head offset.
    internal static class LeanGeometry
    {
        public static V3 Point(V3 p, V3 slope, float footY) => p + slope * (p.Y - footY);

        public static HitVolume Volume(HitVolume v, V3 slope, float footY)
        {
            v.A = Point(v.A, slope, footY);
            if (v.Kind == VolumeKind.Box)
            {
                V3 r = HitMath.Right(v.Yaw), f = HitMath.Forward(v.Yaw);
                v.B.X += Math.Abs(V3.Dot(r, slope)) * v.B.Y;
                v.B.Z += Math.Abs(V3.Dot(f, slope)) * v.B.Y;
            }
            else
            {
                if (v.Kind == VolumeKind.Capsule) v.B = Point(v.B, slope, footY);
                float s = slope.Length;
                v.Radius *= ((float)Math.Sqrt(s * s + 4f) + s) * .5f;
            }
            return v;
        }
    }
}
