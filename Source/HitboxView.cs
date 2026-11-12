using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Infantry toggles the overlay. Vehicle crews hold the same key to inspect the aimed body part or vehicle.
    internal static class HitboxView
    {
        private const float Range = 40f;
        private static readonly List<HitVolume> volumes = new List<HitVolume>();
        private static int frame = -1;
        private static Pawn inspector;
        private static Ballistics.ExternalTrace inspectionTrace;

        // 0.8.1: a tap of Alt turns the volumes on or off (kept between sessions). A tap is Alt pressed and released
        // with no other key meanwhile, so Alt+1-4 (game speed) leaves the display alone.
        private static bool shown, altCombo;
        public static bool Held => shown;

        // Once per input frame while possessed.
        public static void Frame()
        {
            bool alt = ControlKeys.Held(ControlAction.Overlay);
            if (ControlKeys.Down(ControlAction.Overlay)) altCombo = false;
            else if (alt && Input.anyKeyDown) altCombo = true;
            if (ControlKeys.Up(ControlAction.Overlay) && !alt && !altCombo) shown = !shown;
        }

        public static void Draw(Pawn self)
        {
            if (frame == Time.frameCount || self?.Map == null) return;
            frame = Time.frameCount;
            Map map = self.Map;
            V3 origin = FloorSpace.Spot(self);
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == self || p.Dead || p.Position.Fogged(map) || (FloorSpace.Spot(p) - origin).Length > Range) continue;
                volumes.Clear();
                BodyRegions.AddVolumes(p, volumes);
                if (volumes.Count == 0) continue;
                if (p.RaceProps.Animal && FigureMeshes.DrawHitMesh(p)) continue;
                Vector3 offset = Vector3.zero;
                for (int k = 0; k < volumes.Count; k++) Visuals.Volume(volumes[k], offset);
            }
        }

        internal static void DrawInspection(Pawn vehicle)
        {
            var report = Inspect(vehicle);
            if (report.Rows != null) Panel(report.Title, report.Rows);
        }

        internal static (string Title, IReadOnlyList<string> Rows) Inspect(Pawn vehicle)
        {
            Map map = vehicle?.Map;
            if (map == null) return default;
            if (inspector != vehicle)
            {
                inspector = vehicle;
                inspectionTrace = new Ballistics.ExternalTrace(vehicle);
            }
            V3 eye = VehicleModels.V(VehicleControl.Eye());
            V3 end = eye + HitMath.Direction(FirstPerson.Yaw, FirstPerson.Pitch) * Range;
            bool contact = inspectionTrace.Segment(map, eye, end, out float fraction, out Thing target,
                out BodyPartRecord part, out _, out _);
            if (contact && target != null && !target.Position.Fogged(map))
            {
                if (target is Pawn pawn && !VehicleApi.IsVehicle(pawn))
                {
                    part ??= pawn.RaceProps.body.corePart;
                    if (part != null)
                    {
                        float current = pawn.health.hediffSet.GetPartHealth(part);
                        float maximum = part.def.GetMaxHealth(pawn);
                        return (pawn.LabelShortCap.ToString(), new[] { Hp(part.LabelCap.ToString(), current, maximum) });
                    }
                }
                else if (target is Pawn targetVehicle)
                {
                    V3 hit = V3.Lerp(eye, end, fraction);
                    return ComponentHealth(targetVehicle, FloorSpace.Cell(map, hit, targetVehicle));
                }
                else if (ObjectTargets.Damageable(target))
                {
                    return (target.LabelShortCap.ToString(), new[] { Hp(L.T("耐久", "Durability"), target.HitPoints, target.MaxHitPoints) });
                }
            }
            return ComponentHealth(vehicle, null);
        }

        internal static void EndVehicleInspection()
        {
            inspector = null;
            inspectionTrace = null;
        }

        private static (string Title, IReadOnlyList<string> Rows) ComponentHealth(Pawn vehicle, IntVec3? hit)
        {
            object stats = VehicleApi.Get(vehicle, "statHandler");
            var components = VehicleApi.List(stats, "components").ToArray();
            IEnumerable<object> listed = components;
            if (hit.HasValue && VehicleApi.Get(stats, "componentLocations") is IDictionary cells)
            {
                var rotate = AccessTools.Method(VehicleApi.PawnType.Assembly.GetType("Vehicles.Ext_Vehicles"), "RotatedBy",
                    new[] { typeof(IntVec2), typeof(Rot4), typeof(IntVec2) });
                if (rotate != null)
                {
                    IntVec3 cell = vehicle.OccupiedRect().ClosestCellTo(hit.Value);
                    var offset = new IntVec2(cell.x - vehicle.Position.x, cell.z - vehicle.Position.z);
                    object key = rotate.Invoke(null, new object[] { offset, vehicle.Rotation, vehicle.def.Size });
                    if (cells[key] is IList parts && parts.Count > 0) listed = parts.Cast<object>();
                }
            }
            var rows = new List<string>
            {
                Hp(L.T("整体", "Total"), components.Sum(c => VehicleApi.Float(c, "Health")),
                    components.Sum(c => VehicleApi.Float(c, "MaxHealth")))
            };
            foreach (object component in listed.OrderBy(c => VehicleApi.Float(c, "HealthPercent")).Take(6))
            {
                object props = VehicleApi.Get(component, "props");
                string label = VehicleApi.Get(props, "label") as string ?? VehicleApi.Get(props, "key") as string ?? "?";
                rows.Add(Hp(label, VehicleApi.Float(component, "Health"), VehicleApi.Float(component, "MaxHealth")));
            }
            return (vehicle.LabelShortCap.ToString(), rows);
        }

        private static string Hp(string label, float current, float maximum) =>
            label + "   " + current.ToString("0.#") + " / " + maximum.ToString("0.#") + " HP";

        private static void Panel(string title, IReadOnlyList<string> rows)
        {
            const float rowHeight = 21f;
            float width = Mathf.Min(320f, UI.screenWidth * .32f);
            Rect panel = new Rect(12f, 68f, width, 32f + rows.Count * rowHeight);
            Color old = GUI.color; GameFont font = Text.Font; TextAnchor anchor = Text.Anchor;
            try
            {
                GUI.color = Color.white; Text.Font = GameFont.Small; Text.Anchor = TextAnchor.UpperLeft;
                Widgets.DrawBoxSolid(panel, new Color(.015f, .035f, .045f, .9f));
                Widgets.Label(new Rect(panel.x + 10f, panel.y + 5f, width - 20f, 23f), title);
                Text.Font = GameFont.Tiny;
                for (int i = 0; i < rows.Count; i++)
                    Widgets.Label(new Rect(panel.x + 10f, panel.y + 28f + i * rowHeight, width - 20f, rowHeight), rows[i]);
            }
            finally { GUI.color = old; Text.Font = font; Text.Anchor = anchor; }
        }
    }
}
