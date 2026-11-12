using System;

namespace HSK.FpsBridge
{
    // Recovery belongs to the real weapon verb, not the selected camera or trigger mode.
    // Native VerbTick owns every shot in a burst. This clock only gates the NEXT cast.
    internal sealed class SpecialFireClock
    {
        public bool Casting { get; private set; }
        public int ReadyTick { get; private set; }
        public int RecoveryTicks { get; private set; }
        public int WarmupDebt { get; private set; }
        private int fallbackCooldown;

        public bool Ready(int now, bool bursting) => !Casting && !bursting && now >= ReadyTick;
        public void Begin(int nativeCooldown)
        {
            Casting = true;
            WarmupDebt = 0;
            fallbackCooldown = Math.Max(0, nativeCooldown);
        }
        public void TransferWarmup(int ticks) => WarmupDebt = Add(WarmupDebt, Math.Max(0, ticks));
        public int Complete(int now, int nativeCooldown) => Complete(now,nativeCooldown,1f);
        public int Complete(int now, int nativeCooldown,float factor)
        {
            if (!Casting) return Remaining(now);
            RecoveryTicks = Math.Max(1,(int)Math.Clamp(Math.Round(Add(Math.Max(0, nativeCooldown), WarmupDebt)*(double)Math.Clamp(factor,.001f,1000f)),1d,int.MaxValue));
            ReadyTick = Add(now, RecoveryTicks);
            Casting = false;
            return RecoveryTicks;
        }
        public int CompleteFallback(int now) => Complete(now, fallbackCooldown);
        public int CompleteFallback(int now,float factor) => Complete(now,fallbackCooldown,factor);
        public void Failed() { Casting = false; WarmupDebt = 0; }
        public int Remaining(int now) => (int)Math.Clamp((long)ReadyTick - now, 0L, int.MaxValue);
        public float Progress(int now) => RecoveryTicks <= 0 ? 1f : 1f - Math.Clamp(Remaining(now) / (float)RecoveryTicks, 0f, 1f);
        private static int Add(int a, int b) => (int)Math.Clamp((long)a + b, 0L, int.MaxValue);
    }
}
