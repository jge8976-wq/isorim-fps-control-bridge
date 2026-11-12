using System;
using System.Collections.Generic;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Clip against each roof cell's two triangular prisms. The contact is the
    // entry face, including eaves/undersides; no fixed-distance samples can
    // jump over a thin slab or stop somewhere inside a pitched roof.
    internal static class RoofSegmentContact
    {
        private static bool Clip(float a, float b, ref float enter, ref float exit)
        {
            const float epsilon = .000001f;
            if (a >= -epsilon && b >= -epsilon) return true;
            if (a < -epsilon && b < -epsilon) return false;
            float t = a / (a - b);
            if (a < 0) enter = Math.Max(enter, t); else exit = Math.Min(exit, t);
            return enter <= exit + epsilon;
        }

        private static bool Prism(V3 a, V3 b, Vector3 p, Vector3 q, Vector3 r, float bottom, float limit, out float t)
        {
            float enter = 0, exit = limit;
            t = float.PositiveInfinity;
            Vector3 normal = Vector3.Cross(q - p, r - p);
            if (normal.y < 0) normal = -normal;
            if (normal.y < .000001f) return false;
            var aa = VehicleModels.U(a); var bb = VehicleModels.U(b);
            if (!Clip(a.Y - bottom, b.Y - bottom, ref enter, ref exit)
                || !Clip(-Vector3.Dot(normal, aa - p), -Vector3.Dot(normal, bb - p), ref enter, ref exit)) return false;
            float winding = (q.x - p.x) * (r.z - p.z) - (q.z - p.z) * (r.x - p.x);
            if (!Edge(p, q) || !Edge(q, r) || !Edge(r, p)) return false;
            t = Math.Clamp(enter, 0, limit); return true;
            bool Edge(Vector3 x, Vector3 y)
            {
                float Side(Vector3 z) => ((y.x - x.x) * (z.z - x.z) - (y.z - x.z) * (z.x - x.x)) * Math.Sign(winding);
                return Clip(Side(aa), Side(bb), ref enter, ref exit);
            }
        }

        internal static bool Try(Map map, V3 a, V3 b, float limit, out float fraction, out IntVec3 cell,
            ISet<IntVec3> passedRoofs = null)
        {
            fraction = float.PositiveInfinity; cell = IntVec3.Invalid;
            if (map == null || !a.Finite || !b.Finite || limit < 0) return false;
            float begin = 0, end = Math.Min(1, limit), best = end;
            int rows = FloorSpace.Bands(map)[0].Rows;
            if (!Clip(a.X, b.X, ref begin, ref end) || !Clip(map.Size.x - .0001f - a.X, map.Size.x - .0001f - b.X, ref begin, ref end)
                || !Clip(a.Z, b.Z, ref begin, ref end) || !Clip(rows - .0001f - a.Z, rows - .0001f - b.Z, ref begin, ref end)) return false;
            V3 start = V3.Lerp(a, b, begin), finish = V3.Lerp(a, b, end), delta = b - a;
            int x = Mathf.FloorToInt(start.X), z = Mathf.FloorToInt(start.Z), ex = Mathf.FloorToInt(finish.X), ez = Mathf.FloorToInt(finish.Z);
            int sx = Math.Sign(delta.X), sz = Math.Sign(delta.Z);
            float tx = sx == 0 ? float.PositiveInfinity : ((sx > 0 ? x + 1 : x) - a.X) / delta.X;
            float tz = sz == 0 ? float.PositiveInfinity : ((sz > 0 ? z + 1 : z) - a.Z) / delta.Z;
            float dx = sx == 0 ? float.PositiveInfinity : 1 / Math.Abs(delta.X), dz = sz == 0 ? float.PositiveInfinity : 1 / Math.Abs(delta.Z);
            IntVec3 chosen = IntVec3.Invalid;
            int count = Math.Abs(ex - x) + Math.Abs(ez - z) + 4;
            for (int i = 0; i < count; i++)
            {
                Test(x, z);
                if (x == ex && z == ez || Math.Min(tx, tz) > end || chosen.IsValid && Math.Min(tx, tz) > best) break;
                if (Math.Abs(tx - tz) < .000001f) { Test(x + sx, z); Test(x, z + sz); x += sx; z += sz; tx += dx; tz += dz; }
                else if (tx < tz) { x += sx; tx += dx; } else { z += sz; tz += dz; }
            }
            if (!chosen.IsValid) return false;
            fraction = best; cell = chosen; return true;

            void Test(int cx, int cz)
            {
                foreach (FloorBand band in FloorSpace.Bands(map))
                {
                    var candidate = new IntVec3(cx, 0, cz + band.Shift);
                    if (!candidate.InBounds(map) || map.roofGrid.RoofAt(candidate) == null || CeStructureRoof.ShouldIgnore(map, candidate, passedRoofs)) continue;
                    float bottom = band.Height + View3D.WallHeight - .005f;
                    bool shaped = RoofShape.Covers(map, candidate);
                    float Height(int px, int pz) => band.Height + (shaped ? RoofShape.HeightAt(map, px, pz + band.Shift) : View3D.WallHeight + .06f);
                    Vector3 p = new(cx, Height(cx, cz), cz), q = new(cx, Height(cx, cz + 1), cz + 1),
                        r = new(cx + 1, Height(cx + 1, cz + 1), cz + 1), s = new(cx + 1, Height(cx + 1, cz), cz);
                    if (Math.Abs(p.y - r.y) >= Math.Abs(q.y - s.y)) { Triangle(p, q, r); Triangle(p, r, s); }
                    else { Triangle(p, q, s); Triangle(q, r, s); }
                    void Triangle(Vector3 v0, Vector3 v1, Vector3 v2)
                    {
                        if (Prism(a, b, v0, v1, v2, bottom, best, out float t) && t <= best)
                        { best = t; chosen = candidate; }
                    }
                }
            }
        }
    }
}
