using System;
using System.Collections.Generic;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal sealed class TargetDwell
    {
        public const float Delay = .55f;
        private object target;
        private float since, last;
        public void Reset() { target = null; since = last = 0f; }
        public bool Observe(object next, float now)
        {
            if (next == null || !float.IsFinite(now)) { Reset(); return false; }
            if (!ReferenceEquals(target, next) || now < last || now - last > .25f) { target = next; since = now; }
            last = now;
            return now - since >= Delay;
        }
    }

    // Local attack/picking volumes only: no changes to pathfinding, movement or vehicle collision.
    internal static class ObjectTargets
    {
        private static readonly Dictionary<Model, Bounds> bounds = new Dictionary<Model, Bounds>();
        public static bool Damageable(Thing t) => t != null && !t.Destroyed && !(t is Pawn) && t.def?.useHitPoints == true
            && (t.def.category == ThingCategory.Building || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Plant);
        public static void Volumes(Thing t, List<HitVolume> into)
        {
            Ballistics.AddSolid(t, into); // Also retains walls/closed doors that block a more distant target.
            if (into.Count != 0 || !Damageable(t)) return;
            if (Api.ModelsOn && ModelLibrary.TryGet(t.def, out ModelEntry entry) && entry.HasModel && !entry.def.flat)
            {
                Model m = entry.Pick(t);
                if (m?.positions != null && m.positions.Length > 0)
                {
                    if (!bounds.TryGetValue(m, out Bounds b))
                    {
                        b = new Bounds(m.positions[0], Vector3.zero);
                        foreach (Vector3 p in m.positions) b.Encapsulate(p);
                        bounds[m] = b;
                    }
                    Vector3 origin = ModelBaker.PointOnMap(t, entry, m, Vector3.zero, out Vector3 fit, out float yaw);
                    Matrix4x4 place = Matrix4x4.TRS(origin, Quaternion.AngleAxis(yaw, Vector3.up), fit);
                    if (entry.def.layFlat) place *= Matrix4x4.Translate(new Vector3(0, m.halfDepth, 0)) * Matrix4x4.Rotate(Quaternion.Euler(90, 0, 0));
                    Bounds world = new Bounds(place.MultiplyPoint3x4(b.min), Vector3.zero);
                    for (int i = 0; i < 8; i++) world.Encapsulate(place.MultiplyPoint3x4(new Vector3(
                        (i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z)));
                    Add(world.center, world.extents, into); return;
                }
            }
            CellRect r = t.OccupiedRect(); Vector3 o = FloorSpace.Offset(t);
            float height = Bindings.PickHeight(t);
            if (!(height > .01f)) height = t.def.category == ThingCategory.Item ? .16f : .5f;
            float hx = t.def.category == ThingCategory.Item ? Mathf.Clamp(t.def.graphicData?.drawSize.x * .3f ?? .25f, .12f, .45f) : r.Width * .45f;
            float hz = t.def.category == ThingCategory.Item ? hx : r.Height * .45f;
            Add(new Vector3((r.minX + r.maxX + 1) * .5f + o.x, height * .5f + o.y, (r.minZ + r.maxZ + 1) * .5f + o.z), new Vector3(hx, height * .5f, hz), into);
        }
        private static void Add(Vector3 c, Vector3 e, List<HitVolume> into) => into.Add(BodyVolumes.Box(HitRegion.Solid,
            new V3(c.x, c.y, c.z), new V3(Mathf.Max(.015f, e.x), Mathf.Max(.015f, e.y), Mathf.Max(.015f, e.z)), 0f));
    }

    internal static class ObjectDurability
    {
        internal const float Range = 6f;
        private static readonly TargetDwell dwell = new TargetDwell();
        public static void Reset() => dwell.Reset();
        public static void DrawHud(Pawn pawn)
        {
            if (pawn?.Spawned != true || ControlCamera.External || WorkControl.Auto || TurretControl.Manned || VehicleControl.On
                || InventoryPanel.Blocking || Bridge.MainTabBlocks) { Reset(); return; }
            V3 eye = Ballistics.Eye(pawn);
            Thing t = Melee.First(pawn, eye, eye + ControlCamera.ShotDirection(pawn) * Range, out _, out _);
            if (!dwell.Observe(ObjectTargets.Damageable(t) ? t : null, Time.unscaledTime)) return;
            float width = Mathf.Min(270, UI.screenWidth * .3f), x = UI.screenWidth - width - 22, y = UI.screenHeight - 194;
            Color old = GUI.color; GameFont font = Text.Font; TextAnchor anchor = Text.Anchor;
            try
            {
                GUI.color = Color.white; Text.Font = GameFont.Small; Text.Anchor = TextAnchor.UpperLeft;
                Widgets.DrawBoxSolid(new Rect(x - 10, y - 7, width + 20, 76), new Color(.03f, .04f, .05f, .85f));
                Widgets.Label(new Rect(x, y, width, 24), t.LabelShortCap);
                Widgets.Label(new Rect(x, y + 24, width, 23), L.T("耐久 ", "Durability ") + t.HitPoints + " / " + t.MaxHitPoints);
                float fill = Mathf.Clamp01(t.HitPoints / (float)Math.Max(1, t.MaxHitPoints));
                Widgets.DrawBoxSolid(new Rect(x, y + 52, width, 8), new Color(.13f, .16f, .18f));
                Widgets.DrawBoxSolid(new Rect(x, y + 52, width * fill, 8), Color.Lerp(new Color(1f, .3f, .2f), new Color(.4f, .9f, .65f), fill));
            }
            finally { GUI.color = old; Text.Font = font; Text.Anchor = anchor; }
        }
    }
}
