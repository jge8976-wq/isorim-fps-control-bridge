using System;

namespace HSK.FpsBridge
{
    // Engine-independent intent: also exercised by the offline contract executable.
    internal sealed class CapturePolicy
    {
        public bool WantsCapture { get; private set; }
        public bool Captured { get; private set; }
        public bool SkipMouse { get; private set; }

        public void Enter(bool wantsCapture = true) { WantsCapture = wantsCapture; Captured = false; SkipMouse = true; }
        public void Exit() { WantsCapture = false; Captured = false; SkipMouse = true; }

        public void Step(bool contextAllows, bool toggle, bool escape)
        {
            // Esc belongs to the window stack. Closing a tab must not revoke capture intent.
            if (!escape && toggle && contextAllows) WantsCapture = !WantsCapture;
            bool next = WantsCapture && contextAllows && !escape;
            SkipMouse = !Captured || !next;
            Captured = next;
        }
    }

    internal static class CycleIndex
    {
        public static int Next(int current, int count, int direction)
        {
            if (count <= 0) return -1;
            if (current < 0 || current >= count) return direction < 0 ? count - 1 : 0;
            return (current + (direction < 0 ? count - 1 : 1)) % count;
        }
    }

    internal static class MovementIntent
    {
        public static (float x, float z) Rotate(float x, float z, float yaw)
        {
            double r = yaw * Math.PI / 180.0;
            float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
            return (x * c + z * s, z * c - x * s);
        }
    }
}
