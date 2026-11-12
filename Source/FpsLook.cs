using IsoRim;

namespace HSK.FpsBridge
{
    internal static class FpsLook
    {
        internal static float Scale(float zoomScale, bool aiming) => LookSensitivityRules.Scale(
            IsoRimMod.Settings.lookSensitivity, FpsMod.Settings.lookSensitivityMultiplier,
            FpsMod.Settings.aimSensitivityMultiplier, zoomScale, aiming);
    }
}
