namespace HSK.FpsBridge
{
    internal static class AerialInputRules
    {
        internal static bool GroundMovement(bool airborne,bool automatic)=>!airborne&&!automatic;
        internal static bool Direction(ControlAction a)=>a==ControlAction.Forward||a==ControlAction.Backward||a==ControlAction.Left||a==ControlAction.Right;
    }
}
