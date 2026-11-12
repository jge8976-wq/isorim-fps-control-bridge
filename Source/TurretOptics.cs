using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Separate FOV ownership from the pawn's weapon, including remote sessions with no avatar.
    internal static class TurretOptics
    {
        private static bool scoped, ownsFov;
        private static float baseFov, magnification;
        private static Thing turret;
        private static ScopeStyle style;

        internal static float LookScale => scoped ? Mathf.Clamp(1f / magnification, .1f, 1f) : 1f;
        internal static bool Scoped => scoped;

        internal static void Frame(bool allowInput)
        {
            var current = TurretControl.Current;
            if (current == null || Artillery.On) { Release(); return; }
            if (turret != current.Thing)
            {
                Release();
                turret = current.Thing;
                bool spacer = turret.def.techLevel >= TechLevel.Spacer
                    || turret is Building_TurretGun gun && gun.gun?.def.techLevel >= TechLevel.Spacer;
                style = ScopeRules.TurretStyle(spacer, current.Range);
                magnification = ScopeRules.DefaultZoom(style);
            }
            if (allowInput && ControlKeys.Down(ControlAction.Aim)) scoped = !scoped;
            if (!scoped) { RestoreFov(); return; }
            if (!ownsFov) { baseFov = IsoRimMod.Settings.fpFov; ownsFov = true; }
            magnification = ScopeRules.Zoom(magnification, allowInput ? Input.mouseScrollDelta.y : 0f, baseFov, style);
            IsoRimMod.Settings.fpFov = 2f * Mathf.Atan(Mathf.Tan(baseFov * .5f * Mathf.Deg2Rad) / magnification) * Mathf.Rad2Deg;
        }

        internal static bool Draw() => scoped && ScopeHud.Draw(style, magnification);

        private static void RestoreFov()
        {
            if (ownsFov && IsoRimMod.Settings != null) IsoRimMod.Settings.fpFov = baseFov;
            ownsFov = false;
        }

        internal static void Release()
        {
            RestoreFov();
            scoped = false; turret = null; style = ScopeStyle.None;
        }
    }
}
