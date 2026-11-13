using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace HSK.FpsBridge
{
    internal static class VehicleNativeJobs
    {
        private sealed class LastStart { internal int Tick = -1; }
        private static readonly ConditionalWeakTable<Pawn_JobTracker, LastStart> starts = new();

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob)),
                prefix: new HarmonyMethod(typeof(VehicleNativeJobs), nameof(BeforeStartJob)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(VehicleApi.PatherType, "EngageBrakes"),
                prefix: new HarmonyMethod(typeof(VehicleNativeJobs), nameof(BeforeBrakes)));
        }

        private static void BeforeStartJob(Pawn_JobTracker __instance, Pawn ___pawn,
            ref int ___jobsGivenThisTick, ref string ___jobsGivenThisTickTextual)
        {
            if (!VehicleApi.IsVehicle(___pawn) || Find.TickManager is not TickManager manager) return;
            // VF skips JobTrackerTick on undrafted vehicles, but still runs JobTrackerTickInterval.
            // Keep Verse's per-tick guard and its diagnostics on the actual game-tick boundary.
            var last = starts.GetOrCreateValue(__instance);
            if (last.Tick == manager.TicksGame) return;
            last.Tick = manager.TicksGame;
            ___jobsGivenThisTick = 0;
            ___jobsGivenThisTickTextual = "";
        }

        private static bool BeforeBrakes(object __instance)
        {
            // The physical motor reports movement without a VF ground path.
            // VF would report a path failure and end its current job if it braked here.
            if (VehicleApi.Get(__instance, "curPath") != null) return true;
            Pawn vehicle = VehicleApi.Get(__instance, "vehicle") as Pawn;
            return !VehicleDriving.ActiveFor(vehicle) && !AerialFlight.OwnsMotion(vehicle);
        }
    }
}
