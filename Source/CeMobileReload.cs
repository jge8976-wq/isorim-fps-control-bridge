using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace HSK.FpsBridge
{
    internal static class CeMobileReload
    {
        internal static void Install(Harmony harmony) => harmony.Patch(Bindings.ProcessMovement,
            transpiler: new HarmonyMethod(typeof(CeMobileReload), nameof(KeepReloadWhileWalking)) { priority = Priority.Last });

        private static IEnumerable<CodeInstruction> KeepReloadWhileWalking(IEnumerable<CodeInstruction> instructions)
        {
            var end = AccessTools.Method(typeof(Pawn_JobTracker), "EndCurrentJob", new[] { typeof(JobCondition), typeof(bool), typeof(bool) });
            var replacement = AccessTools.Method(typeof(CeMobileReload), nameof(EndMovementJob));
            int count = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(end)) { instruction.opcode = OpCodes.Call; instruction.operand = replacement; count++; }
                yield return instruction;
            }
            if (count != 1) throw new InvalidOperationException("Expected one movement job cancellation; found " + count);
        }

        // Only the ProcessMovement call above is replaced. Death, job failure, manual cancellation and
        // all other Pawn_JobTracker callers keep their original semantics; no CE JobDef is changed globally.
        internal static void EndMovementJob(Pawn_JobTracker jobs, JobCondition condition, bool startNewJob, bool canReturnToPool)
        {
            Pawn pawn = Bridge.ShootingPawn;
            if (Bridge.Active && pawn?.jobs == jobs && Bridge.CeControlsAllowed && CeCompat.MobileReload(pawn))
            { if(CeCompat.MobileReloadProtected<int.MaxValue)CeCompat.MobileReloadProtected++;return; }
            jobs.EndCurrentJob(condition, startNewJob, canReturnToPool);
        }
    }
}
