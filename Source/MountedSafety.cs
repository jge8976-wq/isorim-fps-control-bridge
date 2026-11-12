using HarmonyLib;
using Verse;

namespace HSK.FpsBridge
{
    internal static class MountedSafety
    {
        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.TakeDamage), new[] { typeof(DamageInfo) }),
                prefix: new HarmonyMethod(typeof(MountedSafety), nameof(BeforeDamage)) { priority = Priority.First });
        }
        // The ray filters handle ordinary shots/swings; this also covers native
        // projectiles, area damage and special verbs attributed to the rider.
        private static bool BeforeDamage(Thing __instance, DamageInfo dinfo, ref DamageWorker.DamageResult __result)
        {
            if (!MountedControl.OwnMount(dinfo.Instigator, __instance)) return true;
            __result = new DamageWorker.DamageResult();
            return false;
        }
    }
}
