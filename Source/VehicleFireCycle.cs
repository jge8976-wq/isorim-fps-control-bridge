using System;

namespace HSK.FpsBridge
{
    internal enum VehicleManualFireMode { Single, Triple, Automatic }

    // Input belongs to a single physical gun. Reloading discards every outstanding shot request.
    internal sealed class VehicleFireCycle
    {
        public VehicleManualFireMode Mode { get; private set; }
        public int Remaining { get; private set; }
        public bool FreshPressRequired { get; private set; } = true;
        private bool requested;
        public VehicleFireCycle(bool automatic) { Mode = automatic ? VehicleManualFireMode.Automatic : VehicleManualFireMode.Single; }
        public void Interrupt() { requested = false; Remaining = 0; FreshPressRequired = true; }
        public void NextMode() { Mode = (VehicleManualFireMode)(((int)Mode + 1) % 3); Interrupt(); }
        public bool Poll(bool enabled, bool pressed, bool held, bool reloading, bool hasAmmo)
        {
            if (reloading || !hasAmmo || !enabled) { Interrupt(); return false; }
            // A release must be observed after the reload, even for automatic fire.
            if (FreshPressRequired) { if (!held && !pressed) FreshPressRequired = false; return false; }
            if (pressed || held && Mode == VehicleManualFireMode.Automatic) requested = true;
            return requested || Remaining > 0;
        }
        public void BeginBurst(int nativeShots, bool selectable)
        {
            requested = false;
            Remaining = selectable ? Mode == VehicleManualFireMode.Triple ? 3 : 1 : Math.Max(1, nativeShots);
        }
        public void Fired() { Remaining = Math.Max(0, Remaining - 1); }
        public static bool Selectable(string name, int magazine, int nativeShotTicks, bool nativeAutomatic)
        {
            name = (name ?? "").ToLowerInvariant();
            if (name.Contains("rocket") || name.Contains("missile") || name.Contains("swarm") || name.Contains("mortar") || name.Contains("howitzer") || name.Contains("smoke") || name.Contains("flame")) return false;
            bool gun = name.Contains("machine") || name.Contains("autocannon") || name.Contains("mountedturret") || name.Contains("militaryturret")
                || name.Contains("miniturret") || name.Contains("chain") || name.Contains("minigun") || name.Contains("mg") || name.Contains("机枪") || name.Contains("机炮");
            return gun || magazine > 1 && nativeShotTicks <= 30;
        }
    }

    public enum VehicleHudTemplate { Auto, Wheeled, Tank, Special, FixedWing, Rotorcraft, Boat }
    internal static class VehicleHudRules
    {
        public static VehicleHudTemplate Select(VehicleHudTemplate configured, string name, string nativeType)
        {
            if (configured != VehicleHudTemplate.Auto) return configured;
            name = (name ?? "").ToLowerInvariant(); nativeType = (nativeType ?? "").ToLowerInvariant();
            if (nativeType.Contains("sea") || name.Contains("dinghy") || name.Contains("trawler")) return VehicleHudTemplate.Boat;
            if (name.Contains("mosquito") || name.Contains("frog") || name.Contains("cherokee") || name.Contains("goliath") || name.Contains("toad") || name.Contains("rotor") || name.Contains("helicopter")) return VehicleHudTemplate.Rotorcraft;
            if (nativeType.Contains("air") || name.Contains("smuggler") || name.Contains("albatross")) return VehicleHudTemplate.FixedWing;
            if (name.Contains("tank") || name.Contains("bulldog") || name.Contains("marshal") || name.Contains("paladin") || name.Contains("badger")
                || name.Contains("bunsen") || name.Contains("goliath") || name.Contains("tango") || name.Contains("toad") || name.Contains("udar")) return VehicleHudTemplate.Tank;
            if (name.Contains("scytheman") || name.Contains("roadkill") || name.Contains("burya") || name.Contains("prowler") || name.Contains("anti") || name.Contains("defoliator")) return VehicleHudTemplate.Special;
            return VehicleHudTemplate.Wheeled;
        }
    }
}
