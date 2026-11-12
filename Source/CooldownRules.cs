using System;

namespace HSK.FpsBridge
{
    internal static class CooldownRules
    {
        public const float Default = 1f, Minimum = .1f, Maximum = 5f;

        public static float Normalize(float multiplier) => float.IsFinite(multiplier)
            ? Math.Clamp(multiplier, Minimum, Maximum) : Default;

        public static int Scale(int ticks, float multiplier) =>
            (int)Math.Clamp(Math.Round(ticks * (double)Normalize(multiplier), MidpointRounding.AwayFromZero), 1d, int.MaxValue);

        // Preserve automatic/three-round cadence; single mode also obeys the setting on a burst-capable gun.
        public static int ShotInterval(int ticks, bool burstWeapon, FireMode mode, float multiplier) =>
            burstWeapon && mode != FireMode.Single ? Math.Max(1, ticks) : Scale(ticks, multiplier);

        // 0.8.1: first-person melee attacks per second from the Melee skill: level 5 → 1.5, 10 → 3, 20 → 5 (straight
        // lines between; level 0 → 0.75).
        public static float MeleeRate(int level)
        {
            float l = Math.Clamp(level, 0, 20);
            return l <= 5f ? .75f + .15f * l : l <= 10f ? 1.5f + .3f * (l - 5f) : 3f + .2f * (l - 10f);
        }
        public static int MeleeTicks(int level) => Math.Max(1, (int)Math.Round(60.0 / MeleeRate(level), MidpointRounding.AwayFromZero));

        public static int ForActor(int ticks, float multiplier, object controlled, object caster, bool weaponOrMelee) =>
            controlled != null && ReferenceEquals(controlled, caster) && weaponOrMelee ? Scale(ticks, multiplier) : ticks;
    }
}
