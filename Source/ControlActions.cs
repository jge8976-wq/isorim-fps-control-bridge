namespace HSK.FpsBridge
{
    // Stable suffixes of native KeyBindingDefs. Context-sharing actions are a single binding, so E's tap/hold
    // and turret-next behavior cannot drift apart. Existing skill IDs stay compatible with saved KeyPrefs.
    internal enum ControlAction
    {
        ViewCycle, ExitRecall, Capture,
        Forward, Backward, Left, Right,
        LeanLeft, InteractLeanRight, NextWeapon, Inventory,
        Fire, Aim, Sprint, Crouch, Prone, Jump,
        AutoWork, ManualWork, FireMode, Reload, UseItem, MarkTarget, Social, MechAbility,
        Overlay, AbilityPage, Sandevistan, DeathEye, QuickTend, InteractionMenu, AirAscend, AirDescend, AirLand, AirOrbit, VehicleMachineGun, AirParachute
    }
}
