using System;

namespace HSK.FpsBridge
{
    // One clock per physical weapon. Changing seats, fire mode or selected gun cannot reset its cadence.
    internal sealed class VehicleWeaponClock
    {
        private int next, duration;
        public int Remaining(int tick) => Math.Max(0, next - tick);
        public float Progress(int tick) => Filled(Remaining(tick), duration);
        public void Fired(int tick, int shotTicks) { duration = Math.Max(1, shotTicks); next = tick + duration; }
        public bool Ready(int tick, bool reloading, bool overheated, bool disabled, bool hasAmmo)
            => Remaining(tick) == 0 && !reloading && !overheated && !disabled && hasAmmo;
        public static float Filled(float remaining, float total)
            => remaining <= 0f ? 1f : !float.IsFinite(remaining) || !float.IsFinite(total) || total <= 0f ? 0f : Math.Clamp(1f - remaining / total, 0f, 1f);
        public static float Seconds(int ticks, float efficiency)
            => Math.Max(0, ticks) / (60f * Math.Clamp(efficiency, .5f, 1f));
    }
    internal readonly struct VehicleAimCommand
    {
        public readonly float Yaw, Pitch;
        public VehicleAimCommand(float yaw, float pitch) { Yaw = yaw; Pitch = pitch; }
        public VehicleAimCommand Look(float dx, float dy, float minimum, float maximum)
        {
            if (!float.IsFinite(dx) || !float.IsFinite(dy)) return this;
            return new VehicleAimCommand(((Yaw + dx) % 360f + 360f) % 360f, Math.Clamp(Pitch + dy, minimum, maximum));
        }
    }
}
