using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // 0.8.1: hit volumes of a person fitted to the 3D model IsoRim actually draws. Every drawn figure reports its head
    // and body matrices for that frame (IsoRim Api.AddFigureHook); the head, upper and lower meshes it used are split into
    // body parts by their own vertices (head, neck, torso, shoulders, arms, hands, legs, feet: once per mesh) and each
    // part becomes a box fitted to those vertices, placed by the frame's matrix (posture, crouch, animation, body type).
    // The parts keep the game's own body-part mapping (BodyPlan). Not drawn (off screen, the possessed pawn's own
    // hidden body), lying or crawling: the built-in proportional volumes apply as before.
    internal static class ModelHits
    {
        private enum G { Head, Neck, Torso, ShoulderL, ShoulderR, ArmL, ArmR, HandL, HandR, LegL, LegR, FootL, FootR, Count }

        private sealed class Groups
        {
            public readonly Vector3[] min = new Vector3[(int)G.Count], max = new Vector3[(int)G.Count];
            public readonly int[] count = new int[(int)G.Count];
            public void Add(G g, Vector3 p)
            {
                int i = (int)g;
                if (count[i]++ == 0) { min[i] = p; max[i] = p; }
                else { min[i] = Vector3.Min(min[i], p); max[i] = Vector3.Max(max[i], p); }
            }
        }

        private sealed class Snap
        {
            public Model head, upper, lower;
            public Matrix4x4 headWorld, upperWorld, lowerWorld;
            public V3 spot;
            public float yaw;
            public int frame;
        }

        private const int FreshFrames = 30;
        private static readonly Dictionary<Pawn, Snap> snaps = new Dictionary<Pawn, Snap>();
        private static readonly Dictionary<long, Groups> groups = new Dictionary<long, Groups>();
        // Read for every drawn person every frame: compiled once instead of FieldInfo.GetValue.
        private static Func<object, object> lookField;
        private static Func<object, Model> upperF, lowerF, skullF, ownHeadF;
        private static Func<object, float> headWideF;
        private static Func<object, Vector3> bodyScaleF;
        private static Func<object, bool> upperBonesF, lowerBonesF;
        private static Func<string, Model> part;
        private static Model defaultHead;
        public static bool Enabled { get; private set; }

        public static void Install()
        {
            FieldInfo lookInfo = AccessTools.Field(typeof(FigureFrame), "look") ?? throw new MissingFieldException("FigureFrame.look");
            Type look = lookInfo.FieldType;
            lookField = FastField.Getter<object>(lookInfo);
            upperF = FastField.Getter<Model>(Need(look, "upper")); lowerF = FastField.Getter<Model>(Need(look, "lower"));
            skullF = FastField.Getter<Model>(Need(look, "skull")); ownHeadF = FastField.Getter<Model>(Need(look, "ownHead"));
            headWideF = FastField.Getter<float>(Need(look, "headWide")); bodyScaleF = FastField.Getter<Vector3>(Need(look, "bodyScale"));
            upperBonesF = FastField.Getter<bool>(Need(look, "upperBones")); lowerBonesF = FastField.Getter<bool>(Need(look, "lowerBones"));
            MethodInfo partMethod = AccessTools.Method(AccessTools.TypeByName("IsoRim.People"), "Part", new[] { typeof(string) })
                ?? throw new MissingMethodException("IsoRim.People.Part(string)");
            part = (Func<string, Model>)partMethod.CreateDelegate(typeof(Func<string, Model>));
            Api.AddFigureHook(Capture);
            Enabled = true;
        }

        private static FieldInfo Need(Type t, string name) => AccessTools.Field(t, name) ?? throw new MissingFieldException(t.FullName, name);

        public static void Reset() => snaps.Clear();
        public static void Invalidate(Pawn pawn) { if (snaps.TryGetValue(pawn, out Snap s)) s.frame = -1000000; }

        // ---- capture (IsoRim draws a person)

        private static void Capture(FigureFrame f)
        {
            Pawn pawn = f.Pawn;
            if (pawn == null) return;
            if (f.Lying || f.Crawling || f.Dead || !f.BodyShown) { snaps.Remove(pawn); return; }
            object look = lookField(f);
            if (look == null) return;
            Model upper = upperF(look), lower = lowerF(look);
            if (upper == null || lower == null) return;
            Model head = skullF(look) ?? ownHeadF(look);
            Matrix4x4 headLocal = Matrix4x4.identity;
            if (head == null)
            {
                defaultHead ??= part("person_head");
                head = defaultHead;
                headLocal = Matrix4x4.Scale(new Vector3(headWideF(look), 1f, 1f));
            }
            Matrix4x4 scale = Matrix4x4.Scale(bodyScaleF(look));
            // Drawn matrices carry IsoRim's height squash; hits are in true height.
            // (and in raw floor rows: the floor's place is added, as IsoRim does when it finally draws)
            Matrix4x4 trueSpace = Matrix4x4.Translate(FloorSpace.Offset(pawn)) * Matrix4x4.Scale(new Vector3(1f, View3D.Squash, 1f));
            if (!snaps.TryGetValue(pawn, out Snap s)) snaps[pawn] = s = new Snap();
            s.head = f.HeadHidden ? null : head;
            s.upper = upper; s.lower = lower;
            s.headWorld = trueSpace * f.Head * headLocal;
            s.upperWorld = trueSpace * f.Body * (upperBonesF(look) ? Matrix4x4.identity : scale);
            s.lowerWorld = trueSpace * f.Body * (lowerBonesF(look) ? Matrix4x4.identity : scale);
            s.spot = FloorSpace.Spot(pawn);
            s.yaw = f.Yaw;
            s.frame = Time.frameCount;
        }

        // ---- the parts of a mesh, by its own vertices (cached per mesh and role)

        private static Groups Split(Model m, int role)
        {
            long key = ((long)m.id << 2) | (uint)role;
            if (groups.TryGetValue(key, out Groups g)) return g;
            g = new Groups();
            Vector3[] p = m.positions;
            if (p == null || p.Length == 0) { groups[key] = g; return g; }
            Vector3 lo = p[0], hi = p[0];
            for (int i = 1; i < p.Length; i++) { lo = Vector3.Min(lo, p[i]); hi = Vector3.Max(hi, p[i]); }
            float h = Math.Max(1e-4f, hi.y - lo.y), cx = (lo.x + hi.x) * .5f, ax = Math.Max(1e-4f, Math.Max(hi.x - cx, cx - lo.x));
            if (role == 0) { for (int i = 0; i < p.Length; i++) g.Add(G.Head, p[i]); }
            else if (role == 1)
            {
                // Upper body: arms are the outer part below the shoulder line; the neck the narrow top; the rest torso.
                bool Arm(Vector3 v) => Math.Abs(v.x - cx) > .6f * ax && v.y < hi.y - .08f * h;
                float[] armLo = { float.MaxValue, float.MaxValue }, armHi = { float.MinValue, float.MinValue };
                for (int i = 0; i < p.Length; i++)
                    if (Arm(p[i])) { int s = p[i].x < cx ? 0 : 1; armLo[s] = Math.Min(armLo[s], p[i].y); armHi[s] = Math.Max(armHi[s], p[i].y); }
                for (int i = 0; i < p.Length; i++)
                {
                    Vector3 v = p[i];
                    if (Arm(v))
                    {
                        int s = v.x < cx ? 0 : 1;
                        float u = (v.y - armLo[s]) / Math.Max(1e-4f, armHi[s] - armLo[s]);
                        g.Add(u > .72f ? (s == 0 ? G.ShoulderL : G.ShoulderR) : u < .22f ? (s == 0 ? G.HandL : G.HandR) : (s == 0 ? G.ArmL : G.ArmR), v);
                    }
                    else if (v.y > hi.y - .1f * h && Math.Abs(v.x - cx) < .35f * ax) g.Add(G.Neck, v);
                    else g.Add(G.Torso, v);
                }
            }
            else
            {
                // Lower body: the two legs by side, the feet their lowest part.
                for (int i = 0; i < p.Length; i++)
                {
                    Vector3 v = p[i];
                    bool left = v.x < cx;
                    g.Add(v.y < lo.y + .12f * h ? (left ? G.FootL : G.FootR) : (left ? G.LegL : G.LegR), v);
                }
            }
            groups[key] = g;
            return g;
        }

        // ---- volumes

        // The model-fitted volumes of a drawn person; false when there is no fresh drawing (callers use the built-in ones).
        public static bool Build(Pawn pawn, BodyPlan plan, IReadOnlyList<bool> present, List<HitVolume> into)
        {
            if (!Enabled || !snaps.TryGetValue(pawn, out Snap s) || Time.frameCount - s.frame > FreshFrames) return false;
            if (pawn.Downed || pawn.GetPosture() != PawnPosture.Standing) return false;
            int start = into.Count;
            // The pawn may have moved since the frame was drawn: the figure moves with it.
            V3 now = FloorSpace.Spot(pawn), d = now - s.spot;
            Matrix4x4 move = Matrix4x4.Translate(new Vector3(d.X, d.Y, d.Z));
            V3 right = HitMath.Right(s.yaw), fwd = HitMath.Forward(s.yaw);
            bool Has(int p) => p >= 0 && p < present.Count && present[p];

            void Emit(Model m, int role, Matrix4x4 world)
            {
                if (m == null) return;
                Groups g = Split(m, role);
                Matrix4x4 w = move * world;
                for (int i = 0; i < (int)G.Count; i++)
                {
                    if (g.count[i] < 3) continue;
                    if (!Fit(w, g.min[i], g.max[i], right, fwd, s.yaw, out HitVolume box, out float side)) continue;
                    int sideIndex = side < 0f ? 0 : 1;   // [0] left, [1] right of the facing, as the body plan
                    G kind = (G)i;
                    int partIndex, leg = -1; HitRegion region;
                    switch (kind)
                    {
                        case G.Head: partIndex = plan.HeadOr(); region = HitRegion.Head; break;
                        case G.Neck: partIndex = plan.Neck >= 0 ? plan.Neck : plan.HeadOr(); region = HitRegion.Head; break;
                        case G.Torso: partIndex = plan.Torso; region = HitRegion.Torso; break;
                        case G.ShoulderL: case G.ShoulderR: partIndex = plan.ShoulderOr(sideIndex); region = HitRegion.Torso; break;
                        case G.ArmL: case G.ArmR: partIndex = plan.ArmOr(sideIndex); region = HitRegion.Torso; break;
                        case G.HandL: case G.HandR: partIndex = plan.HandOr(sideIndex); region = HitRegion.Torso; break;
                        default:
                            leg = LegOn(plan, sideIndex);
                            if (leg < 0) continue;
                            bool foot = kind == G.FootL || kind == G.FootR;
                            partIndex = foot ? plan.FootOr(leg) : plan.Legs[leg].Part;
                            if (foot && !Has(plan.Legs[leg].Part)) continue;
                            region = HitRegion.Leg; break;
                    }
                    if (!Has(partIndex)) continue;
                    box.Region = region; box.Part = partIndex; box.Leg = leg;
                    into.Add(box);
                }
            }

            Emit(s.head, 0, s.headWorld);
            Emit(s.upper, 1, s.upperWorld);
            Emit(s.lower, 2, s.lowerWorld);
            if (into.Count == start) return false;
            return true;
        }

        private static int LegOn(BodyPlan plan, int sideIndex)
        {
            int want = sideIndex == 0 ? -1 : 1;
            for (int i = 0; i < plan.Legs.Count; i++) if (plan.Legs[i].Side == want) return i;
            return sideIndex < plan.Legs.Count ? sideIndex : plan.Legs.Count - 1;
        }

        // A box round the mesh part's local bounds as placed in the world, aligned to the figure's facing.
        private static bool Fit(Matrix4x4 w, Vector3 lo, Vector3 hi, V3 right, V3 fwd, float yaw, out HitVolume box, out float side)
        {
            box = default; side = 0f;
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            for (int c = 0; c < 8; c++)
            {
                Vector3 p = w.MultiplyPoint3x4(new Vector3((c & 1) == 0 ? lo.x : hi.x, (c & 2) == 0 ? lo.y : hi.y, (c & 4) == 0 ? lo.z : hi.z));
                if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z)) return false;
                V3 q = new V3(p.x, p.y, p.z);
                float x = V3.Dot(q, right), z = V3.Dot(q, fwd);
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y); minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
            }
            const float pad = .008f;
            float cx = (minX + maxX) * .5f, cz = (minZ + maxZ) * .5f;
            V3 centre = right * cx + fwd * cz + new V3(0f, (minY + maxY) * .5f, 0f);
            box = BodyVolumes.Box(HitRegion.None, centre, new V3((maxX - minX) * .5f + pad, (maxY - minY) * .5f + pad, (maxZ - minZ) * .5f + pad), yaw);
            // Which side of the body centre the part sits on, along the facing's right.
            Vector3 origin = w.MultiplyPoint3x4(Vector3.zero);
            side = cx - V3.Dot(new V3(origin.x, origin.y, origin.z), right);
            return true;
        }
    }
}
