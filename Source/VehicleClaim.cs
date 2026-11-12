using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace HSK.FpsBridge
{
    internal static class VehicleClaim
    {
        internal static void Install(Harmony harmony)
        {
            foreach (var type in GenTypes.AllTypes.Where(VehicleApi.PawnType.IsAssignableFrom))
            {
                var claim = AccessTools.DeclaredMethod(type, nameof(Pawn.ClaimableBy), new[] { typeof(Faction) });
                if (claim is { IsAbstract: false, ContainsGenericParameters: false })
                    harmony.Patch(claim, postfix: new HarmonyMethod(typeof(VehicleClaim), nameof(AfterClaimable)) { priority = Priority.Last });
                var menu = AccessTools.DeclaredMethod(type, nameof(Pawn.GetFloatMenuOptions), new[] { typeof(Pawn) });
                if (menu is { IsAbstract: false, ContainsGenericParameters: false })
                    harmony.Patch(menu, postfix: new HarmonyMethod(typeof(VehicleClaim), nameof(AfterFloatMenu)));
            }
        }

        private static bool CanClaim(Pawn vehicle) => vehicle is { Spawned: true, Dead: false, Faction: null }
            && VehicleApi.IsVehicle(vehicle) && !vehicle.Position.Fogged(vehicle.Map) && !AerialFlight.Airborne(vehicle)
            && !AerialCrew.LandingWithoutPilot(vehicle)
            && !VehicleApi.List(vehicle, "AllPawnsAboard").OfType<Pawn>().Any(pawn => !pawn.Dead);

        internal static bool CanClaimWith(Pawn pawn, Pawn vehicle) => CanClaim(vehicle)
            && pawn is { Spawned: true, Downed: false, InMentalState: false }
            && pawn.Faction == Faction.OfPlayer && pawn.Map == vehicle.Map
            && pawn.RaceProps.ToolUser && !VehicleApi.IsVehicle(pawn);

        private static void AfterClaimable(Pawn __instance, Faction __0, ref AcceptanceReport __result)
        {
            if (__0 != Faction.OfPlayer || !__instance.Spawned) return;
            if (AerialFlight.Airborne(__instance) || AerialCrew.LandingWithoutPilot(__instance)) __result = false;
            else if (__instance.Faction == null) __result = CanClaim(__instance);
        }

        private static IEnumerable<FloatMenuOption> AfterFloatMenu(IEnumerable<FloatMenuOption> options, Pawn __instance, Pawn __0)
        {
            string label = L.T("认领载具", "Claim vehicle");
            bool alreadyOffered = false;
            foreach (var option in options)
            {
                alreadyOffered |= option.Label == label;
                yield return option;
            }
            if (alreadyOffered || !CanClaimWith(__0, __instance) || !__0.CanReserveAndReach(__instance, PathEndMode.Touch, Danger.Deadly)) yield break;
            yield return new FloatMenuOption(label, () =>
            {
                if (CanClaimWith(__0, __instance))
                    __0.jobs.TryTakeOrderedJob(JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed("FPS_ClaimVehicle"), __instance));
            });
        }
    }

    public sealed class JobDriver_ClaimVehicle : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) =>
            VehicleClaim.CanClaimWith(pawn, job.targetA.Pawn) && pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => !VehicleClaim.CanClaimWith(pawn, job.targetA.Pawn));
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            yield return Toils_General.Do(() => job.targetA.Pawn.SetFaction(Faction.OfPlayer));
        }
    }
}

