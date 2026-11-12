using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace HSK.FpsBridge
{
    // VVE treats each path destination as a parking place. A manually held throttle is a continuing journey.
    internal static class VehicleVveMovement
    {
        private static MethodInfo speedUp;
        internal static void Install()
        {
            Type comp = AccessTools.TypeByName("VanillaVehiclesExpanded.CompVehicleMovementController");
            if (comp == null) return;
            MethodInfo slow = AccessTools.Method(comp, "Slowdown", new[] { typeof(float), typeof(float), typeof(bool) });
            speedUp = AccessTools.Method(comp, "SpeedUp", new[] { typeof(float) });
            if (slow == null || speedUp == null || AccessTools.PropertyGetter(comp, "StatMoveSpeed") == null)
            { Log.Warning("[FpsBridge] VVE throttle hook unavailable; native movement retained."); return; }
            new Harmony(Bootstrap.Id + ".vve-throttle").Patch(slow,
                prefix: new HarmonyMethod(typeof(VehicleVveMovement), nameof(Slowdown)));
            Log.Message("[FpsBridge] VVE_THROTTLE BOUND; continuous manual drive, native acceleration retained");
        }
        private static bool Slowdown(object __instance, bool stopImmediately)
        {
            if (!VehicleControl.On || !VehicleControl.Captured || !VehicleDriving.Throttle || stopImmediately
                || Find.TickManager.Paused || !(ControlKeys.Held(ControlAction.Forward) || ControlKeys.Held(ControlAction.Backward))
                || !ReferenceEquals(VehicleApi.Get(__instance, "Vehicle"), VehicleControl.Vehicle)
                || !VehicleApi.Bool(VehicleApi.Get(VehicleControl.Vehicle, "vehiclePather"), "Moving")) return true;
            speedUp.Invoke(__instance, new object[] { VehicleApi.Float(__instance, "StatMoveSpeed") });
            return false;
        }
    }
}
