using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HSK.FpsBridge
{
    internal static class SurfaceRegions
    {
        internal static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(RCellFinder), nameof(RCellFinder.TryFindDirectFleeDestination),
                new[] { typeof(IntVec3), typeof(float), typeof(Pawn), typeof(IntVec3).MakeByRefType() })
                ?? throw new MissingMethodException("RCellFinder.TryFindDirectFleeDestination"),
                prefix: new HarmonyMethod(typeof(SurfaceRegions), nameof(BeforeFlee)));
            h.Patch(AccessTools.Method(typeof(RCellFinder), nameof(RCellFinder.SpotToStandDuringJob),
                new[] { typeof(Pawn), typeof(Predicate<IntVec3>), typeof(Region) })
                ?? throw new MissingMethodException("RCellFinder.SpotToStandDuringJob"),
                prefix: new HarmonyMethod(typeof(SurfaceRegions), nameof(BeforeStand)));
        }

        // Wall-top control deliberately occupies a wall cell, which has no ground navigation region. Constant AI
        // checks still run there; never pass that missing region to RandomRegionNear or invent a region below us.
        private static bool NoGroundRegion(Pawn pawn) => Surface.For(pawn) && pawn.GetRegion() == null;
        public static bool BeforeFlee(Pawn pawn, ref IntVec3 result, ref bool __result)
        {
            if (!NoGroundRegion(pawn)) return true;
            result = pawn.Position;
            __result = false; // no ground flee path; manual top control remains responsible until back on ground
            return false;
        }
        public static bool BeforeStand(Pawn pawn, Predicate<IntVec3> extraValidator, Region targetRegion, ref IntVec3 __result)
        {
            if (targetRegion != null || !NoGroundRegion(pawn)) return true;
            // A valid surface is already a standing position, but keep the caller's placement restrictions.
            __result = extraValidator == null || extraValidator(pawn.Position) ? pawn.Position : IntVec3.Invalid;
            return false;
        }
    }
}
