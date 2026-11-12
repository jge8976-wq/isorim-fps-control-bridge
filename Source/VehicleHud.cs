using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class VehicleHud
    {
        private sealed class Part { internal string Label; internal float Health; }
        private static readonly List<Part> parts = new List<Part>();
        private static Pawn snapshot;
        private static int next;
        private static float fuel, capacity, health;
        private static string fuelName;
        private static VehicleHudTemplate template;
        private static readonly Color cyan = new Color(.3f, .85f, 1f);
        private static void Refresh(Pawn vehicle)
        {
            int tick = Find.TickManager.TicksGame;
            if (snapshot == vehicle && tick < next && next - tick <= 12) return;
            snapshot = vehicle; next = tick + 12; parts.Clear();
            object stats = VehicleApi.Get(vehicle, "statHandler");
            health = VehicleApi.Float(stats, "HealthPercent");
            foreach (object component in VehicleApi.List(stats, "components"))
            {
                object props = VehicleApi.Get(component, "props");
                parts.Add(new Part { Label = VehicleApi.Get(props, "label") as string ?? VehicleApi.Get(props, "key") as string ?? "?", Health = VehicleApi.Float(component, "HealthPercent") });
            }
            // Worst condition first: the lower rows never hide a critical component behind healthy ones.
            parts.Sort((a, b) => a.Health.CompareTo(b.Health));
            object tank = VehicleApi.Get(vehicle, "CompFueledTravel");
            fuel = tank == null ? 0f : VehicleApi.Float(tank, "Fuel"); capacity = tank == null ? 0f : VehicleApi.Float(tank, "FuelCapacity");
            fuelName = (VehicleApi.Get(VehicleApi.Get(tank, "Props"), "fuelType") as Def)?.LabelCap.ToString() ?? L.T("燃料 / 电量", "Fuel / charge");
            template = VehicleHudRules.Select(VehicleModels.Profile(vehicle).hudTemplate, vehicle.def.defName, VehicleApi.Get(VehicleApi.Get(vehicle, "VehicleDef"), "type")?.ToString());
        }
        private static Color Condition(float f) => f > .65f ? new Color(.35f, .95f, .55f) : f > .3f ? new Color(1f, .8f, .25f) : new Color(1f, .3f, .22f);
        private static string TemplateLabel => template switch {
            VehicleHudTemplate.Tank => L.T("坦克 / 装甲", "TANK / ARMOUR"),
            VehicleHudTemplate.Special => L.T("特种车辆", "SPECIAL VEHICLE"),
            VehicleHudTemplate.FixedWing => L.T("固定翼", "FIXED WING"),
            VehicleHudTemplate.Rotorcraft => L.T("旋翼机", "ROTORCRAFT"),
            VehicleHudTemplate.Boat => L.T("船只", "BOAT"),
            _ => L.T("轮式车辆", "WHEELED VEHICLE") };
        internal static void Draw(Pawn shown = null)
        {
            Pawn vehicle = shown ?? VehicleControl.Vehicle;
            if (vehicle == null) return;
            bool manual = VehicleControl.Owns(vehicle);
            Refresh(vehicle);
            Color old = GUI.color; GameFont font = Text.Font; TextAnchor anchor = Text.Anchor;
            try
            {
                GUI.color = Color.white; Text.Font = GameFont.Tiny; Text.Anchor = TextAnchor.UpperLeft;
                float width = Math.Min(380f, UI.screenWidth * .38f), height = Math.Min(375f, UI.screenHeight * .62f);
                Rect panel = new Rect(12f, Math.Max(8f, UI.screenHeight - height - (manual ? 46f : 285f)), width, height);
                Widgets.DrawBoxSolid(panel, new Color(.015f, .035f, .045f, .9f));
                Widgets.DrawBoxSolid(new Rect(panel.x, panel.y, width, 3f), cyan);
                float x = panel.x + 10f, y = panel.y + 8f, w = width - 20f;
                Label(x, y, w, TemplateLabel + " · " + vehicle.LabelShort, cyan); y += 21f;
                Label(x, y, w, manual ? VehicleControl.CameraLabel + " · " + (VehicleControl.Uncrewed ? L.T("无人控制", "Uncrewed control") : VehicleApi.RoleLabel(VehicleControl.Seat)) : L.T("原生载具状态", "Native vehicle status"), Color.white); y += 20f;
                Label(x, y, w, L.T("速度 ", "Speed ") + (manual ? VehicleDriving.Speed.ToString("0.0") : "—") + L.T(" 格/秒", " cells/s") + "  |  "
                    + L.T("乘员效率 ", "Crew efficiency ") + ((manual ? VehicleControl.Efficiency : VehicleApi.Efficiency(vehicle)) * 100f).ToString("0") + "%", cyan); y += 23f;
                Silhouette(new Rect(x, y, 96f, 90f));
                Label(x + 104f, y, w - 104f, L.T("整体耐久 ", "Integrity ") + (health * 100f).ToString("0") + "%", Condition(health));
                for (int i = 0; i < Math.Min(3, parts.Count); i++)
                    Label(x + 104f, y + 22f + i * 20f, w - 104f, parts[i].Label + " " + (parts[i].Health * 100f).ToString("0") + "%", Condition(parts[i].Health));
                y += 94f;
                if (capacity > 0f)
                {
                    Label(x, y, w, fuelName + " " + fuel.ToString("0.0") + " / " + capacity.ToString("0.0"), Condition(fuel / capacity));
                    Bar(new Rect(x, y + 19f, w, 4f), fuel / capacity); y += 29f;
                }
                else { Label(x, y, w, L.T("燃料：本车无需燃料", "Fuel: not required by this vehicle"), Color.gray); y += 24f; }
                object gun = manual ? VehicleWeapons.Current : VehicleApi.Turrets(vehicle).FirstOrDefault();
                if (gun != null)
                {
                    object def = VehicleApi.Get(gun, "def");
                    Label(x, y, w, ((def as Def)?.LabelCap.ToString() ?? VehicleApi.TurretKey(gun)) + " · " + (manual ? VehicleWeapons.ModeLabel : VehicleApi.Get(VehicleApi.Get(gun,"CurrentFireMode"),"label") as string), Color.white); y += 20f;
                    ThingDef loaded = VehicleApi.Get(gun, "loadedAmmo") as ThingDef ?? VehicleApi.Get(gun, "savedAmmoType") as ThingDef;
                    int reserve = loaded == null ? 0 : vehicle.inventory.innerContainer.Where(t => t.def == loaded).Sum(t => t.stackCount);
                    bool finite = VehicleApi.Get(def, "ammunition") != null;
                    string ammo = loaded?.LabelCap.ToString() ?? (VehicleApi.Get(gun, "ProjectileDef") as Def)?.LabelCap.ToString() ?? "—";
                    Label(x, y, w, ammo + "  " + (finite ? VehicleApi.Int(gun, "shellCount") + "/" + VehicleApi.Int(def, "magazineCapacity") + L.T(" · 备弹 ", " · reserve ") + reserve : L.T("无限弹药", "Unlimited ammunition")), cyan); y += 21f;
                    Label(x, y, w, manual ? VehicleWeapons.StateLabel : VehicleApi.Int(gun,"ReloadTicks") > 0 ? L.T("装填中", "Loading") : VehicleApi.Bool(gun,"HasAmmo") ? L.T("已装填", "Loaded") : L.T("无弹药", "No ammunition"), VehicleApi.Int(gun, "ReloadTicks") > 0 ? Color.yellow : Color.white); y += 22f;
                }
                else { Label(x, y, w, L.T("当前席位无武器", "No weapon at this seat"), Color.gray); y += 42f; }
                object machine = manual ? VehicleWeapons.MachineGuns().FirstOrDefault(t => t != gun) : null;
                if (machine != null)
                {
                    int remaining = VehicleApi.Int(machine, "ReloadTicks");
                    Label(x, y, w, (ControlKeys.Label(ControlAction.VehicleMachineGun) + L.T(" 机枪 ", " MG ")) + VehicleApi.Int(machine, "shellCount") + "/"
                        + VehicleApi.Int(VehicleApi.Get(machine, "def"), "magazineCapacity") + " · " + VehicleWeapons.ModeLabelFor(machine)
                        + (remaining > 0 ? L.T(" · 装填 ", " · loading ") + VehicleWeaponClock.Seconds(remaining, VehicleControl.Efficiency).ToString("0.0") + "s" : ""), cyan);
                    y += 22f;
                }
                if (!manual) { Label(x,panel.yMax-35f,w,L.T("使用载具框架的驾驶 / 飞行 / 航行指令", "Use native driving / flight / sailing commands"),Color.gray); return; }
                if (!string.IsNullOrEmpty(VehicleDriving.Status)) { Label(x, y, w, VehicleDriving.Status, Color.yellow); y += 19f; }
                Label(x, panel.yMax - 35f, w, ControlKeys.Label(ControlAction.ViewCycle) + L.T(" 视角  ", " view  ") + ControlKeys.Label(ControlAction.Aim) + L.T(" 炮镜  ", " optic  ")
                    + ControlKeys.Label(ControlAction.NextWeapon) + L.T(" 武器  ", " weapon  ") + ControlKeys.Label(ControlAction.FireMode) + L.T(" 模式", " mode"), Color.gray);
                string exit = AerialFlight.Airborne(vehicle)
                    ? (VehicleControl.ExternalFollow ? ControlKeys.Label(ControlAction.ManualWork) + L.T(" 跳伞  ", " parachute  ")
                        : ControlKeys.Label(ControlAction.AirParachute) + L.T(" 长按 3 秒跳伞  ", " hold 3 s to parachute  "))
                    : ControlKeys.Label(ControlAction.ManualWork) + L.T(" 下车  ", " disembark  ");
                Label(x, panel.yMax - 18f, w, ControlKeys.Label(ControlAction.Reload) + L.T(" 装填  ", " reload  ") + exit
                    + ControlKeys.Label(ControlAction.ExitRecall) + L.T(" 退出控制", " exit control"), Color.gray);
            }
            finally { GUI.color = old; Text.Font = font; Text.Anchor = anchor; }
        }
        private static void Label(float x, float y, float w, string s, Color color)
        { GUI.color = color; Widgets.Label(new Rect(x, y, w, 20f), s); GUI.color = Color.white; }
        private static void Bar(Rect rect, float filled)
        { Widgets.DrawBoxSolid(rect, new Color(.15f,.18f,.2f)); rect.width *= Mathf.Clamp01(filled); Widgets.DrawBoxSolid(rect, Condition(filled)); }
        private static void Silhouette(Rect r)
        {
            Color color = Condition(health);
            void Box(float x, float y, float w, float h, Color c) => Widgets.DrawBoxSolid(new Rect(r.x + x, r.y + y, w, h), c);
            Box(34, 15, 28, 63, color);
            if (template == VehicleHudTemplate.FixedWing) { Box(5,34,86,13,color); Box(20,70,56,9,color); }
            else if (template == VehicleHudTemplate.Rotorcraft) { Box(4,20,88,4,color); Box(46,0,4,48,color); Box(23,73,50,5,color); }
            else if (template == VehicleHudTemplate.Boat)
            { Widgets.DrawLine(new Vector2(r.x+34,r.y+15),new Vector2(r.x+48,r.y+2),color,4); Widgets.DrawLine(new Vector2(r.x+48,r.y+2),new Vector2(r.x+62,r.y+15),color,4); Box(38,32,20,24,new Color(.08f,.1f,.12f)); }
            else if (template == VehicleHudTemplate.Tank) { Box(23,14,8,66,color); Box(65,14,8,66,color); Box(39,31,18,22,cyan); Box(46,3,4,30,cyan); }
            else if (template == VehicleHudTemplate.Special) { Box(23,12,8,68,color); Box(65,12,8,68,color); Box(20,2,56,10,cyan); Box(42,30,12,30,cyan); }
            else { for(int i=0;i<3;i++) { Box(25,18+i*23,7,13,color); Box(64,18+i*23,7,13,color); } Box(38,22,20,12,cyan); }
            // Actual component conditions: one cell per native component, sorted from most damaged.
            for (int i = 0; i < Math.Min(18, parts.Count); i++) Box(2 + i % 9 * 10, 82 + i / 9 * 5, 8, 4, Condition(parts[i].Health));
        }
    }
}
