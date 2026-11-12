namespace HSK.FpsBridge
{
    // Frame identity, not elapsed wall time: fallback and upstream can arrive in either order.
    internal sealed class MovementFrameGate
    {
        private object owner;
        private int frame = -1;
        internal bool Seen(object instance, int now) => ReferenceEquals(owner, instance) && frame == now;
        internal bool Enter(object instance, int now)
        {
            if (Seen(instance, now)) return false;
            owner = instance; frame = now; return true;
        }
        internal void Reset() { owner = null; frame = -1; }
    }
}
