using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class VehicleScopeHud
    {
        // Reuse the six cached optical textures. This adds no second camera or render target.
        internal static bool Draw(float zoom)
        {
            VehicleScopeTemplate template = VehicleWeapons.Optic;
            string hint = ControlKeys.Label(ControlAction.Aim) + L.T(" 退出炮镜 · 滚轮调倍率", " exit optic · wheel for zoom");
            if (!ScopeHud.DrawOptic(VehicleScopeRules.Style(template), zoom, VehicleScopeRules.Label(template), hint)) return false;
            if (!FpsMod.Settings.showHints) return true;
            Color color = GUI.color; GameFont font = Text.Font; TextAnchor anchor = Text.Anchor;
            try
            {
                GUI.color = new Color(.85f, .92f, .95f, .95f); Text.Font = GameFont.Tiny; Text.Anchor = TextAnchor.UpperLeft;
                float relative = Mathf.DeltaAngle(VehicleApi.Yaw(VehicleControl.Vehicle), VehicleAimAlignment.TurretYaw(VehicleControl.Vehicle, VehicleWeapons.Current));
                V3 bore=VehicleWeapons.Direction(VehicleControl.Vehicle,VehicleWeapons.Current), sight=VehicleWeapons.ScopeDirection;
                float error=Vector3.Angle(VehicleModels.U(bore),VehicleModels.U(sight));
                string range = VehicleWeapons.AimRange >= 0f ? VehicleWeapons.AimRange.ToString("0.0") : "—";
                string status = L.T("视线接触距离 ", "Sight contact range ") + range + L.T(" 格", " cells")
                    + "\n" + L.T("仰角 ", "Elevation ") + (-VehicleWeapons.Pitch).ToString("+0.0;-0.0;0.0") + "°"
                    + "\n" + L.T("炮塔相对车头 ", "Turret / hull ") + relative.ToString("+0.0;-0.0;0.0") + "°"
                    + "\n" + L.T("炮管与瞄准线偏差 ", "Bore / sight error ") + error.ToString("0.0") + "°";
                Widgets.Label(new Rect(24f, 104f, 420f, 94f), status);
            }
            finally { GUI.color = color; Text.Font = font; Text.Anchor = anchor; }
            return true;
        }
    }
}
