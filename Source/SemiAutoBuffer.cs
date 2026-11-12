namespace HSK.FpsBridge
{
    // One press may wait up to 24 game ticks for the current cycle, but a held button never repeats a single shot.
    internal sealed class SemiAutoBuffer
    {
        public const int WindowTicks = 24;
        private int expires = int.MinValue;
        public void Clear() { expires = int.MinValue; }
        public bool Wants(int now, int nextTick, bool pressed)
        {
            if (pressed && nextTick - now <= WindowTicks) expires = now + WindowTicks;
            return now >= nextTick && now <= expires;
        }
    }
}
