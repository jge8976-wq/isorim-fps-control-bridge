using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialBeamMotes
    {
        private static readonly ConditionalWeakTable<MoteDualAttached, Pawn> targets = new();
        [System.ThreadStatic] private static Pawn launchedAt;
        private static FieldInfo raised, end, endHeight, stretch;
        private static MethodInfo tryGet;

        internal static void Install(Harmony harmony)
        {
            var type = AccessTools.TypeByName("IsoRim.FloorSprays");
            raised = AccessTools.Field(type, "raised");
            var pose = raised?.FieldType.GetGenericArguments()[1];
            var attach = AccessTools.Method(type, "Dual_Postfix");
            if (pose == null || attach == null) return;
            tryGet = AccessTools.Method(raised.FieldType, "TryGetValue");
            end = AccessTools.Field(pose, "b");
            endHeight = AccessTools.Field(pose, "hb");
            stretch = AccessTools.Field(pose, "stretch");
            if (tryGet == null || end == null || endHeight == null || stretch == null) return;

            harmony.Patch(AccessTools.DeclaredMethod(typeof(Beam), nameof(Beam.Launch)),
                prefix: new HarmonyMethod(typeof(AerialBeamMotes), nameof(Begin)),
                finalizer: new HarmonyMethod(typeof(AerialBeamMotes), nameof(End)));
            harmony.Patch(AccessTools.Method(typeof(MoteMaker), "MakeInteractionOverlay",
                new[] { typeof(ThingDef), typeof(TargetInfo), typeof(TargetInfo), typeof(Vector3), typeof(Vector3) }),
                postfix: new HarmonyMethod(typeof(AerialBeamMotes), nameof(Remember)));
            harmony.Patch(attach, postfix: new HarmonyMethod(typeof(AerialBeamMotes), nameof(Place)));
        }

        private static void Begin(LocalTargetInfo usedTarget, out Pawn __state)
        {
            __state = launchedAt;
            launchedAt = AerialCombat.Target(usedTarget, out var aircraft) ? aircraft : null;
        }

        private static void End(Pawn __state) => launchedAt = __state;

        private static void Remember(TargetInfo __2, MoteDualAttached __result)
        {
            if (launchedAt != null && __2.Thing == launchedAt && __result != null)
                targets.AddOrUpdate(__result, launchedAt);
        }

        private static void Place(MoteDualAttached __0)
        {
            if (!targets.TryGetValue(__0, out var aircraft) || !AerialFlight.Airborne(aircraft)
                || aircraft.Map != __0.Map) return;
            object[] args = { __0, null };
            if (!(bool)tryGet.Invoke(raised.GetValue(null), args)) return;
            Vector3 point = AerialTargetLock.Point(aircraft);
            end.SetValue(args[1], new Vector3(point.x, 0, point.z));
            endHeight.SetValue(args[1], point.y);
            stretch.SetValue(args[1], 1f);
        }
    }
}
