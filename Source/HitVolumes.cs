using System;
using System.Collections.Generic;

namespace HSK.FpsBridge
{
    // Engine-independent 3D hit volumes for first-person ballistics; also exercised by the offline contracts.
    // Space: x east, z north (map cells), y height above the ground in cells (IsoRim's true height, unsquashed).
    // Yaw: degrees clockwise from north, as RimWorld's Rot4.AsAngle and IsoRim's FirstPerson.Yaw.
    internal struct V3
    {
        public float X, Y, Z;
        public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static V3 operator +(V3 a, V3 b) => new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V3 operator -(V3 a, V3 b) => new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V3 operator *(V3 a, float s) => new V3(a.X * s, a.Y * s, a.Z * s);
        public static float Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public float Length => (float)Math.Sqrt(X * X + Y * Y + Z * Z);
        public static V3 Lerp(V3 a, V3 b, float t) => a + (b - a) * t;
        public bool Finite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);
    }

    internal enum HitRegion { None, Head, Torso, Leg, Solid }
    internal enum BodyShape { Upright, Long, Lying }
    internal enum VolumeKind { Sphere, Capsule, Box }

    internal struct HitVolume
    {
        public VolumeKind Kind;
        public HitRegion Region;
        public int Leg;          // index into the caller's leg list, -1 when not a leg
        public int Part;         // 0.6.0: index of the body part this volume damages (BodyDef.AllParts order), -1 none
        public V3 A, B;          // sphere: A; capsule: A..B; box: centre A, half extents B (local: lateral, up, forward)
        public float Radius;     // sphere/capsule radius
        public bool Oriented;
        public V3 AxisRight, AxisUp, AxisForward;
        public float Yaw;        // box yaw
    }

    // One leg of a body as the volumes need it: which side (-1 left, +1 right of the facing) and front/rear (+1, -1, 0).
    internal struct LegSlot
    {
        public int Side, Front;
        public LegSlot(int side, int front) { Side = side; Front = front; }
    }

    internal static class HitMath
    {
        public static V3 Forward(float yaw)
        {
            double r = yaw * Math.PI / 180.0;
            return new V3((float)Math.Sin(r), 0f, (float)Math.Cos(r));
        }

        public static V3 Right(float yaw)
        {
            double r = yaw * Math.PI / 180.0;
            return new V3((float)Math.Cos(r), 0f, -(float)Math.Sin(r));
        }

        // Direction of a view: yaw clockwise from north, pitch degrees below the horizon (IsoRim's convention).
        public static V3 Direction(float yaw, float pitch)
        {
            double y = yaw * Math.PI / 180.0, p = pitch * Math.PI / 180.0;
            return new V3((float)(Math.Sin(y) * Math.Cos(p)), -(float)Math.Sin(p), (float)(Math.Cos(y) * Math.Cos(p)));
        }

        // Closest distance from point p to segment q0..q1.
        public static float PointSegment(V3 p, V3 q0, V3 q1)
        {
            V3 d = q1 - q0;
            float len2 = V3.Dot(d, d);
            float t = len2 > 1e-9f ? Math.Clamp(V3.Dot(p - q0, d) / len2, 0f, 1f) : 0f;
            return (p - (q0 + d * t)).Length;
        }

        // First fraction t (0..1) of segment a..b inside the volume, or false. A start point already inside counts as t = 0.
        public static bool Enter(V3 a, V3 b, in HitVolume v, out float t)
        {
            t = 0f;
            if (!a.Finite || !b.Finite) return false;
            switch (v.Kind)
            {
                case VolumeKind.Box: return EnterBox(a, b, v, out t);
                case VolumeKind.Sphere: return EnterRound(a, b, v.A, v.A, v.Radius, out t);
                default: return EnterRound(a, b, v.A, v.B, v.Radius, out t);
            }
        }

        // Sphere and capsule: the distance from the moving point to the core segment is convex along a..b, so the entry
        // lies between the start and the closest approach; it is found by bisection.
        private static bool EnterRound(V3 a, V3 b, V3 q0, V3 q1, float r, out float t)
        {
            t = 0f;
            if (PointSegment(a, q0, q1) <= r) return true;
            // Closest approach by ternary search on the convex distance.
            float lo = 0f, hi = 1f;
            for (int i = 0; i < 40; i++)
            {
                float m1 = lo + (hi - lo) / 3f, m2 = hi - (hi - lo) / 3f;
                if (PointSegment(V3.Lerp(a, b, m1), q0, q1) < PointSegment(V3.Lerp(a, b, m2), q0, q1)) hi = m2; else lo = m1;
            }
            float best = (lo + hi) * 0.5f;
            if (PointSegment(V3.Lerp(a, b, best), q0, q1) > r) return false;
            lo = 0f; hi = best;
            for (int i = 0; i < 30; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (PointSegment(V3.Lerp(a, b, mid), q0, q1) <= r) hi = mid; else lo = mid;
            }
            t = hi;
            return true;
        }

        private static bool EnterBox(V3 a, V3 b, in HitVolume v, out float t)
        {
            t = 0f;
            V3 right = Right(v.Yaw), fwd = Forward(v.Yaw);
            V3 la = BoxLocal(a - v.A, v), lb = BoxLocal(b - v.A, v);
            V3 d = lb - la;
            float near = 0f, far = 1f;
            if (!AimGeometry.Slab(la.X, d.X, -v.B.X, v.B.X, ref near, ref far)) return false;
            if (!AimGeometry.Slab(la.Y, d.Y, -v.B.Y, v.B.Y, ref near, ref far)) return false;
            if (!AimGeometry.Slab(la.Z, d.Z, -v.B.Z, v.B.Z, ref near, ref far)) return false;
            t = near;
            return true;
        }

        private static V3 BoxLocal(V3 p, in HitVolume v) => v.Oriented
            ? new V3(V3.Dot(p, v.AxisRight), V3.Dot(p, v.AxisUp), V3.Dot(p, v.AxisForward))
            : Local(p, Right(v.Yaw), Forward(v.Yaw));
        private static V3 Local(V3 p, V3 right, V3 fwd) => new V3(V3.Dot(p, right), p.Y, V3.Dot(p, fwd));

        // 0.6.0: the part of segment a..b inside the volume. tIn: entry fraction (0 when a is inside); tOut: exit
        // fraction, +infinity when b is still inside. False when the segment never enters, or only touches the
        // boundary on its way out (a start on the exit face does not count again).
        public static bool Span(V3 a, V3 b, in HitVolume v, out float tIn, out float tOut)
        {
            tIn = 0f; tOut = float.PositiveInfinity;
            if (!a.Finite || !b.Finite) return false;
            if (v.Kind == VolumeKind.Box)
            {
                V3 right = Right(v.Yaw), fwd = Forward(v.Yaw);
                V3 la = BoxLocal(a - v.A, v), lb = BoxLocal(b - v.A, v), d = lb - la;
                float lo = float.NegativeInfinity, hi = float.PositiveInfinity;
                if (!Line(la.X, d.X, v.B.X, ref lo, ref hi) || !Line(la.Y, d.Y, v.B.Y, ref lo, ref hi) || !Line(la.Z, d.Z, v.B.Z, ref lo, ref hi)) return false;
                if (hi < 1e-5f || lo > 1f || lo > hi) return false;
                tIn = Math.Max(0f, lo);
                tOut = hi > 1f ? float.PositiveInfinity : hi;
                return tOut > tIn + 1e-6f || float.IsPositiveInfinity(tOut);
            }
            V3 q0 = v.A, q1 = v.Kind == VolumeKind.Sphere ? v.A : v.B;
            float r = v.Radius;
            bool inside = PointSegment(a, q0, q1) <= r;
            if (!inside && !EnterRound(a, b, q0, q1, r, out tIn)) return false;
            if (PointSegment(b, q0, q1) <= r) { tOut = float.PositiveInfinity; return true; }
            // Exit: the convex distance crosses r once after the entry.
            float lo2 = tIn, hi2 = 1f;
            // From inside, step to the deepest point first so the bisection brackets the exit.
            float deep = lo2, best = PointSegment(V3.Lerp(a, b, lo2), q0, q1);
            for (int i = 1; i <= 16; i++)
            {
                float t = lo2 + (hi2 - lo2) * i / 16f;
                float dist = PointSegment(V3.Lerp(a, b, t), q0, q1);
                if (dist < best) { best = dist; deep = t; }
            }
            lo2 = deep;
            for (int i = 0; i < 30; i++)
            {
                float mid = (lo2 + hi2) * 0.5f;
                if (PointSegment(V3.Lerp(a, b, mid), q0, q1) <= r) lo2 = mid; else hi2 = mid;
            }
            tOut = hi2;
            if (tOut < 1e-5f) return false;
            return true;
        }

        // The whole line's parameter interval inside |x0 + d t| <= half.
        private static bool Line(float x0, float d, float half, ref float lo, ref float hi)
        {
            if (Math.Abs(d) < 1e-9f) return x0 >= -half && x0 <= half;
            float t0 = (-half - x0) / d, t1 = (half - x0) / d;
            if (t0 > t1) (t0, t1) = (t1, t0);
            lo = Math.Max(lo, t0); hi = Math.Min(hi, t1);
            return lo <= hi;
        }

        // Outward unit normal of the volume's surface at (or nearest to) a contact point: a box face by the largest scaled
        // local coordinate; a sphere or capsule radially from its core. Used to bounce thrown grenades.
        public static V3 BoxNormal(in HitVolume v, V3 p)
        {
            if (v.Kind == VolumeKind.Box)
            {
                V3 right = v.Oriented ? v.AxisRight : Right(v.Yaw), fwd = v.Oriented ? v.AxisForward : Forward(v.Yaw);
                V3 up = v.Oriented ? v.AxisUp : new V3(0f, 1f, 0f);
                V3 l = BoxLocal(p - v.A, v);
                float ax = Math.Abs(l.X) / Math.Max(1e-5f, v.B.X), ay = Math.Abs(l.Y) / Math.Max(1e-5f, v.B.Y), az = Math.Abs(l.Z) / Math.Max(1e-5f, v.B.Z);
                if (ay >= ax && ay >= az) return up * (l.Y >= 0f ? 1f : -1f);
                return ax >= az ? right * (l.X >= 0f ? 1f : -1f) : fwd * (l.Z >= 0f ? 1f : -1f);
            }
            V3 q0 = v.A, q1 = v.Kind == VolumeKind.Sphere ? v.A : v.B, d = q1 - q0;
            float len2 = V3.Dot(d, d);
            float t = len2 > 1e-9f ? Math.Clamp(V3.Dot(p - q0, d) / len2, 0f, 1f) : 0f;
            V3 n = p - (q0 + d * t);
            float len = n.Length;
            return len > 1e-6f ? n * (1f / len) : new V3(0f, 1f, 0f);
        }

        // Reflect a velocity off a surface (horizontal faces and walls alike), keeping `keep` of the speed along the face
        // and `bounce` of the speed into it.
        public static V3 Reflect(V3 vel, V3 normal, float keep, float bounce)
        {
            float into = V3.Dot(vel, normal);
            V3 along = vel - normal * into;
            return along * keep - normal * (into * bounce);
        }

        // The nearest volume crossed by a..b; index -1 when none.
        public static int Nearest(V3 a, V3 b, List<HitVolume> volumes, out float t)
        {
            int hit = -1;
            t = float.PositiveInfinity;
            for (int i = 0; i < volumes.Count; i++)
            {
                HitVolume v = volumes[i];
                if (Enter(a, b, v, out float ti) && ti < t) { t = ti; hit = i; }
            }
            return hit;
        }
    }

    // Three-part bodies: head (for long bodies the front, pointed end), torso (arms and hands included) and one volume per leg.
    internal static class BodyVolumes
    {
        public static void Build(List<HitVolume> into, BodyShape shape, V3 foot, float yaw, float height, float size, IReadOnlyList<LegSlot> legs, IReadOnlyList<bool> legPresent)
        {
            float k = (float)Math.Sqrt(Math.Max(0.05f, size));
            V3 f = HitMath.Forward(yaw), r = HitMath.Right(yaw);
            switch (shape)
            {
                case BodyShape.Upright:
                {
                    float hip = 0.45f * height, shoulder = 0.82f * height;
                    float headR = Math.Max(0.08f, 0.11f * height);
                    into.Add(Sphere(HitRegion.Head, foot + new V3(0f, height - headR, 0f), headR));
                    into.Add(Box(HitRegion.Torso, foot + new V3(0f, (hip + shoulder) * 0.5f, 0f), new V3(0.22f * k, (shoulder - hip) * 0.5f, 0.13f * k), yaw));
                    float legR = 0.075f * k;
                    for (int i = 0; i < legs.Count; i++)
                    {
                        if (!legPresent[i]) continue;
                        float lateral = (legs.Count == 1 ? 0f : legs[i].Side) * 0.1f * k;
                        float ahead = legs[i].Front * 0.08f * k;
                        V3 at = foot + r * lateral + f * ahead;
                        into.Add(Capsule(HitRegion.Leg, i, at + new V3(0f, legR, 0f), at + new V3(0f, hip, 0f), legR));
                    }
                    break;
                }
                case BodyShape.Long:
                {
                    float length = Math.Clamp(0.25f + 0.7f * k, 0.35f, 2.8f), width = 0.45f * length;
                    float belly = 0.45f * height;
                    float headR = Math.Clamp(0.14f * Math.Max(length, height), 0.07f, 0.6f);
                    into.Add(Sphere(HitRegion.Head, foot + f * (0.5f * length - headR * 0.5f) + new V3(0f, Math.Max(headR, 0.72f * height), 0f), headR));
                    into.Add(Box(HitRegion.Torso, foot + f * (-0.05f * length) + new V3(0f, (belly + height * 0.92f) * 0.5f, 0f),
                        new V3(0.5f * width, (height * 0.92f - belly) * 0.5f, 0.36f * length), yaw));
                    float legR = 0.03f + 0.05f * k;
                    int n = legs.Count;
                    for (int i = 0; i < n; i++)
                    {
                        if (!legPresent[i]) continue;
                        float ahead = legs[i].Front != 0 ? legs[i].Front * 0.28f * length : (n <= 1 ? 0f : (0.3f - 0.6f * (i / 2) / Math.Max(1, (n - 1) / 2)) * length);
                        float lateral = (legs[i].Side != 0 ? legs[i].Side : (i % 2 == 0 ? -1 : 1)) * 0.35f * width;
                        V3 at = foot + f * ahead + r * lateral;
                        into.Add(Capsule(HitRegion.Leg, i, at + new V3(0f, legR, 0f), at + new V3(0f, Math.Max(belly, legR * 2f), 0f), legR));
                    }
                    break;
                }
                default:
                {
                    // Lying: a low body along the facing, head at the front, legs behind.
                    float length = Math.Clamp(0.5f + 0.9f * k, 0.5f, 2.6f), low = Math.Clamp(0.18f * k, 0.12f, 0.45f);
                    float headR = Math.Clamp(0.11f * k + 0.04f, 0.08f, 0.35f);
                    into.Add(Sphere(HitRegion.Head, foot + f * (0.5f * length - headR) + new V3(0f, headR, 0f), headR));
                    into.Add(Box(HitRegion.Torso, foot + f * (0.05f * length) + new V3(0f, low * 0.5f, 0f), new V3(0.22f * k, low * 0.5f, 0.22f * length), yaw));
                    float legR = Math.Max(0.06f, 0.07f * k);
                    for (int i = 0; i < legs.Count; i++)
                    {
                        if (!legPresent[i]) continue;
                        float lateral = (legs[i].Side != 0 ? legs[i].Side : (i % 2 == 0 ? -1 : 1)) * 0.09f * k;
                        V3 hip = foot + f * (-0.15f * length) + r * lateral + new V3(0f, legR, 0f);
                        into.Add(Capsule(HitRegion.Leg, i, hip, hip + f * (-0.32f * length), legR));
                    }
                    break;
                }
            }
        }

        public static HitVolume Sphere(HitRegion region, V3 c, float radius) =>
            new HitVolume { Kind = VolumeKind.Sphere, Region = region, Leg = -1, Part = -1, A = c, B = c, Radius = radius };

        public static HitVolume Capsule(HitRegion region, int leg, V3 a, V3 b, float radius) =>
            new HitVolume { Kind = VolumeKind.Capsule, Region = region, Leg = leg, Part = -1, A = a, B = b, Radius = radius };

        public static HitVolume Box(HitRegion region, V3 centre, V3 half, float yaw) =>
            new HitVolume { Kind = VolumeKind.Box, Region = region, Leg = -1, Part = -1, A = centre, B = half, Yaw = yaw };

        private static HitVolume Tag(HitVolume v, int part, int leg = -1) { v.Part = part; v.Leg = leg; return v; }

        // ---- 0.6.0 fine bodies: every volume belongs to one real body part (plan index); missing parts have none.

        public static void BuildFine(List<HitVolume> into, BodyShape shape, V3 foot, float yaw, float height, float size, BodyPlan plan, IReadOnlyList<bool> present)
        {
            float k = (float)Math.Sqrt(Math.Max(0.05f, size));
            V3 f = HitMath.Forward(yaw), r = HitMath.Right(yaw), up = new V3(0f, 1f, 0f);
            bool Has(int part) => part >= 0 && part < present.Count && present[part];
            void Add(HitVolume v, int part, int leg = -1) { if (Has(part)) into.Add(Tag(v, part, leg)); }
            int torso = plan.Torso;
            if (shape == BodyShape.Upright)
            {
                // Proportions of a standing figure (IsoRim's height): head 19% of the height, a short neck, shoulders at 78%.
                float hip = 0.45f * height, shoulder = 0.78f * height;
                float headR = Math.Max(0.075f, 0.095f * height);
                float headY = height - headR;
                Add(Sphere(HitRegion.Head, foot + up * headY, headR), plan.HeadOr());
                if (plan.Neck >= 0)
                    Add(Capsule(HitRegion.Head, -1, foot + up * (shoulder - 0.02f * height), foot + up * (headY - headR * 0.5f), 0.045f * k), plan.Neck);
                bool arms = plan.Humanoid;
                float torsoHalf = arms ? 0.14f * k : 0.22f * k;
                Add(Box(HitRegion.Torso, foot + up * ((hip + shoulder) * 0.5f), new V3(torsoHalf, (shoulder - hip) * 0.5f, 0.12f * k), yaw), torso);
                if (arms)
                    for (int side = 0; side < 2; side++)
                    {
                        float sign = side == 0 ? -1f : 1f;
                        int sh = plan.ShoulderOr(side), arm = plan.ArmOr(side), hand = plan.HandOr(side);
                        V3 top = foot + r * (sign * 0.185f * k) + up * (shoulder - 0.03f * height);
                        Add(Sphere(HitRegion.Torso, top, 0.055f * k), sh);
                        V3 elbowTop = foot + r * (sign * 0.20f * k) + up * (shoulder - 0.075f * height);
                        V3 wrist = foot + r * (sign * 0.21f * k) + up * (hip + 0.03f * height);
                        Add(Capsule(HitRegion.Torso, -1, elbowTop, wrist, 0.045f * k), arm);
                        Add(Sphere(HitRegion.Torso, wrist + up * (-0.05f * height), 0.045f * k), hand);
                    }
                float legR = 0.07f * k;
                for (int i = 0; i < plan.Legs.Count; i++)
                {
                    LegRef slot = plan.Legs[i];
                    float lateral = (plan.Legs.Count == 1 ? 0f : slot.Side) * 0.08f * k;
                    V3 at = foot + r * lateral + f * (slot.Front * 0.08f * k);
                    float ankle = 0.09f * height;
                    Add(Capsule(HitRegion.Leg, i, at + up * (ankle + legR * 0.5f), at + up * hip, legR), plan.Legs[i].Part, i);
                    if (Has(plan.Legs[i].Part))
                        Add(Box(HitRegion.Leg, at + f * (0.04f * k) + up * (ankle * 0.5f), new V3(0.05f * k, ankle * 0.5f, 0.1f * k), yaw), plan.FootOr(i), i);
                }
                return;
            }
            if (shape == BodyShape.Long)
            {
                float length = Math.Clamp(0.25f + 0.7f * k, 0.35f, 2.8f), width = 0.45f * length;
                float belly = 0.45f * height;
                float headR = Math.Clamp(0.14f * Math.Max(length, height), 0.07f, 0.6f);
                V3 head = foot + f * (0.5f * length - headR * 0.5f) + up * Math.Max(headR, 0.72f * height);
                Add(Sphere(HitRegion.Head, head, headR), plan.HeadOr());
                V3 chest = foot + f * (0.31f * length) + up * ((belly + height * 0.92f) * 0.5f);
                if (plan.Neck >= 0) Add(Capsule(HitRegion.Head, -1, chest, head - f * (headR * 0.6f), Math.Max(0.04f, 0.45f * headR)), plan.Neck);
                Add(Box(HitRegion.Torso, foot + f * (-0.05f * length) + up * ((belly + height * 0.92f) * 0.5f),
                    new V3(0.5f * width, (height * 0.92f - belly) * 0.5f, 0.36f * length), yaw), torso);
                float legR = 0.03f + 0.05f * k;
                int n = plan.Legs.Count;
                for (int i = 0; i < n; i++)
                {
                    LegRef slot = plan.Legs[i];
                    float ahead = slot.Front != 0 ? slot.Front * 0.28f * length : (n <= 1 ? 0f : (0.3f - 0.6f * (i / 2) / Math.Max(1, (n - 1) / 2)) * length);
                    float lateral = (slot.Side != 0 ? slot.Side : (i % 2 == 0 ? -1 : 1)) * 0.35f * width;
                    V3 at = foot + f * ahead + r * lateral;
                    float paw = Math.Max(legR * 2f, 0.12f * belly);
                    Add(Capsule(HitRegion.Leg, i, at + up * (paw + legR * 0.5f), at + up * Math.Max(belly, legR * 2f + paw), legR), slot.Part, i);
                    if (Has(slot.Part)) Add(Sphere(HitRegion.Leg, at + up * (paw * 0.5f + 0.01f), Math.Max(legR, paw * 0.5f)), plan.FootOr(i), i);
                }
                return;
            }
            {
                // Lying: a low body along the facing, head at the front, arms beside the torso, legs and feet behind.
                float length = Math.Clamp(0.5f + 0.9f * k, 0.5f, 2.6f), low = Math.Clamp(0.18f * k, 0.12f, 0.45f);
                float headR = Math.Clamp(0.11f * k + 0.04f, 0.08f, 0.35f);
                V3 head = foot + f * (0.5f * length - headR) + up * headR;
                Add(Sphere(HitRegion.Head, head, headR), plan.HeadOr());
                if (plan.Neck >= 0) Add(Capsule(HitRegion.Head, -1, head - f * headR, head - f * (headR * 1.6f), Math.Min(0.06f, headR * 0.5f)), plan.Neck);
                float torsoHalf = plan.Humanoid ? 0.15f * k : 0.22f * k;
                Add(Box(HitRegion.Torso, foot + f * (0.05f * length) + up * (low * 0.5f), new V3(torsoHalf, low * 0.5f, 0.22f * length), yaw), torso);
                if (plan.Humanoid)
                    for (int side = 0; side < 2; side++)
                    {
                        float sign = side == 0 ? -1f : 1f;
                        V3 sh = foot + f * (0.25f * length) + r * (sign * 0.19f * k) + up * (low * 0.5f);
                        Add(Sphere(HitRegion.Torso, sh, 0.05f * k), plan.ShoulderOr(side));
                        V3 wrist = sh - f * (0.3f * length);
                        Add(Capsule(HitRegion.Torso, -1, sh - f * 0.04f, wrist, 0.042f * k), plan.ArmOr(side));
                        Add(Sphere(HitRegion.Torso, wrist - f * 0.05f, 0.042f * k), plan.HandOr(side));
                    }
                float legR = Math.Max(0.06f, 0.07f * k);
                for (int i = 0; i < plan.Legs.Count; i++)
                {
                    LegRef slot = plan.Legs[i];
                    float lateral = (slot.Side != 0 ? slot.Side : (i % 2 == 0 ? -1 : 1)) * 0.09f * k;
                    V3 hipAt = foot + f * (-0.15f * length) + r * lateral + up * legR;
                    V3 ankle = hipAt + f * (-0.28f * length);
                    Add(Capsule(HitRegion.Leg, i, hipAt, ankle, legR), slot.Part, i);
                    if (Has(slot.Part)) Add(Sphere(HitRegion.Leg, ankle + f * (-0.05f * length), legR), plan.FootOr(i), i);
                }
            }
        }

        // The whole pawn: an oriented box (the pawn's facing) round every volume, a hair larger. Leaving it is leaving
        // the pawn; gaps between parts (arm and torso) stay inside it.
        public static bool Envelope(List<HitVolume> volumes, int from, float yaw, out HitVolume box)
        {
            box = default;
            if (from >= volumes.Count) return false;
            V3 right = HitMath.Right(yaw), fwd = HitMath.Forward(yaw);
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            void Point(V3 p, float pad)
            {
                float x = V3.Dot(p, right), z = V3.Dot(p, fwd);
                minX = Math.Min(minX, x - pad); maxX = Math.Max(maxX, x + pad);
                minY = Math.Min(minY, p.Y - pad); maxY = Math.Max(maxY, p.Y + pad);
                minZ = Math.Min(minZ, z - pad); maxZ = Math.Max(maxZ, z + pad);
            }
            for (int i = from; i < volumes.Count; i++)
            {
                HitVolume v = volumes[i];
                if (v.Kind == VolumeKind.Box)
                {
                    V3 vr = HitMath.Right(v.Yaw), vf = HitMath.Forward(v.Yaw);
                    for (int c = 0; c < 8; c++)
                        Point(v.A + vr * ((c & 1) == 0 ? -v.B.X : v.B.X) + new V3(0f, (c & 2) == 0 ? -v.B.Y : v.B.Y, 0f) + vf * ((c & 4) == 0 ? -v.B.Z : v.B.Z), 0f);
                }
                else { Point(v.A, v.Radius); Point(v.Kind == VolumeKind.Sphere ? v.A : v.B, v.Radius); }
            }
            const float pad2 = 0.02f;
            float cx = (minX + maxX) * 0.5f, cz = (minZ + maxZ) * 0.5f;
            V3 centre = right * cx + fwd * cz + new V3(0f, (minY + maxY) * 0.5f, 0f);
            box = Box(HitRegion.None, centre, new V3((maxX - minX) * 0.5f + pad2, (maxY - minY) * 0.5f + pad2, (maxZ - minZ) * 0.5f + pad2), yaw);
            return true;
        }
    }

    // 0.6.0: which body parts a body has, as indices into its BodyDef.AllParts (engine side), -1 when absent.
    // Volumes fall back to the nearest larger part that exists (hand -> arm -> shoulder -> torso), never at random.
    internal sealed class BodyPlan
    {
        public int Head = -1, Neck = -1, Torso = -1;
        public readonly int[] Shoulder = { -1, -1 }, Arm = { -1, -1 }, Hand = { -1, -1 };   // [0] left, [1] right
        public readonly List<LegRef> Legs = new List<LegRef>();
        public bool Humanoid => Arm[0] >= 0 && Arm[1] >= 0;

        public int HeadOr() => Head >= 0 ? Head : Torso;
        public int ShoulderOr(int side) => Shoulder[side] >= 0 ? Shoulder[side] : Torso;
        public int ArmOr(int side) => Arm[side] >= 0 ? Arm[side] : ShoulderOr(side);
        public int HandOr(int side) => Hand[side] >= 0 ? Hand[side] : ArmOr(side);
        public int FootOr(int leg) => Legs[leg].Foot >= 0 ? Legs[leg].Foot : Legs[leg].Part;
    }

    internal struct LegRef
    {
        public int Side, Front, Part, Foot;
        public LegRef(int side, int front, int part, int foot) { Side = side; Front = front; Part = part; Foot = foot; }
        public static implicit operator LegSlot(LegRef l) => new LegSlot(l.Side, l.Front);
    }

    // Skill-scaled aim motion: the weapon itself is exact; the shooter's hands sway and kick. Pure numbers, no engine.
    internal static class AimSkill
    {
        // Shooting level 0..20 -> 0..1.
        public static float Steadiness(int level) => Math.Clamp(level / 20f, 0f, 1f);

        // Peak sway in degrees: about 2.4 at level 0, 0.25 at level 20.
        public static float SwayDegrees(int level, bool moving) => (2.4f + (0.25f - 2.4f) * Steadiness(level)) * (moving ? 1.4f : 1f);

        // Vertical kick of one shot in degrees, from the projectile's damage (heavier rounds kick more).
        public static float KickDegrees(int level, float damage)
        {
            float weapon = Math.Clamp(0.5f + 0.06f * damage, 0.6f, 4f);
            return weapon * (1.6f + (0.4f - 1.6f) * Steadiness(level));
        }

        // Smooth, bounded wander in -1..1 from time (sum of incommensurate sines; deterministic for tests).
        public static float Wander(float time, float phase) =>
            (float)(0.6 * Math.Sin(time * 0.9 + phase) + 0.3 * Math.Sin(time * 2.3 + phase * 1.7) + 0.1 * Math.Sin(time * 5.1 + phase * 2.9));
    }
}
