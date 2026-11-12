using System;
using System.Collections.Generic;
using System.Linq;
using IsoRim;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HSK.FpsBridge
{
    public sealed class AircraftCollisionExtension : DefModExtension
    {
        public float returnFraction = .5f;
        public float armoredReturnFraction = .15f;
    }

    internal static class AircraftCollisionDamage
    {
        private static bool PositiveArmor(object upgrade)
        {
            if (upgrade == null || AccessTools.Field(upgrade.GetType(), "stats") == null) return false;
            foreach (object modifier in VehicleApi.List(upgrade, "stats"))
                if (VehicleApi.Get(modifier, "def") is StatDef stat && stat.defName.StartsWith("ArmorRating_", StringComparison.Ordinal)
                    && VehicleApi.Float(modifier, "value") > 0) return true;
            return false;
        }

        internal static bool ExtraArmor(Pawn vehicle)
        {
            foreach (ThingComp component in vehicle.AllComps)
            {
                if (component.GetType().FullName != "Vehicles.CompUpgradeTree") continue;
                object definition = VehicleApi.Get(VehicleApi.Get(component, "Props"), "def");
                foreach (object node in VehicleApi.List(definition, "nodes"))
                {
                    if (!VehicleApi.List(node, "upgrades").Any(PositiveArmor)) continue;
                    if (VehicleApi.Call(component, "NodeUnlocked", node) is bool selected && selected) return true;
                }
            }
            return false;
        }

        internal static float ReturnFraction(Pawn vehicle)
        {
            var policy = vehicle.def.GetModExtension<AircraftCollisionExtension>() ?? new AircraftCollisionExtension();
            return Math.Clamp(ExtraArmor(vehicle) ? policy.armoredReturnFraction : policy.returnFraction, 0, 1);
        }

        internal static void ReturnDamage(Pawn vehicle, Thing target, float damageDealt, float yaw)
        {
            if (damageDealt <= 0 || vehicle.Destroyed || vehicle.Dead || !vehicle.Spawned) return;
            float amount = damageDealt * ReturnFraction(vehicle);
            if (amount > 0) VehicleContacts.Damage(vehicle, target,
                VehicleModels.U(VehicleGeometry.Pose(vehicle).Position), amount);
        }
    }
}
