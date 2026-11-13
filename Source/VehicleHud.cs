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
        private static ThingDef fuelDef;
        private static VehicleHudTemplate template;
        internal static bool Expanded { get; set; }
        private static void Refresh(Pawn vehicle)
        {
            int tick = Find.TickManager.TicksGame;
            if (snapshot == vehicle && tick < next && next - tick <= 12) return;
            snapshot = vehicle; next = tick + 12; parts.Clear();
            object stats = VehicleApi.Get(vehicle, "statHandler");
            float currentHealth = 0f, maximumHealth = 0f;
            foreach (object component in VehicleApi.List(stats, "components"))
            {
                object props = VehicleApi.Get(component, "props");
                currentHealth += VehicleApi.Float(component, "Health");
                maximumHealth += VehicleApi.Float(component, "MaxHealth");
                parts.Add(new Part { Label = VehicleApi.Get(props, "label") as string ?? VehicleApi.Get(props, "key") as string ?? "?", Health = VehicleApi.Float(component, "HealthPercent") });
            }
            health = maximumHealth > 0f ? currentHealth / maximumHealth : 1f;
            // Worst condition first: the lower rows never hide a critical component behind healthy ones.
            parts.Sort((a, b) => a.Health.CompareTo(b.Health));
            object tank = VehicleApi.Get(vehicle, "CompFueledTravel");
            fuel = tank == null ? 0f : VehicleApi.Float(tank, "Fuel"); capacity = tank == null ? 0f : VehicleApi.Float(tank, "FuelCapacity");
            fuelDef = VehicleApi.Get(VehicleApi.Get(tank, "Props"), "fuelType") as ThingDef;
            template = VehicleHudRules.Select(VehicleModels.Profile(vehicle).hudTemplate, vehicle.def.defName, VehicleApi.Get(VehicleApi.Get(vehicle, "VehicleDef"), "type")?.ToString());
        }
        internal static void Draw(Pawn shown = null)
        {
            Pawn vehicle = shown ?? VehicleControl.Vehicle;
            if (vehicle == null) return;
            bool manual = VehicleControl.Owns(vehicle);
            Refresh(vehicle);
            var weapons = manual ? VehicleWeapons.Available() : VehicleApi.Turrets(vehicle).ToList();
            object selected = manual ? VehicleWeapons.Current : null;
            var active = selected ?? weapons.FirstOrDefault();
            var activeAmmo = active == null ? default : Ammo(vehicle, active);
            var reload = activeAmmo.Reload;
            bool details = manual && Expanded;
            float width = Math.Min(370f, UI.screenWidth - 24f);
            if (width < 180f) return;
            float cardsWidth = width - 20f;
            int visibleSlots = Math.Clamp(Math.Min(weapons.Count, (int)(cardsWidth / 114f)), 1, 3);
            int page = Math.Max(0, weapons.IndexOf(selected)) / visibleSlots;
            bool rail = weapons.Count > 1 && visibleSlots > 1;
            bool moreSlots = rail && weapons.Count > visibleSlots;
            string note = manual ? AerialFlight.TryGet(vehicle, out var flight) ? flight.Status : VehicleDriving.Status : null;
            if (manual && note == null && selected != null && reload == null)
            {
                var status = VehicleReloadHud.Status(selected);
                if (status.Filled < 1f || VehicleApi.Bool(selected, "OnCooldown"))
                    note = status.Label;
            }
            float weaponHeight = weapons.Count == 0 ? 0f : rail ? 65f : 46f;
            float height = 62f + (weaponHeight > 0f ? weaponHeight + 5f : 0f) + (moreSlots ? 16f : 0f)
                + (details ? 95f : 0f) + (reload.HasValue ? 22f : 0f) + (manual ? 22f : 0f) + (note != null ? 18f : 0f);
            float y = Math.Max(8f, UI.screenHeight - height - (manual ? 48f : 285f));
            var panel = new Rect(12f, y, width, height);
            GameFont font = Text.Font;
            TextAnchor alignment = Text.Anchor;
            Color tint = GUI.color;
            try
            {
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                Widgets.DrawBoxSolid(panel, CombatHudCard.Background);
                float x = panel.x + 10f;
                y += 6f;
                CombatHudCard.Label(new Rect(x, y, cardsWidth * .61f, 18f), vehicle.LabelShortCap.ToString(), Color.white);
                if (manual)
                    CombatHudCard.Label(new Rect(x + cardsWidth * .62f, y, cardsWidth * .38f, 18f),
                        (AerialFlight.TryGet(vehicle, out var state) && state.Airborne ? state.Motor.Velocity.Length : VehicleDriving.Speed)
                            .ToString("0.0") + L.T(" 格/秒", " cells/s"), CombatHudCard.Muted);
                y += 20f;
                if (weaponHeight > 0f)
                {
                    if (rail)
                    {
                        float slotWidth = (cardsWidth - 5f * (visibleSlots - 1)) / visibleSlots;
                        int start = page * visibleSlots;
                        for (int slot = 0; slot < visibleSlots && start + slot < weapons.Count; slot++)
                        {
                            object gun = weapons[start + slot];
                            Weapon(new Rect(x + slot * (slotWidth + 5f), y, slotWidth, weaponHeight),
                                gun, gun == active ? activeAmmo : Ammo(vehicle, gun), gun == selected, true);
                        }
                    }
                    else Weapon(new Rect(x, y, cardsWidth, weaponHeight), active, activeAmmo, false, false);
                    y += weaponHeight + 5f;
                    if (moreSlots)
                    {
                        CombatHudCard.Label(new Rect(x, y, cardsWidth, 16f),
                            ControlKeys.Label(ControlAction.NextWeapon) + "  ·  " + (weapons.IndexOf(active) + 1) + " / " + weapons.Count, CombatHudCard.Muted);
                        y += 16f;
                    }
                }
                if (reload is float progress)
                {
                    CombatHudCard.Reload(new Rect(x, y, cardsWidth, 18f), progress);
                    y += 22f;
                }
                if (details)
                {
                    Silhouette(new Rect(x, y, 96f, 90f));
                    Gauge(new Rect(x + 104f, y, cardsWidth - 104f, 19f), L.T("乘员效率", "Crew efficiency"), VehicleControl.Efficiency);
                    for (int i = 0; i < Math.Min(3, parts.Count); i++)
                        Gauge(new Rect(x + 104f, y + 20f + i * 23f, cardsWidth - 104f, 20f), parts[i].Label, parts[i].Health);
                    y += 95f;
                }
                if (capacity <= 0f) Gauge(new Rect(x, y, cardsWidth, 21f), L.T("耐久", "Integrity"), health);
                else
                {
                    float half = (cardsWidth - 12f) * .5f;
                    CombatHudCard.Icon(new Rect(x, y + 1f, 15f, 16f), fuelDef?.uiIcon);
                    Gauge(new Rect(x + 18f, y, half - 18f, 21f), L.T("燃料", "Fuel"), fuel / capacity);
                    Gauge(new Rect(x + half + 12f, y, half, 21f), L.T("耐久", "Integrity"), health);
                }
                y += 25f;
                if (note != null)
                {
                    CombatHudCard.Label(new Rect(x, y, cardsWidth, 18f), note, CombatHudCard.Amber);
                    y += 18f;
                }
                if (manual)
                    CombatHudCard.Label(new Rect(x, y, cardsWidth, 18f), ControlKeys.Label(ControlAction.CockpitStatus)
                        + L.T(details ? " 收起状态" : " 展开状态", details ? " hide status" : " show status")
                        + (weapons.Count > 1 && !moreSlots ? "  ·  " + ControlKeys.Label(ControlAction.NextWeapon) + L.T(" 切换", " switch") : ""),
                        CombatHudCard.Muted);
            }
            finally { Text.Font = font; Text.Anchor = alignment; GUI.color = tint; }
        }

        private static void Gauge(Rect rect, string name, float fraction) => CombatHudCard.Gauge(rect, name, fraction);

        private static CombatHudCard.Ammo Ammo(Pawn vehicle, object gun)
        {
            var def = (Def)VehicleApi.Get(gun, "def");
            ThingDef ammo = VehicleApi.Get(gun, "loadedAmmo") as ThingDef ?? VehicleApi.Get(gun, "savedAmmoType") as ThingDef;
            ThingDef projectile = VehicleWeapons.ProjectileFor(gun);
            int capacity = VehicleApi.Int(def, "magazineCapacity");
            int rounds = VehicleApi.Int(gun, "shellCount");
            bool finite = VehicleApi.Get(def, "ammunition") != null;
            int? reserve = ammo == null ? null : vehicle.inventory.innerContainer.TotalStackCountOfDef(ammo);
            int remaining = VehicleApi.Int(gun, "ReloadTicks");
            float? reload = remaining > 0 ? VehicleWeaponClock.Filled(remaining, VehicleApi.Int(gun, "MaxTicks")) : null;
            return new CombatHudCard.Ammo(ammo ?? projectile, rounds, finite ? capacity : 0, reserve, reload);
        }

        private static void Weapon(Rect row, object gun, CombatHudCard.Ammo ammo, bool selected, bool compact)
        {
            var def = (Def)VehicleApi.Get(gun, "def");
            Texture2D icon = VehicleApi.Get(gun, "GizmoIcon") as Texture2D;
            string label = compact && ammo.Kind != null
                ? (ammo.Kind.LabelCap.ToString().Split(' ')[0] is string caliber && caliber.Any(char.IsDigit)
                    ? caliber : ammo.Kind.LabelCap.ToString())
                : def.LabelCap.ToString();
            CombatHudCard.Weapon(row, label, icon == BaseContent.BadTex ? ammo.Kind?.uiIcon : icon, ammo, selected, compact);
        }
        private static void Silhouette(Rect r)
        {
            Color color = CombatHudCard.Condition(health);
            void Box(float x, float y, float w, float h, Color c) => Widgets.DrawBoxSolid(new Rect(r.x + x, r.y + y, w, h), c);
            Box(34, 15, 28, 63, color);
            if (template == VehicleHudTemplate.FixedWing) { Box(5,34,86,13,color); Box(20,70,56,9,color); }
            else if (template == VehicleHudTemplate.Rotorcraft) { Box(4,20,88,4,color); Box(46,0,4,48,color); Box(23,73,50,5,color); }
            else if (template == VehicleHudTemplate.Boat)
            { Widgets.DrawLine(new Vector2(r.x+34,r.y+15),new Vector2(r.x+48,r.y+2),color,4); Widgets.DrawLine(new Vector2(r.x+48,r.y+2),new Vector2(r.x+62,r.y+15),color,4); Box(38,32,20,24,new Color(.08f,.1f,.12f)); }
            else if (template == VehicleHudTemplate.Tank) { Box(23,14,8,66,color); Box(65,14,8,66,color); Box(39,31,18,22,CombatHudCard.Accent); Box(46,3,4,30,CombatHudCard.Accent); }
            else if (template == VehicleHudTemplate.Special) { Box(23,12,8,68,color); Box(65,12,8,68,color); Box(20,2,56,10,CombatHudCard.Accent); Box(42,30,12,30,CombatHudCard.Accent); }
            else { for(int i=0;i<3;i++) { Box(25,18+i*23,7,13,color); Box(64,18+i*23,7,13,color); } Box(38,22,20,12,CombatHudCard.Accent); }
            // Actual component conditions: one cell per native component, sorted from most damaged.
            for (int i = 0; i < Math.Min(18, parts.Count); i++) Box(2 + i % 9 * 10, 82 + i / 9 * 5, 8, 4, CombatHudCard.Condition(parts[i].Health));
        }
    }
}
