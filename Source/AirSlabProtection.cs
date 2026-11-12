using HarmonyLib;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AirSlabProtection
    {
        internal static void Install(Harmony harmony)
        {
            var type = AccessTools.TypeByName("ProjectZ.SlabDamage");
            var hit = AccessTools.Method(type, "HeavyHit", new[] { typeof(IntVec3), typeof(Map) });
            if (hit != null) harmony.Patch(hit, prefix: new HarmonyMethod(typeof(AirSlabProtection), nameof(HeavyHit)));
        }

        private static bool HeavyHit(IntVec3 c, Map map)
            => !FpsMod.Settings.reinforceCrackedPlasteel
                || map.terrainGrid.TopTerrainAt(c).defName != "PZ_UpperFloorDamaged_Plasteel";
    }
}
