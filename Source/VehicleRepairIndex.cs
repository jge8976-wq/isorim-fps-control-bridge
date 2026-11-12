using System;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HSK.FpsBridge
{
    internal static class VehicleRepairIndex
    {
        private static Type listerType;

        internal static void Install(Harmony harmony)
        {
            listerType = AccessTools.TypeByName("Vehicles.ListerVehiclesRepairable")
                ?? throw new TypeLoadException("Vehicles.ListerVehiclesRepairable");
            var parameters = new[] { typeof(Faction), typeof(Pawn) };
            foreach (var type in GenTypes.AllTypes.Where(VehicleApi.PawnType.IsAssignableFrom))
            {
                var setFaction = AccessTools.DeclaredMethod(type, nameof(Pawn.SetFaction), parameters);
                if (setFaction is not { IsAbstract: false, ContainsGenericParameters: false }) continue;
                harmony.Patch(setFaction,
                    prefix: new HarmonyMethod(typeof(VehicleRepairIndex), nameof(BeforeSetFaction)),
                    postfix: new HarmonyMethod(typeof(VehicleRepairIndex), nameof(AfterSetFaction)) { priority = Priority.Last });
            }
        }

        private static void BeforeSetFaction(Pawn __instance, out Faction __state) => __state = __instance.Faction;

        private static void AfterSetFaction(Pawn __instance, Faction __state)
        {
            if (!__instance.Spawned || __instance.Faction == __state
                || __instance.Map.GetComponent(listerType) is not object lister) return;

            // VF records damaged vehicles per faction when they spawn or take damage.
            if (__state != null)
            {
                object former = VehicleApi.Call(lister, "RepairsForFaction", __state);
                VehicleApi.Call(former, "Remove", __instance);
            }
            if (__instance.Faction != null)
                VehicleApi.Call(lister, "NotifyVehicleTookDamage", __instance);
        }
    }
}
