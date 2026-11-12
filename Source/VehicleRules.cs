using System;
using System.Collections.Generic;

namespace HSK.FpsBridge
{
    internal enum VehicleView { ThirdPerson, Scoped }
    internal readonly struct CrewSlot
    {
        public readonly int Required, Capable;
        public CrewSlot(int required, int capable) { Required = Math.Max(0, required); Capable = Math.Max(0, capable); }
    }
    internal sealed class VehicleTrigger
    {
        public bool Requested { get; private set; }
        public void Clear() => Requested = false;
        public bool Wants(bool input, bool pressed, bool held, bool automatic, int burstRemaining)
        {
            if (!input) { Clear(); return false; }
            if (pressed || held && automatic) Requested = true;
            return Requested || burstRemaining > 0;
        }
        public void BeginBurst() => Requested = false;
    }
    // Pure rules: never create phantom crew, alter a Def, or speed up native simulation.
    internal static class VehicleRules
    {
        public static VehicleView Toggle(VehicleView mode) => mode == VehicleView.ThirdPerson ? VehicleView.Scoped : VehicleView.ThirdPerson;
        public static int Cycle(int index, int count, int step) => count <= 0 ? -1 : index < 0 ? (step < 0 ? count - 1 : 0) : ((index + step) % count + count) % count;
        public static float Efficiency(IEnumerable<CrewSlot> slots)
        { foreach (CrewSlot slot in slots) if (slot.Capable < slot.Required) return .5f; return 1f; }
        // Whole-vehicle possession keeps hull controls in the optic too. The caller
        // still enforces capture, pause, crew capability and the vehicle's CanMoveFinal.
        public static bool CanDrive(VehicleView mode, bool movementSeat) => mode == VehicleView.ThirdPerson || mode == VehicleView.Scoped;
        public static bool CanShoot(VehicleView mode, bool weaponSeat, bool capable) => capable;
        public static int Delay(int nativeTicks, float efficiency) => Math.Max(1, (int)Math.Ceiling(Math.Max(1, nativeTicks) / Math.Clamp(efficiency, .5f, 1f)));
        public static bool Simulation(bool captured, bool paused, bool hasFocus, bool blocked) => captured && !paused && hasFocus && !blocked;
        // Local W/S is hull forward/back; A/D chooses a diagonal while driving, or turns the hull in place.
        public static float Steering(int forward, int sideways) => sideways * (forward == 0 ? 45f : 45f * Math.Sign(forward));
        public static float ArcTurn(float distance, float length, int forward, int side) => forward == 0 ? 0f
            : Math.Clamp(distance, 0f, 2f) / Math.Max(3f, length * 1.2f) * (180f / (float)Math.PI) * Math.Sign(forward) * Math.Sign(side);
        public static float ContactDamage(float footprint, float speed, bool tree) => Math.Clamp(
            Math.Max(1f, footprint) * (tree ? 4f : 2f) * (1f + Math.Clamp(speed, 0f, 6f) * .2f), 4f, 120f);
        public static int DriveHorizon(float length, float speed) => (int)Math.Ceiling(Math.Clamp(Math.Max(length * 2f + 4f, speed * 3f), 12f, 30f));
        public static bool RefreshPath(bool moving, bool sameDirection, int remaining, int horizon) => !moving || !sameDirection || remaining <= horizon / 2;
    }
}
