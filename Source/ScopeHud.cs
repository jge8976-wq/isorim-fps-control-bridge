using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Cached RGBA optics; one texture and four exterior rectangles, no extra camera/render target.
    internal static class ScopeHud
    {
        private static readonly Texture2D[] textures = new Texture2D[7];
        private static readonly bool[] tried = new bool[7];

        public static bool Draw()
        {
            ScopeStyle style = WeaponView.CurrentScope;
            if (!WeaponView.ScopedAiming || style == ScopeStyle.None) return false;
            return Draw(style, WeaponView.ScopeMagnification);
        }

        internal static bool Draw(ScopeStyle style, float magnification)
            => DrawOptic(style, magnification, ScopeRules.Label(style), L.T("右键退出 · 滚轮调倍率", "RMB to exit · wheel for magnification"));

        internal static bool DrawOptic(ScopeStyle style, float magnification, string label, string hint)
        {
            if (style == ScopeStyle.None) return false;
            int i = (int)style;
            if (!tried[i])
            {
                tried[i] = true;
                textures[i] = ContentFinder<Texture2D>.Get(ScopeRules.TexturePath(style), false);
                if (textures[i] == null) Log.Warning("[FpsBridge] Scope texture missing: " + style + "; using centre crosshair.");
            }
            Texture2D tex = textures[i];
            if (tex == null) return false;
            var v = ScopeRules.Viewport(UI.screenWidth, UI.screenHeight);
            Rect area = new Rect(v.x, v.y, v.size, v.size);
            Color oldColor = GUI.color; GameFont oldFont = Text.Font; TextAnchor oldAnchor = Text.Anchor;
            try
            {
                GUI.color = Color.white;
                Widgets.DrawBoxSolid(new Rect(0, 0, area.x, UI.screenHeight), Color.black);
                Widgets.DrawBoxSolid(new Rect(area.xMax, 0, UI.screenWidth - area.xMax, UI.screenHeight), Color.black);
                Widgets.DrawBoxSolid(new Rect(area.x, 0, area.width, area.y), Color.black);
                Widgets.DrawBoxSolid(new Rect(area.x, area.yMax, area.width, UI.screenHeight - area.yMax), Color.black);
                GUI.DrawTexture(area, tex);
                if (FpsMod.Settings.showHints)
                {
                    Text.Font = GameFont.Tiny; Text.Anchor = TextAnchor.UpperLeft;
                    GUI.color = new Color(.85f, .92f, .95f, .9f);
                    Widgets.Label(new Rect(24f, 58f, 420f, 22f), label + "  " + magnification.ToString("0.0") + "×");
                    Widgets.Label(new Rect(24f, 80f, 420f, 22f), hint);
                }
            }
            finally { GUI.color = oldColor; Text.Font = oldFont; Text.Anchor = oldAnchor; }
            return true;
        }
    }
}
