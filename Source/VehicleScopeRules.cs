using System;

namespace HSK.FpsBridge
{
    // Public values are also the XML names accepted by VehicleWeaponView.scopeTemplate.
    public enum VehicleScopeTemplate { Auto, VintageTank, ModernTank, MachineGun, Rocket, Artillery, Energy }

    internal static class VehicleScopeRules
    {
        internal static VehicleScopeTemplate Select(VehicleScopeTemplate configured, string turretDef, string projectileDef, int techLevel, float range, bool overhead)
        {
            if (configured != VehicleScopeTemplate.Auto && Enum.IsDefined(configured)) return configured;
            string id = ((turretDef ?? "") + " " + (projectileDef ?? "")).ToLowerInvariant();
            // Projectile identity takes priority over vehicle technology: a spacer vehicle can carry an ordinary MG.
            if (Has(id, "rocket", "swarm", "mlrs", "missile")) return VehicleScopeTemplate.Rocket;
            if (Has(id, "artillery", "mortar", "howitzer", "toad_", "udar_") || overhead) return VehicleScopeTemplate.Artillery;
            if (Has(id, "beam", "laser", "railgun", "gauss", "charge", "plasma", "pulse")) return VehicleScopeTemplate.Energy;
            if (Has(id, "machinegun", "machine_gun", "miniturret", "autocannon", "chain", "bunsen", "flame", "smoke", "highwayman", "tango", "badger", "mountedturret", "militaryturret"))
                return VehicleScopeTemplate.MachineGun;
            if (Has(id, "roadkill", "ww1", "ww2", "vintage") || techLevel > 0 && techLevel <= 3) return VehicleScopeTemplate.VintageTank;
            return float.IsFinite(range) && range > 0f && range < 30f ? VehicleScopeTemplate.MachineGun : VehicleScopeTemplate.ModernTank;
        }
        private static bool Has(string text, params string[] terms)
        { foreach (string term in terms) if (text.Contains(term, StringComparison.Ordinal)) return true; return false; }
        internal static ScopeStyle Style(VehicleScopeTemplate template) => template switch {
            VehicleScopeTemplate.VintageTank => ScopeStyle.VintagePost,
            VehicleScopeTemplate.MachineGun => ScopeStyle.ModernCombat,
            VehicleScopeTemplate.Rocket => ScopeStyle.SpacerHolo,
            VehicleScopeTemplate.Artillery => ScopeStyle.VintageFine,
            VehicleScopeTemplate.Energy => ScopeStyle.SpacerPrecision,
            _ => ScopeStyle.ModernPrecision
        };
        internal static float DefaultZoom(VehicleScopeTemplate template) => template switch {
            VehicleScopeTemplate.VintageTank => 2.5f,
            VehicleScopeTemplate.MachineGun => 1.5f,
            VehicleScopeTemplate.Rocket => 2f,
            _ => 3f
        };
        internal static string Label(VehicleScopeTemplate template) => template switch {
            VehicleScopeTemplate.VintageTank => L.T("老式坦克炮镜", "Vintage tank optic"),
            VehicleScopeTemplate.MachineGun => L.T("机枪 / 机炮炮镜", "MG / autocannon optic"),
            VehicleScopeTemplate.Rocket => L.T("火箭发射器炮镜", "Rocket launcher optic"),
            VehicleScopeTemplate.Artillery => L.T("迫击炮 / 榴弹炮炮镜", "Mortar / artillery optic"),
            VehicleScopeTemplate.Energy => L.T("科幻能量炮镜", "Energy weapon optic"),
            _ => L.T("现代坦克炮镜", "Modern tank optic")
        };
    }
}
