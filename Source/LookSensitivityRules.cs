using System;

namespace HSK.FpsBridge
{
    internal static class LookSensitivityRules
    {
        internal const float Minimum = .10f, Maximum = 5f, Default = 1f;
        internal static float Normalize(float value) => float.IsFinite(value) ? Math.Clamp(value, Minimum, Maximum) : Default;
        internal static float Scale(float upstream, float look, float aim, float zoomScale, bool aiming)
        {
            float basis = float.IsFinite(upstream) ? Math.Clamp(upstream, .02f, 2f) : .15f;
            float zoom = float.IsFinite(zoomScale) && zoomScale > 0f ? Math.Min(zoomScale, 1f) : 1f;
            return basis * Normalize(look) * (aiming ? Normalize(aim) : 1f) * zoom;
        }
    }
}
