using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // A visual binding owned by an art pack. Gameplay remains with the weapon's actual Verb.
    public sealed class ImportedWeaponDef : Def
    {
        public string weapon, modelId, mesh, texture, kind = "Gun", aim = "PreserveExisting", scope = "None";
        public Vector3 min, max;
        public List<ImportedAnchor> anchors = new List<ImportedAnchor>();
        public float rearDistance = .22f, magnification = 1.35f;
        internal ModelSpec spec;
    }
    public sealed class ImportedAnchor { public string name; public Vector3 position; }

    internal static class ImportedWeapons
    {
        private static Dictionary<string, ModelSpec> models;
        private static void Read()
        {
            if (models != null) return;
            models = new Dictionary<string, ModelSpec>(StringComparer.OrdinalIgnoreCase);
            foreach (ImportedWeaponDef d in DefDatabase<ImportedWeaponDef>.AllDefsListForReading)
            {
                if (d.modContentPack == null || string.IsNullOrEmpty(d.weapon) || string.IsNullOrEmpty(d.mesh) || string.IsNullOrEmpty(d.texture)) continue;
                if (!Enum.TryParse(d.kind, out ModelKind kind)) { Log.Warning("[FpsBridge] Invalid imported weapon kind: " + d.defName); continue; }
                var anchors = new Dictionary<string, V3>();
                foreach (ImportedAnchor a in d.anchors) anchors[a.name] = new V3(a.position.x, a.position.y, a.position.z);
                var s = new ModelSpec(d.weapon, d.modelId ?? d.weapon, kind, "", d.texture,
                    new V3(d.min.x, d.min.y, d.min.z), new V3(d.max.x, d.max.y, d.max.z), anchors);
                s.Import = d; d.spec = s; models[d.weapon] = s;
            }
        }
        internal static ModelSpec For(string defName) { Read(); return models.TryGetValue(defName ?? "", out ModelSpec m) ? m : null; }
    }

    public sealed class ImportedTurretGeometry : DefModExtension
    {
        public Vector3 muzzle, pitchPivot, eye;
    }
}
