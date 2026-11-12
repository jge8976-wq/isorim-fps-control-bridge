using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.ImportedRendering
{
    // Keep the upstream renderer, lighting and floor handling. Imported textured faces share the original shell.
    public static class SurfaceAdapter
    {
        private const string Owner = "hsk.importedmodels.surfaces";
        private static int reports;
        public static void Install()
        {
            var build = AccessTools.Method(typeof(DynamicModels), "Build");
            if (build == null) throw new MissingMethodException("IsoRim.DynamicModels.Build");
            if (!Harmony.HasAnyPatches(Owner))
                new Harmony(Owner).Patch(build, postfix: new HarmonyMethod(typeof(SurfaceAdapter), nameof(AfterBuild)));
            // The Anomaly helper may have registered the older shared adapter before the bridge.
            // A dedicated owner makes the Ratkin fix independent of that assembly's startup order.
            const string ratkinOwner = Owner + ".ratkin";
            if (!Harmony.HasAnyPatches(ratkinOwner))
                new Harmony(ratkinOwner).Patch(build, postfix: new HarmonyMethod(typeof(SurfaceAdapter), nameof(AfterRatkinBuild)));
            Log.Message("[ImportedModels] SURFACE_ADAPTER BOUND; joined imported surfaces; imported outline shells disabled");
        }
        public static void AfterRatkinBuild(Model m, bool textured, List<DynamicModels.Part> parts)
        {
            if (m?.materials?.Any(mat => mat?.texture?.StartsWith("Imported/NewRatkinPlusR7/", StringComparison.Ordinal) == true) == true)
                AfterBuild(m, textured, parts);
        }
        private static bool Owned(string path) => path != null && (path.StartsWith("Imported/Vehicles/", StringComparison.Ordinal)
            || path.StartsWith("Imported/Anomaly/", StringComparison.Ordinal)
            || path.StartsWith("Imported/NewRatkinPlusR7/", StringComparison.Ordinal));
        public static void AfterBuild(Model m, bool textured, List<DynamicModels.Part> parts)
        {
            if (m?.materials == null || parts == null || !m.materials.Any(mat => mat != null && Owned(mat.texture))) return;
            // A fixed 0.03-cell expansion can invert small faces on dense/concave imported meshes.
            // These models retain their original surface and native lighting without the expanded shell.
            int outlinesRemoved = 0;
            for (int i = parts.Count - 1; i >= 0; i--)
                if (parts[i].outline) { DynamicModels.Retire(parts[i].mesh); parts.RemoveAt(i); outlinesRemoved++; }
            if (!textured || m.material == null || m.normals == null) return;
            var used = new SortedDictionary<int, List<Vector3>>();
            var lifted = new SortedDictionary<int, List<Vector3>>();
            bool allTextured = true;
            for (int i = 0; i + 2 < m.positions.Length; i += 3)
            {
                int mi = m.material[i];
                if (!Owned(m.materials[mi].texture)) { allTextured = false; continue; }
                if (!used.TryGetValue(mi, out var original))
                { used[mi] = original = new List<Vector3>(); lifted[mi] = new List<Vector3>(); }
                for (int j = i; j < i + 3; j++)
                { original.Add(m.positions[j]); lifted[mi].Add(m.positions[j] + m.normals[i] * .004f); }
            }
            if (used.Count == 0) return;
            // Native Build appends textured material groups in material-index order. Verify its actual output first.
            int start = parts.Count - used.Count;
            if (start < 0) return;
            int index = start;
            foreach (var group in lifted)
            {
                var part = parts[index++];
                if (part.outline || part.mesh == null || part.mesh.vertexCount != group.Value.Count) return;
                Vector3[] actual = part.mesh.vertices;
                for (int i = 0; i < actual.Length; i++)
                    if ((actual[i] - group.Value[i]).sqrMagnitude > 1e-9f) return;
            }
            index = start;
            foreach (var group in used)
            {
                Mesh mesh = parts[index++].mesh;
                mesh.SetVertices(group.Value); mesh.RecalculateBounds();
                MeshGuard.Check(mesh, "imported joined surface");
            }
            // With an opaque/textured surface the native white undercoat can protrude through concave detail.
            // Keep untextured faces; remove only a redundant whole-model undercoat.
            if (allTextured)
                for (int i = start - 1; i >= 0; i--)
                    if (!parts[i].outline) { DynamicModels.Retire(parts[i].mesh); parts.RemoveAt(i); }
            if (reports++ < 12)
                Log.Message("[ImportedModels] SURFACE_FIXED model=" + m.id + "; faces=" + (m.positions.Length / 3)
                    + "; texturedGroups=" + used.Count + "; undercoatRemoved=" + allTextured + "; outlineShellsRemoved=" + outlinesRemoved);
        }
    }
}
