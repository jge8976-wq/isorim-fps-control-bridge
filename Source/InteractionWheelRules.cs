using System;

namespace HSK.FpsBridge
{
    internal enum WheelGestureEvent { None, Tap, Open, Confirm, Cancel }

    // No engine state: a short mark press and a held interaction can never both dispatch.
    internal sealed class WheelGesture
    {
        internal const float HoldSeconds = .30f;
        internal bool Pending { get; private set; }
        internal bool Open { get; private set; }
        private float since;
        public void Reset() { Pending = Open = false; since = 0f; }
        internal WheelGestureEvent Step(float now, bool pressed, bool held, bool allowed, bool cancel, bool immediate = false)
        {
            if (!allowed || cancel || !float.IsFinite(now) || (Pending && now < since))
            { bool active = Pending; Reset(); return active ? WheelGestureEvent.Cancel : WheelGestureEvent.None; }
            if (!Pending && pressed)
            {
                Pending = true; since = now;
                if (immediate) { Open = true; return WheelGestureEvent.Open; }
            }
            if (!Pending) return WheelGestureEvent.None;
            if (!held)
            {
                WheelGestureEvent result = Open ? WheelGestureEvent.Confirm
                    : now - since < HoldSeconds ? WheelGestureEvent.Tap : WheelGestureEvent.Cancel;
                Reset(); return result;
            }
            if (!Open && now - since >= HoldSeconds) { Open = true; return WheelGestureEvent.Open; }
            return WheelGestureEvent.None;
        }
    }

    internal static class InteractionWheelRules
    {
        internal const int PageSize = 8;
        internal static int Pick(float x, float y, int count, float inner, float outer)
        {
            if (count <= 0 || !float.IsFinite(x) || !float.IsFinite(y)) return -1;
            float radius2 = x * x + y * y;
            if (radius2 < inner * inner || radius2 > outer * outer) return -1;
            double angle = Math.Atan2(y, x) + Math.PI / 2 + Math.PI / count;
            angle %= Math.PI * 2;
            if (angle < 0) angle += Math.PI * 2;
            return Math.Min(count - 1, (int)(angle * count / (Math.PI * 2)));
        }
    }
}
