
using System;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialAiAim
    {
        internal const float HalfAngle = 30f;

        internal static bool FixedWing(Pawn aircraft) =>
            AerialFlight.Profile(aircraft)?.kind == AerialFlightKind.FixedWing;

        internal static Vector3 Forward(Pawn aircraft) =>
            AerialFlight.TryGet(aircraft, out var flight) ? AerialGeometry.Rotation(flight.Motor) * Vector3.forward : Vector3.forward;

        internal static bool InCone(Pawn aircraft, V3 ray) =>
            !FixedWing(aircraft) || (ray.Length > .001f && Vector3.Dot(Forward(aircraft), VehicleModels.U(ray).normalized) >= .8660254f);

        internal static float Precision(int shooting, int intellectual, float operation, float distance, float range)
        {
            float distanceFactor = 1f - .55f * Mathf.Clamp01(distance / Math.Max(1f, range));
            return Mathf.Clamp01(shooting / 20f) * Mathf.Clamp01(intellectual / 20f)
                * Mathf.Clamp01(operation) * distanceFactor;
        }

        internal static V3 Scatter(Pawn aircraft, V3 direction, float distance, float range)
        {
            if (direction.Length <= .001f) return direction;
            Pawn pilot = VehicleApi.Handlers(aircraft).Where(VehicleApi.Movement)
                .SelectMany(seat => VehicleApi.List(seat, "thingOwner").OfType<Pawn>().Where(pawn => VehicleApi.CanOperate(seat, pawn)))
                .FirstOrDefault();
            int shooting = pilot?.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 0;
            int intellectual = pilot?.skills?.GetSkill(SkillDefOf.Intellectual)?.Level ?? 0;
            float operation = pilot?.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation) ?? 0f;
            operation *= VehicleApi.Efficiency(aircraft);
            float error = (1f - Precision(shooting, intellectual, operation, distance, range)) * 10f;
            float radius = Mathf.Tan(error * Mathf.Deg2Rad) * MathF.Sqrt(Rand.Value);
            float angle = Rand.Value * MathF.PI * 2f;
            Vector3 forward = VehicleModels.U(direction).normalized;
            Vector3 right = Vector3.Cross(Math.Abs(forward.y) > .95f ? Vector3.forward : Vector3.up, forward).normalized;
            Vector3 up = Vector3.Cross(forward, right);
            Vector3 aim = (forward + radius * (MathF.Cos(angle) * right + MathF.Sin(angle) * up)).normalized;
            if (FixedWing(aircraft))
                aim = Vector3.RotateTowards(Forward(aircraft), aim, HalfAngle * Mathf.Deg2Rad, 0f);
            return VehicleModels.V(aim * direction.Length);
        }
    }
}

