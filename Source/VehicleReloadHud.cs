using System;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class VehicleReloadHud
    {
        private const int Segments = 80;
        private static readonly Vector2[] ring = MakeRing();
        private static Vector2[] MakeRing()
        {
            var points = new Vector2[Segments + 1];
            for (int i = 0; i <= Segments; i++)
            { float angle = i * 2f * Mathf.PI / Segments; points[i] = new Vector2(Mathf.Sin(angle), -Mathf.Cos(angle)); }
            return points;
        }
        internal static void Draw()
        {
            string blocked = VehicleControl.ControlBlockReason;
            if (blocked != null) DrawBlocked(blocked);
        }
        private static void DrawBlocked(string reason)
        {
            Color color = GUI.color; GameFont font = Text.Font; TextAnchor anchor = Text.Anchor;
            try
            {
                GUI.color = Color.white; Text.Font = GameFont.Small; Text.Anchor = TextAnchor.MiddleCenter;
                float width = Math.Min(520f, UI.screenWidth - 24f);
                Rect rect = new Rect((UI.screenWidth - width) * .5f, Math.Min(UI.screenHeight * .5f + 90f, UI.screenHeight - 100f), width, 58f);
                Widgets.DrawBoxSolid(rect, new Color(.04f, .025f, .015f, .9f));
                GUI.color = new Color(1f, .78f, .35f); Widgets.Label(rect.ContractedBy(6f), reason);
            }
            finally { GUI.color = color; Text.Font = font; Text.Anchor = anchor; }
        }
        internal static (float Filled, Color Tint, string Label) Status(object turret)
        {
            VehicleWeaponClock clock = VehicleWeapons.ClockFor(turret);
            int reload = VehicleApi.Int(turret, "ReloadTicks"), tick = Find.TickManager.TicksGame;
            bool heat = VehicleApi.Bool(turret, "OnCooldown"), disabled = VehicleApi.Bool(turret, "TurretDisabled");
            float filled; Color tint; string label;
            if (heat)
            {
                float temperature = Math.Max(0f, VehicleApi.Float(turret, "currentHeatRate"));
                filled = VehicleWeaponClock.Filled(temperature, 100f); tint = new Color(1f, .58f, .18f);
                object cooling = VehicleApi.Get(VehicleApi.Get(turret, "def"), "cooldown");
                float rate = cooling == null ? 0f : VehicleApi.Float(cooling, "dissipationRate") * VehicleApi.Float(cooling, "dissipationCapMultiplier");
                int wait = Math.Max(0, 60 - VehicleApi.Int(turret, "ticksSinceLastShot"));
                string time = rate > 0f && float.IsFinite(rate) ? " · ~" + ((wait + Math.Ceiling(temperature / rate) + 1f) / 60f).ToString("0.0") + "s" : "";
                label = L.T("散热 ", "Cooling ") + (filled * 100f).ToString("0") + "%" + time;
                if (reload > 0) label += "\n" + L.T("装填暂停", "Reload on hold");
            }
            else if (reload > 0)
            {
                filled = VehicleWeaponClock.Filled(reload, VehicleApi.Int(turret, "MaxTicks")); tint = new Color(.3f, .85f, 1f);
                label = L.T("装填 ", "Reload ") + VehicleWeaponClock.Seconds(reload, VehicleControl.Efficiency).ToString("0.0") + "s";
            }
            else if (disabled)
            { filled = 0f; tint = new Color(1f, .35f, .3f); label = L.T("武器不可用", "Weapon unavailable"); }
            else if (!VehicleApi.Bool(turret, "HasAmmo"))
            { filled = 0f; tint = new Color(1f, .55f, .3f); label = L.T("无弹药", "No ammunition"); }
            else if (clock.Remaining(tick) > 0)
            {
                filled = clock.Progress(tick); tint = new Color(.3f, .85f, 1f);
                label = L.T("射击循环 ", "Cycling ") + VehicleWeaponClock.Seconds(clock.Remaining(tick), 1f).ToString("0.0") + "s";
            }
            else
            { filled = 1f; tint = new Color(.4f, 1f, .6f); label = L.T("就绪", "Ready"); }
            if (Find.TickManager.Paused && (heat || reload > 0 || clock.Remaining(tick) > 0)) label += L.T(" · 暂停", " · paused");
            return (filled, tint, label);
        }

        internal static void DrawRing(Vector2 center, float radius, float filled, Color tint)
        {
            for (int i = 0; i < Segments; i++)
            {
                Vector2 a = center + ring[i] * radius, b = center + ring[i + 1] * radius;
                Widgets.DrawLine(a, b, new Color(.18f, .24f, .29f, .92f), 4f);
                float part = Mathf.Clamp01(filled * Segments - i);
                if (part > 0f) Widgets.DrawLine(a, a + (b - a) * part, tint, 2.5f);
            }
        }
    }
}
