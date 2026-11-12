using System;

namespace HSK.FpsBridge
{
    // No engine state: exact straight/arc integration, independent of render frame boundaries.
    internal readonly struct VehicleMotorState
    {
        private readonly double x, z, yaw, velocity;
        public float X => (float)x;
        public float Z => (float)z;
        public float Yaw => (float)yaw;
        public float Speed => (float)velocity;
        public VehicleMotorState(double x, double z, double yaw, double speed = 0) { this.x = x; this.z = z; this.yaw = yaw; velocity = speed; }
        public VehicleMotorState Halt() => new VehicleMotorState(x, z, yaw);
        public VehicleMotorState Step(float dt, float maximum, float length, int throttle, int steering)
        {
            if (!float.IsFinite(dt) || dt <= 0f || !float.IsFinite(maximum) || maximum <= 0f) return this;
            dt = Math.Min(dt, .1f); maximum = Math.Clamp(maximum, .1f, 20f);
            throttle = Math.Clamp(throttle, -1, 1); steering = Math.Clamp(steering, -1, 1);
            double target = throttle * maximum * (throttle < 0 ? .65 : 1);
            double acceleration = maximum * (throttle == 0 || velocity * throttle < 0 ? 3.5 : 1.2);
            double difference = target - velocity, sign = Math.Sign(difference);
            double ramp = Math.Min(dt, Math.Abs(difference) / acceleration);
            double distance = velocity * ramp + sign * acceleration * ramp * ramp * .5 + target * (dt - ramp);
            double speed = Math.Abs(difference) <= acceleration * dt ? target : velocity + sign * acceleration * dt;
            double curve = steering / Math.Max(3.0, length * 1.2), angle = yaw * Math.PI / 180.0;
            double end = angle + curve * distance;
            double dx = curve == 0 ? Math.Sin(angle) * distance : (Math.Cos(angle) - Math.Cos(end)) / curve;
            double dz = curve == 0 ? Math.Cos(angle) * distance : (Math.Sin(end) - Math.Sin(angle)) / curve;
            return new VehicleMotorState(x + dx, z + dz, (end * 180 / Math.PI % 360 + 360) % 360, speed);
        }
    }
    internal static class VehicleMotionRules
    {
        public static bool PlantBlocks(bool tree, float growth) => tree && (!float.IsFinite(growth) || growth >= .5f);
        // SAT: true oriented hull footprint against one map cell. Contact alone is not penetration.
        public static bool Covers(float x, float z, float yaw, float width, float length, int cx, int cz)
        {
            double a = yaw * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a), ac = Math.Abs(c), ass = Math.Abs(s);
            double hx = Math.Max(.15, width * .5 - .1), hz = Math.Max(.15, length * .5 - .1);
            double dx = cx + .5 - x, dz = cz + .5 - z;
            return Math.Abs(dx) < hx * ac + hz * ass + .5 - .00001
                && Math.Abs(dz) < hx * ass + hz * ac + .5 - .00001
                && Math.Abs(dx * c - dz * s) < hx + .5 * (ac + ass) - .00001
                && Math.Abs(dx * s + dz * c) < hz + .5 * (ac + ass) - .00001;
        }
    }
}
