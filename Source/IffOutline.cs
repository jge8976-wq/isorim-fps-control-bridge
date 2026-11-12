using System.Collections.Generic;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Friend-or-foe outlines: while the possessed pawn is drafted, every person, animal and mech IsoRim draws within
    // range gets a bright rim in its mark's colour (red hostile, green own side, blue friendly or guest, yellow neutral).
    // It reuses IsoRim's own outline shell (each figure's hull mesh, drawn with front faces culled so only the rim beyond
    // the body shows): a copy with white vertex colours (IsoRim bakes them dark) drawn a little wider with a material
    // whose colour is the mark's. Depth-tested like the figure, so nothing shows through walls.
    internal static class IffOutline
    {
        private const float Range = 60f, Widen = 1.035f;
        private static readonly Dictionary<Mesh, Mesh> white = new Dictionary<Mesh, Mesh>();
        private static readonly Dictionary<Color, Material> mats = new Dictionary<Color, Material>();
        private static Pawn viewer;
        private static int viewerFrame = -1;
        private static bool on;

        // Whether this frame draws outlines (possessed, drafted, IsoRim's view): checked once per frame.
        private static bool Active()
        {
            if (viewerFrame == Time.frameCount) return on;
            viewerFrame = Time.frameCount;
            viewer = Bridge.ShootingPawn;
            on = viewer != null && viewer.Drafted && View3D.Active && FpsMod.Settings.showIff;
            return on;
        }

        // From FigureMeshes' Submit hook while IsoRim draws pawn's figure: parts and the matrix before IsoRim's floor step.
        public static void Submit(Pawn pawn, List<DynamicModels.Part> parts, Matrix4x4 matrix)
        {
            if (!Active() || pawn == viewer || pawn.Dead || pawn.Map != viewer.Map) return;
            if (!pawn.Position.InHorDistOf(viewer.Position, Range) || pawn.Position.Fogged(pawn.Map)) return;
            Color c = IffMarkers.Kind(pawn, out _);
            if (pawn.Downed) c *= .55f;
            c.a = 1f;
            Material mat = MaterialFor(c);
            if (mat == null) return;
            Matrix4x4 drawn = Floors.DrawnMatrix(matrix * Matrix4x4.Scale(new Vector3(Widen, Widen, Widen)), out int floor);
            MaterialPropertyBlock block = RoofClip.FloorBlock(floor);
            for (int i = 0; i < parts.Count; i++)
            {
                DynamicModels.Part p = parts[i];
                if (p == null || !p.outline || p.mesh == null) continue;
                Mesh hull = p.mesh;
                Mesh rim = WhiteOf(hull);
                if (rim != null) Graphics.DrawMesh(rim, drawn, mat, 0, null, 0, block);
            }
        }

        private static Material MaterialFor(Color c)
        {
            if (mats.TryGetValue(c, out Material m)) return m;
            m = R3DMaterials.Make("IsoRimOutline", "FpsBridge_IffOutline");
            if (m != null)
            {
                m.SetFloat(Shader.PropertyToID("_OutlinePush"), .2f);   // as IsoRim's own moving outlines
                m.color = c;
            }
            mats[c] = m;
            return m;
        }

        private static Mesh WhiteOf(Mesh hull)
        {
            if (white.TryGetValue(hull, out Mesh w)) return w;
            if (white.Count > 3000) Sweep();
            w = null;
            try
            {
                if (hull.isReadable)
                {
                    w = new Mesh();
                    Vector3[] v = hull.vertices;
                    w.vertices = v;
                    var tags = new List<Vector3>(v.Length);
                    for (int i = 0; i < v.Length; i++) tags.Add(RoofClip.MovingTag);   // IsoRim's moving-model tag (roof clipping)
                    w.SetUVs(1, tags);
                    var colors = new Color32[v.Length];
                    for (int i = 0; i < colors.Length; i++) colors[i] = new Color32(255, 255, 255, 255);
                    w.colors32 = colors;
                    w.triangles = hull.triangles;
                    w.RecalculateBounds();
                }
            }
            catch (System.Exception e) { Log.WarningOnce("[FpsBridge] IFF outline copy failed: " + e.Message, 93700101); w = null; }
            white[hull] = w;
            return w;
        }

        private static void Sweep()
        {
            var dead = new List<Mesh>();
            foreach (var kv in white) if (kv.Key == null) dead.Add(kv.Key);
            foreach (Mesh m in dead) { if (white[m] != null) Object.Destroy(white[m]); white.Remove(m); }
        }

        public static void Reset()
        {
            foreach (Mesh m in white.Values) if (m != null) Object.Destroy(m);
            white.Clear();
        }
    }
}
