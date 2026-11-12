using System;

namespace HSK.FpsBridge
{
    internal enum ChargeImpact { Airborne, Push, Knockdown }
    internal static class MountedRules
    {
        public const float ChargeRatio = 1.25f, MinChargeSpeed = 4.5f, SprintRatio = 1.9f;
        public static float DamageMultiplier(int melee, float speedRatio) => (1f + .1f * Math.Clamp(melee, 0, 20)) * Math.Clamp(speedRatio, 0f, 3f);
        public static ChargeImpact Impact(float riderMass, float victimMass) => victimMass < riderMass * .45f ? ChargeImpact.Airborne
            : victimMass <= riderMass * 1.15f ? ChargeImpact.Push : ChargeImpact.Knockdown;
        public static float Cost(float riderMass, float victimMass) => Math.Clamp(.18f + .3f * victimMass / Math.Max(.1f, riderMass), .2f, .85f);
        public static bool InLane(float along, float side, float length, float halfWidth) => along >= 0f && along <= length && Math.Abs(side) <= halfWidth;
        public static bool InCleave(float along, float side, float radius) => along >= 0f && along * along + side * side <= radius * radius
            && Math.Abs(side) <= along * 1.732051f + .35f;
    }
    // Physics positions can advance only on game ticks while rendering runs
    // faster. Keep the last measured speed over a short game-time window.
    internal sealed class MountedSpeedSampler
    {
        private float seconds, distance;
        public float Speed { get; private set; }
        public void Reset() { seconds = distance = Speed = 0f; }
        public float Step(float dt, float travelled, float teleportDistance)
        {
            if (dt <= 0f) return Speed;
            if (travelled < 0f || travelled > teleportDistance) { Reset(); return 0f; }
            seconds += dt; distance += travelled;
            if (seconds >= .12f) { Speed = distance / seconds; seconds = distance = 0f; }
            return Speed;
        }
    }
    internal sealed class MountedMomentum
    {
        public float Factor { get; private set; } = 1f;
        public void Reset() => Factor = 1f;
        public void Step(float seconds, bool moving, bool sprint, float actualSpeed)
        {
            float target = moving && sprint && actualSpeed > .15f ? MountedRules.SprintRatio : 1f;
            float amount = Math.Clamp(seconds, 0f, .1f) * (target > Factor ? .55f : 1.8f);
            Factor += Math.Clamp(target - Factor, -amount, amount);
        }
        public void Impact(float cost) => Factor = Math.Max(.3f, Factor - Math.Max(0f, cost));
    }
}
