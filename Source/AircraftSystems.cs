using System;
using System.Collections;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AircraftSystems
    {
        private static IDictionary Parts(Pawn aircraft) => VehicleApi.Get(
            VehicleApi.Get(aircraft, "statHandler"), "componentsByKeys") as IDictionary;

        private static float Integrity(IDictionary parts, string key) =>
            parts?[key] is object part ? Mathf.Clamp01(VehicleApi.Float(part, "HealthPercent")) : 1f;

        private static float Weakest(IDictionary parts, string a, string b, string c) =>
            Math.Min(Integrity(parts, a), Math.Min(Integrity(parts, b), Integrity(parts, c)));

        internal static (float power, float control, bool critical) Flight(Pawn aircraft)
        {
            var parts = Parts(aircraft);
            float drive = Weakest(parts, "Engine", "Transmission", "RotorMast");
            float controls = Weakest(parts, "Rudder", "Dashboard", "Servos");
            float wings = Math.Min(Integrity(parts, "LeftWing"), Integrity(parts, "RightWing"));
            float tail = Math.Min(Integrity(parts, "LeftHorizontalStabilizer"), Integrity(parts, "RightHorizontalStabilizer"));
            return (Mathf.Lerp(.62f, 1f, drive), Mathf.Lerp(.23f, 1f, Math.Min(controls, Math.Min(wings, tail))),
                drive <= .12f || wings <= .12f);
        }

        internal static bool CanFire(Pawn aircraft, object gun)
        {
            if (aircraft?.stances?.stunner?.Stunned == true) return false;
            if (aircraft == null || !AerialFlight.Supports(aircraft)) return true;
            var parts = Parts(aircraft);
            if (Integrity(parts, "Servos") <= .20f || Integrity(parts, "Dashboard") <= .15f) return false;
            float side = gun == null ? 0f : VehicleModels.Weapon(aircraft, gun).mount.x;
            if (Math.Abs(side) < .1f) return true;
            string prefix = side < 0f ? "Left" : "Right";
            return Math.Min(Integrity(parts, prefix + "Wing"), Integrity(parts, prefix + "SidePanel")) > .25f;
        }

    }
}
