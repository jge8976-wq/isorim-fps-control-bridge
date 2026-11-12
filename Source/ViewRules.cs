using System;

namespace HSK.FpsBridge
{
    internal enum ControlView { FirstPerson, Shoulder, Overhead }

    // Pure rules shared with the offline checks. Null means release possession.
    internal static class ViewRules
    {
        public static ControlView? Next(ControlView view) => view == ControlView.FirstPerson ? ControlView.Shoulder
            : view == ControlView.Shoulder ? ControlView.Overhead : (ControlView?)null;
        public static float Safe(float value, float min, float max, float fallback) =>
            float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
        public static float Gravity(float multiplier) => FlightRules.BulletGravity * Safe(multiplier, 0f, 10f, 1f);
        public static float Speed(float original, float multiplier) => Math.Max(.05f, original) * Safe(multiplier, .25f, 4f, 1f);
    }
}
