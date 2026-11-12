using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static partial class CeCompat
    {
        [ThreadStatic] private static Thing cePackLaunch;

        private static void InstallCePackFlight()
        {
            var frames = AccessTools.TypeByName("IsoRimCEPackZ.Frames");
            if (frames == null) return;

            var harmony = new Harmony(Bootstrap.Id + ".cepack-flight");
            try
            {
                var normalise = AccessTools.DeclaredMethod(frames, "Normalise", new[] { projectileType });
                var step = AccessTools.DeclaredMethod(frames, "Step", new[] { typeof(Vector3), projectileType });
                if (normalise?.ReturnType != typeof(void) || step?.ReturnType != typeof(Vector3))
                    throw new MissingMethodException("CEPackZ Frames.Normalise / Frames.Step");

                harmony.Patch(normalise, prefix: new HarmonyMethod(typeof(CeCompat), nameof(BeforeCePackNormalise)));
                harmony.Patch(step, prefix: new HarmonyMethod(typeof(CeCompat), nameof(BeforeCePackStep)));
                Log.Message("[FpsBridge] CEPACK_FLIGHT_COMPAT READY; FPS-owned CE projectiles only");
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(harmony.Id);
                Log.Warning("[FpsBridge] CEPack flight compatibility unavailable: " + error);
            }
        }

        private static bool BeforeCePackNormalise(Thing __0) => !ReferenceEquals(cePackLaunch, __0);

        private static bool BeforeCePackStep(Vector3 __0, Thing __1, ref Vector3 __result)
        {
            if (!OwnFlight(__1, out _)) return true;
            // The FPS step already encoded this position in its destination floor's native rows.
            __result = __0;
            return false;
        }
    }
}
