using System;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class CombatHudCard
    {
        internal readonly record struct Ammo(ThingDef Kind, int Rounds, int Capacity, int? Reserve, float? Reload);
        internal static readonly Color Background = new Color(.055f, .10f, .12f, .97f);
        internal static readonly Color Surface = new Color(.09f, .16f, .19f);
        internal static readonly Color Muted = new Color(.68f, .77f, .80f);
        internal static readonly Color Accent = new Color(.48f, .88f, .90f);
        internal static readonly Color Amber = new Color(1f, .74f, .44f);
        private static readonly Color Track = new Color(.19f, .27f, .30f);
        internal static Color Condition(float fraction) => fraction < .3f ? new Color(1f, .42f, .33f)
            : fraction < .65f ? Amber : new Color(.52f, .84f, .67f);

        internal static void Label(Rect rect, string value, Color tint)
        {
            Color previous = GUI.color;
            bool wrap = Text.WordWrap;
            GUI.color = tint;
            Text.WordWrap = false;
            try { Widgets.Label(rect, (value ?? "").Truncate(rect.width)); }
            finally { GUI.color = previous; Text.WordWrap = wrap; }
        }

        internal static void Icon(Rect rect, Texture2D texture)
        {
            if (texture != null && texture != BaseContent.BadTex) GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit);
        }

        internal static void Bar(Rect rect, float fraction, Color tint)
        {
            Widgets.DrawBoxSolid(rect, Track);
            rect.width *= Mathf.Clamp01(fraction);
            Widgets.DrawBoxSolid(rect, tint);
        }

        internal static void Gauge(Rect rect, string name, float fraction, Color? tint = null)
        {
            Color color = tint ?? Condition(fraction);
            Label(new Rect(rect.x, rect.y, rect.width - 42f, 18f), name, Color.white);
            Label(new Rect(rect.xMax - 40f, rect.y, 40f, 18f), fraction.ToString("P0"), color);
            Bar(new Rect(rect.x, rect.y + 17f, rect.width, 4f), fraction, color);
        }

        internal static void Weapon(Rect row, string name, Texture2D texture, Ammo ammo, bool selected, bool compact)
        {
            Widgets.DrawBoxSolid(row, Surface);
            if (selected) Widgets.DrawBoxSolid(new Rect(row.x, row.y, 2f, row.height), Accent);
            float iconSize = compact ? 24f : 37f;
            Icon(new Rect(row.x + 6f, row.y + 3f, iconSize, iconSize), texture);
            float labelLeft = row.x + (compact ? 6f : 49f);
            float right = row.xMax - (ammo.Capacity > 0 || ammo.Reserve.HasValue ? 40f : 6f);
            float labelTop = row.y + (compact ? 27f : 5f);
            Label(new Rect(labelLeft, labelTop, Math.Max(1f, right - labelLeft), 18f), name, Color.white);
            string kind = ammo.Kind?.LabelCap.ToString();
            string detail = ammo.Reserve.HasValue
                ? (compact && ammo.Kind != null ? "" : kind + "  ") + L.T("备弹 ", "Reserve ") + ammo.Reserve.Value
                : compact ? null : kind;
            if (!detail.NullOrEmpty())
                Label(new Rect(labelLeft, labelTop + 17f, Math.Max(1f, right - labelLeft), 18f), detail, Muted);
            if (ammo.Capacity <= 0 && !ammo.Reserve.HasValue) return;
            int count = ammo.Capacity > 0 ? ammo.Rounds : ammo.Reserve.Value;
            var center = new Vector2(row.xMax - 23f, row.y + (compact ? 21f : row.height * .5f));
            if (ammo.Capacity > 0)
            {
                float fraction = count / (float)ammo.Capacity;
                VehicleReloadHud.DrawRing(center, compact ? 15f : 18f, fraction, fraction < .2f ? Amber : Accent);
            }
            TextAnchor anchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleCenter;
            Label(new Rect(center.x - 20f, center.y - 8f, 40f, 16f), count.ToString(), Color.white);
            Text.Anchor = anchor;
        }

        internal static void Reload(Rect rect, float progress)
        {
            Label(new Rect(rect.x, rect.y, 62f, 18f), L.T("装填", "Reloading"), Amber);
            Bar(new Rect(rect.x + 64f, rect.y + 7f, rect.width - 107f, 4f), progress, Amber);
            Label(new Rect(rect.xMax - 38f, rect.y, 38f, 18f), progress.ToString("P0"), Amber);
        }
    }
}
