using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // 0.8.1: exact hits on what is drawn. While IsoRim draws a person, animal or mech in the world, every mesh it submits
    // for that pawn (body, apparel, headgear, hair, an animal's body and each leg; not the held weapon or a carried item)
    // is recorded with its matrix for that frame. A round then hits a pawn where, and only where, it meets one of those
    // triangles; which body part it is comes from the nearest part volume (BodyRegions). Pawns not drawn lately keep
    // the volumes alone.
    internal static class FigureMeshes
    {
        private sealed class Record { public Mesh mesh; public Matrix4x4 world; }
        private sealed class Snap { public readonly List<Record> parts = new List<Record>(); public int frame = -1; public V3 spot; public int used; }
        private sealed class Tris { public Vector3[] v; public int[] t; public Bounds b; public MeshContactMath.Tree contact; }

        private const int FreshFrames = 30;
        private static readonly Dictionary<Pawn, Snap> snaps = new Dictionary<Pawn, Snap>();
        private static readonly Dictionary<Mesh, Tris> tris = new Dictionary<Mesh, Tris>();
        private static Pawn drawing;
        private static int paused;
        private static Func<bool> scopeActive;
        public static bool Enabled { get; private set; }

        public static void Install(Harmony harmony)
        {
            FieldInfo scope = AccessTools.Field(AccessTools.TypeByName("IsoRim.FigureScope"), "Active") ?? throw new MissingFieldException("FigureScope.Active");
            scopeActive = FastField.Static<bool>(scope);   // read per figure per frame: compiled, not reflection
            var t = typeof(FigureMeshes);
            foreach (string name in new[] { "IsoRim.People", "IsoRim.Animals", "IsoRim.Mechs" })
            {
                MethodInfo draw = AccessTools.Method(AccessTools.TypeByName(name), "Draw") ?? throw new MissingMethodException(name + ".Draw");
                harmony.Patch(draw, prefix: new HarmonyMethod(t, nameof(BeginFigure)), finalizer: new HarmonyMethod(t, nameof(EndFigure)));
            }
            foreach (string name in new[] { "IsoRim.FigureWeapons", "IsoRim.Carried" })
            {
                MethodInfo draw = AccessTools.Method(AccessTools.TypeByName(name), "Draw");
                if (draw != null) harmony.Patch(draw, prefix: new HarmonyMethod(t, nameof(Pause)), finalizer: new HarmonyMethod(t, nameof(Resume)));
            }
            MethodInfo submit = AccessTools.Method(typeof(DynamicModels), "Submit") ?? throw new MissingMethodException("DynamicModels.Submit");
            harmony.Patch(submit, prefix: new HarmonyMethod(t, nameof(OnSubmit)) { priority = Priority.First });
            Enabled = true;
        }

        public static void Reset() { snaps.Clear(); tris.Clear(); drawing = null; paused = 0; IffOutline.Reset(); }
        public static void Invalidate(Pawn pawn) { if (snaps.TryGetValue(pawn, out Snap s)) s.frame = -1000000; }

        // ---- recording

        public static void BeginFigure(Pawn pawn)
        {
            // Figures drawn for other mods (portraits, measuring) are not in the world.
            if (pawn == null || !pawn.Spawned || scopeActive()) { drawing = null; return; }
            drawing = pawn;
            if (!snaps.TryGetValue(pawn, out Snap s)) snaps[pawn] = s = new Snap();
            if (s.frame != Time.frameCount) { s.used = 0; s.frame = Time.frameCount; s.spot = FloorSpace.Spot(pawn); }
        }
        public static Exception EndFigure(Exception __exception) { drawing = null; return __exception; }
        public static void Pause() => paused++;
        public static Exception Resume(Exception __exception) { if (paused > 0) paused--; return __exception; }

        public static void OnSubmit(object __0, Matrix4x4 __1)
        {
            Pawn pawn = drawing;
            // IsoRim's parts are a public type: read directly (this runs for every part of every figure every frame).
            if (pawn == null || paused > 0 || !(__0 is List<DynamicModels.Part> list) || !snaps.TryGetValue(pawn, out Snap s)) return;
            // Use the renderer's actual floor, bridge and attachment placement in the ballistic coordinate space.
            Matrix4x4 world = Matrix4x4.Scale(new Vector3(1f, View3D.Squash, 1f)) * Floors.DrawnMatrix(__1, out _);
            for (int i = 0; i < list.Count; i++)
            {
                DynamicModels.Part p = list[i];
                if (p == null || p.outline) continue;
                Mesh mesh = p.mesh;
                if (mesh == null) continue;
                if (s.used < s.parts.Count) { s.parts[s.used].mesh = mesh; s.parts[s.used].world = world; }
                else s.parts.Add(new Record { mesh = mesh, world = world });
                s.used++;
            }
            IffOutline.Submit(pawn, list, __1);   // friend-or-foe rims (drafted only)
        }

        private static Tris TrisOf(Mesh mesh)
        {
            if (tris.TryGetValue(mesh, out Tris t)) return t;
            if (tris.Count > 4000) { var dead = new List<Mesh>(); foreach (var k in tris.Keys) if (k == null) dead.Add(k); foreach (var k in dead) tris.Remove(k); }
            t = null;
            try
            {
                if (mesh.isReadable)
                {
                    Vector3[] vertices = mesh.vertices;
                    int[] triangles = mesh.triangles;
                    if (vertices.Length >= 3 && triangles.Length >= 3)
                        t = new Tris { v = vertices, t = triangles, b = mesh.bounds };
                }
            }
            catch { t = null; }
            tris[mesh] = t;
            return t;
        }

        // ---- hits

        private static bool TrySnapshot(Pawn pawn, out Snap snap)
        {
            snap = null;
            return Enabled && snaps.TryGetValue(pawn, out snap) && snap.used > 0
                && Time.frameCount - snap.frame <= FreshFrames;
        }

        public static bool Has(Pawn pawn)
        {
            if (!TrySnapshot(pawn, out Snap s)) return false;
            for (int i = 0; i < s.used; i++)
                if (s.parts[i].mesh != null && TrisOf(s.parts[i].mesh) != null) return true;
            return false;
        }

        internal static bool ContactMeshes(Pawn pawn, List<MeshContactMath.Instance> into)
        {
            if (!TrySnapshot(pawn, out Snap snap)) return false;
            Matrix4x4 placement = Placement(pawn, snap);
            for (int i = 0; i < snap.used; i++)
            {
                Record record = snap.parts[i];
                Tris geometry = record.mesh == null ? null : TrisOf(record.mesh);
                if (geometry == null || geometry.t.Length < 3) continue;
                if (geometry.contact == null)
                {
                    var vertices = new System.Numerics.Vector3[geometry.t.Length];
                    for (int j = 0; j < vertices.Length; j++) vertices[j] = VehicleMeshContact.N3(geometry.v[geometry.t[j]]);
                    geometry.contact = new MeshContactMath.Tree(vertices);
                }
                into.Add(new MeshContactMath.Instance(geometry.contact, VehicleMeshContact.Matrix(placement * record.world)));
            }
            return into.Count > 0;
        }

        // Both the ballistic trace and its overlay use the same captured geometry and current translation.
        private static Matrix4x4 Placement(Pawn pawn, Snap snap)
        {
            V3 delta = FloorSpace.Spot(pawn) - snap.spot;
            return Matrix4x4.Translate(new Vector3(delta.X, delta.Y, delta.Z));
        }

        internal static bool DrawHitMesh(Pawn pawn)
        {
            if (!TrySnapshot(pawn, out Snap s)) return false;
            Matrix4x4 place = Placement(pawn, s);
            bool drawn = false;
            for (int i = 0; i < s.used; i++)
            {
                Record part = s.parts[i];
                if (part.mesh == null || TrisOf(part.mesh) == null) continue;
                drawn |= Visuals.HitMesh(part.mesh, place * part.world);
            }
            return drawn;
        }

        // The first point where a→b meets the pawn's drawn model (t along a→b), its normal, true when it does.
        public static bool Hit(Pawn pawn, V3 a, V3 b, out float best, out V3 normal)
        {
            best = float.PositiveInfinity; normal = new V3(0f, 1f, 0f);
            if (!TrySnapshot(pawn, out Snap s)) return false;
            // Move the captured world geometry with the pawn between render frames.
            Matrix4x4 place = Placement(pawn, s);
            Vector3 A = new Vector3(a.X, a.Y, a.Z), B = new Vector3(b.X, b.Y, b.Z);
            for (int i = 0; i < s.used; i++)
            {
                Record r = s.parts[i];
                if (r.mesh == null) continue;
                Tris t = TrisOf(r.mesh);
                if (t == null) continue;
                Matrix4x4 world = place * r.world, inv = world.inverse;
                Vector3 la = inv.MultiplyPoint3x4(A), lb = inv.MultiplyPoint3x4(B);
                if (!Slab(la, lb, t.b)) continue;
                Vector3 dir = lb - la;
                for (int k = 0; k + 2 < t.t.Length; k += 3)
                {
                    Vector3 p0 = t.v[t.t[k]], p1 = t.v[t.t[k + 1]], p2 = t.v[t.t[k + 2]];
                    if (!Ray(la, dir, p0, p1, p2, out float u) || u >= best) continue;
                    best = u;
                    Vector3 n = world.MultiplyVector(Vector3.Cross(p1 - p0, p2 - p0)).normalized;
                    if (Vector3.Dot(n, B - A) > 0f) n = -n;
                    normal = new V3(n.x, n.y, n.z);
                }
            }
            return float.IsFinite(best);
        }

        // Segment (u in 0..1) against a triangle, both faces (Möller–Trumbore).
        private static bool Ray(Vector3 o, Vector3 d, Vector3 p0, Vector3 p1, Vector3 p2, out float u)
        {
            u = 0f;
            Vector3 e1 = p1 - p0, e2 = p2 - p0, h = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, h);
            if (Math.Abs(det) < 1e-9f) return false;
            float f = 1f / det;
            Vector3 s = o - p0;
            float bu = f * Vector3.Dot(s, h);
            if (bu < 0f || bu > 1f) return false;
            Vector3 q = Vector3.Cross(s, e1);
            float bv = f * Vector3.Dot(d, q);
            if (bv < 0f || bu + bv > 1f) return false;
            u = f * Vector3.Dot(e2, q);
            return u >= 0f && u <= 1f;
        }

        private static bool Slab(Vector3 a, Vector3 b, Bounds box)
        {
            const float pad = .02f;
            Vector3 lo = box.min - Vector3.one * pad, hi = box.max + Vector3.one * pad, d = b - a;
            float t0 = 0f, t1 = 1f;
            for (int i = 0; i < 3; i++)
            {
                float o = a[i], dd = d[i];
                if (Math.Abs(dd) < 1e-9f) { if (o < lo[i] || o > hi[i]) return false; continue; }
                float u0 = (lo[i] - o) / dd, u1 = (hi[i] - o) / dd;
                if (u0 > u1) { float x = u0; u0 = u1; u1 = x; }
                t0 = Math.Max(t0, u0); t1 = Math.Min(t1, u1);
                if (t0 > t1) return false;
            }
            return true;
        }

        // The part volume nearest a point (the body part a model hit lands on).
        public static int NearestVolume(List<HitVolume> volumes, int from, V3 p)
        {
            int best = -1; float bestD = float.MaxValue;
            for (int i = from; i < volumes.Count; i++)
            {
                HitVolume v = volumes[i];
                float dist;
                if (v.Kind == VolumeKind.Box)
                {
                    V3 r = HitMath.Right(v.Yaw), f = HitMath.Forward(v.Yaw), q = p - v.A;
                    float x = Math.Max(0f, Math.Abs(V3.Dot(q, r)) - v.B.X), y = Math.Max(0f, Math.Abs(q.Y) - v.B.Y), z = Math.Max(0f, Math.Abs(V3.Dot(q, f)) - v.B.Z);
                    dist = x * x + y * y + z * z;
                }
                else
                {
                    float s = v.Kind == VolumeKind.Sphere ? (p - v.A).Length : HitMath.PointSegment(p, v.A, v.B);
                    dist = Math.Max(0f, s - v.Radius); dist *= dist;
                }
                if (dist < bestD) { bestD = dist; best = i; }
            }
            return best;
        }
    }
}
