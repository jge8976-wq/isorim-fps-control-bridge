using System;

namespace HSK.FpsBridge
{
    internal static class VehicleProjectileRules
    {
        // VEF expands a native start-to-current-position line into a damage area after base.TickInterval.
        // Replacing only the base projectile flight leaves that second trajectory running with incompatible endpoints.
        internal static bool NativeAreaFlight(Type type)
        {
            for (Type t = type; t != null; t = t.BaseType)
                if (t.FullName == "VEF.Weapons.ExpandableProjectile" || t.FullName == "VFECore.ExpandableProjectile") return true;
            return false;
        }
    }
}
