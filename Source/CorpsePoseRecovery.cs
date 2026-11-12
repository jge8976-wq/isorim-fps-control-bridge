using HarmonyLib;
using RimWorld;
using Verse;

namespace HSK.FpsBridge
{
    internal static class CorpsePoseRecovery
    {
        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Pawn),nameof(Pawn.Kill),new[]{typeof(DamageInfo?),typeof(Hediff)}),
                postfix:new HarmonyMethod(typeof(CorpsePoseRecovery),nameof(AfterDeath)){priority=Priority.Last});
        }
        public static void AfterDeath(Pawn __instance)
        {
            if(!__instance.Dead)return;
            // Knockdown jobs and optional riding adapters must not restore a standing pose after death.
            if(__instance.jobs!=null)__instance.jobs.posture=PawnPosture.LayingOnGroundNormal;
            __instance.Drawer?.renderer?.SetAllGraphicsDirty();
            if(__instance.Corpse is { Spawned:true } corpse)corpse.Map.mapDrawer.MapMeshDirty(corpse.Position,MapMeshFlagDefOf.Things);
            BodyRegions.Invalidate(__instance);
            ModelHits.Invalidate(__instance);
        }
    }
}
