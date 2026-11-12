#nullable enable
using System;

namespace HSK.FpsBridge
{
    internal enum ScopeStyle { None, VintagePost, VintageFine, ModernCombat, ModernPrecision, SpacerHolo, SpacerPrecision }

    internal static class ScopeRules
    {
        public static ScopeStyle TurretStyle(bool spacer, float range) => spacer
            ? (range >= 40f ? ScopeStyle.SpacerPrecision : ScopeStyle.SpacerHolo)
            : (range >= 40f ? ScopeStyle.ModernPrecision : ScopeStyle.ModernCombat);
        // Use actual defNames, never translated labels or the approximate hand model.
        public static ScopeStyle Style(string? defName)
        {
            string id = (defName ?? "").ToLowerInvariant();
            if (id == "gun_boltactionrifle_unique") return ScopeStyle.VintageFine;
            if (id.EndsWith("_unique", StringComparison.Ordinal)) id = id.Substring(0, id.Length - 7);
            switch (id)
            {
                case "gun_boltactionrifle": return ScopeStyle.VintagePost;
                case "gun_sniperrifle": return ScopeStyle.ModernPrecision;
                case "gun_assaultrifle": case "gun_lmg": return ScopeStyle.ModernCombat;
                case "gun_chargerifle": case "gun_beamrepeater": return ScopeStyle.SpacerHolo;
                case "gun_chargelance": return ScopeStyle.SpacerPrecision;
            }
            if (Has(id, "grenade", "molotov", "launcher", "shotgun", "pistol", "revolver", "minigun", "turret", "melee", "bow_")) return ScopeStyle.None;
            if (Has(id, "kar98", "mosin", "leeenfield", "lee_enfield", "springfield", "m1903"))
                return Has(id, "sniper", "scoped", "scope") ? ScopeStyle.VintageFine : ScopeStyle.VintagePost;
            if (Has(id, "chargelance", "charge_lance", "railgun", "gauss", "lasersniper", "laser_sniper")) return ScopeStyle.SpacerPrecision;
            if (Has(id, "chargerifle", "charge_rifle", "beamrepeater", "beam_repeater", "pulserifle", "pulse_rifle", "laserrifle", "laser_rifle")) return ScopeStyle.SpacerHolo;
            if (Has(id, "sniper", "scoped", "marksman", "dmr")) return ScopeStyle.ModernPrecision;
            if (Has(id, "boltaction", "bolt_action")) return ScopeStyle.VintagePost;
            if (Has(id, "assaultrifle", "assault_rifle", "gun_lmg", "lightmachinegun")) return ScopeStyle.ModernCombat;
            return ScopeStyle.None;
        }
        private static bool Has(string text, params string[] terms)
        { foreach (string term in terms) if (text.Contains(term, StringComparison.Ordinal)) return true; return false; }
        public static bool Scoped(string? defName) => Style(defName) != ScopeStyle.None;
        public static string? TexturePath(ScopeStyle style) => style == ScopeStyle.None ? null : "HSKFps/Scopes/" + style;
        public static string Label(ScopeStyle style) => style switch {
            ScopeStyle.VintagePost => L.T("二战 · 三柱镜", "WWII · three-post"), ScopeStyle.VintageFine => L.T("二战 · 细十字镜", "WWII · fine crosshair"),
            ScopeStyle.ModernCombat => L.T("现代 · 战斗镜", "Modern · combat"), ScopeStyle.ModernPrecision => L.T("现代 · 精确镜", "Modern · precision"),
            ScopeStyle.SpacerHolo => L.T("科幻 · 全息镜", "Sci-fi · holographic"), ScopeStyle.SpacerPrecision => L.T("科幻 · 精确镜", "Sci-fi · precision"), _ => L.T("机瞄", "Iron sights")
        };
        public static float DefaultZoom(ScopeStyle style) => style switch {
            ScopeStyle.ModernCombat => 1.5f, ScopeStyle.SpacerHolo => 1.5f,
            ScopeStyle.ModernPrecision => 3f, ScopeStyle.SpacerPrecision => 3f, _ => 2.5f
        };
        public static float MaxZoom(ScopeStyle style) => style switch {
            ScopeStyle.ModernCombat => 3f, ScopeStyle.SpacerHolo => 3f,
            ScopeStyle.VintagePost => 4f, ScopeStyle.VintageFine => 4f, _ => 6f
        };
        // Match IsoRim's 15 degree minimum FOV; never display an unattainable magnification.
        public static float Limit(float requested, float baseFov, ScopeStyle style = ScopeStyle.ModernPrecision)
        {
            double fov = Math.Clamp(float.IsFinite(baseFov) ? baseFov : 65f, 15f, 100f);
            float max = Math.Min(MaxZoom(style), (float)(Math.Tan(fov * Math.PI / 360d) / Math.Tan(15d * Math.PI / 360d)));
            return Math.Clamp(float.IsFinite(requested) ? requested : DefaultZoom(style), 1f, Math.Max(1f, max));
        }
        public static float Zoom(float current, float wheel, float baseFov, ScopeStyle style = ScopeStyle.ModernPrecision) =>
            Limit(current + (float.IsFinite(wheel) ? Math.Sign(wheel) * .5f : 0f), baseFov, style);
        // Centre on the optical axis even on portrait and ultrawide screens.
        public static (float x, float y, float size) Viewport(float width, float height)
        {
            width = Math.Max(1f, float.IsFinite(width) ? width : 1f);
            height = Math.Max(1f, float.IsFinite(height) ? height : 1f);
            float size = .92f * Math.Min(width, height);
            return ((width - size) * .5f, (height - size) * .5f, size);
        }
    }
}
