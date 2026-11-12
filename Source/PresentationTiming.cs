using System;

namespace HSK.FpsBridge
{
    // Real-time clocks: never derive presentation duration from world ticks or the Sandevistan multiplier.
    internal static class DescentTiming
    {
        internal static double Duration(double seconds, bool overview, DescentKind kind, bool profiles)
        {
            seconds = double.IsFinite(seconds) ? Math.Clamp(seconds, .5, 2) : 1.5;
            if (!overview) return Math.Min(.6, seconds);
            double factor = !profiles ? 1 : kind == DescentKind.Vehicle ? 4.0/3 : kind == DescentKind.Turret ? .8 : kind == DescentKind.Mechanoid ? 1.2 : 1;
            return Math.Clamp(seconds * factor, .5, 2);
        }
        // Bounded, endpoint-zero displacement; affects only rendered camera pose.
        internal static float Landing(double progress)
        {
            double t = Math.Clamp((progress - .8) / .2, 0, 1);
            double envelope = Math.Sin(Math.PI * t);
            return (float)(Math.Sin(t * Math.PI * 4) * envelope * envelope);
        }
        internal static double Progress(double now, double began, double seconds) => Math.Clamp((now - began) / Math.Max(.05, seconds), 0, 1);
        internal static float Ease(double progress) { double t = Math.Clamp(progress, 0, 1); return (float)(t*t*t*(t*(t*6-15)+10)); }
    }
    internal enum DescentKind { Pawn, Mechanoid, Turret, Vehicle }
    internal sealed class CombatMusicClock
    {
        internal bool Active { get; private set; }
        internal double Started { get; private set; }
        internal double EndAt { get; private set; }
        private double clipEnd;
        internal void Trigger(double now, double secondsAvailable, double window)
        {
            // A repeat ability cannot restart, extend or shorten the current playback window.
            if (Active && !Expired(now)) return;
            Reset();
            if (!double.IsFinite(now) || !double.IsFinite(secondsAvailable) || !double.IsFinite(window)
                || secondsAvailable <= .05 || window <= 0) return;
            Started = now; clipEnd = now + secondsAvailable; Active = true;
            EndAt = Math.Min(clipEnd, Started + window);
        }
        internal void FadeOut(double now, double duration = .8) { if (Active) EndAt = Math.Min(EndAt, now + Math.Max(0, duration)); }
        internal float Envelope(double now)
        {
            if (!Active || now < Started || now >= EndAt) return 0f;
            return DescentTiming.Ease(Math.Clamp((now-Started)/.4,0,1)) * DescentTiming.Ease(Math.Clamp((EndAt-now)/.8,0,1));
        }
        internal bool Expired(double now) => Active && (now >= EndAt || now < Started);
        internal void Reset() { Active = false; Started = EndAt = clipEnd = 0; }
    }
}
