using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using IsoRim;
using Verse;

namespace HSK.FpsBridge
{
    internal static class FirstPersonEventDelivery
    {
        internal static void Install(Harmony harmony)
        {
            var receive = AccessTools.Method(typeof(LetterStack), nameof(LetterStack.ReceiveLetter),
                new[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) });
            harmony.Patch(receive, transpiler: new HarmonyMethod(typeof(FirstPersonEventDelivery), nameof(KeepLetterRunning)));
            harmony.Patch(AccessTools.Method(typeof(LetterStack), nameof(LetterStack.OpenAutomaticLetters)),
                prefix: new HarmonyMethod(typeof(FirstPersonEventDelivery), nameof(AllowAutomaticLetters)));
        }

        private static bool InFirstPerson => Current.ProgramState == ProgramState.Playing
            && (FirstPerson.On && FirstPerson.Pawn is { Dead: false, Downed: false }
                || VehicleControl.DriverCamera || VehicleControl.Scoped || RemoteTurret.On);

        public static bool AllowAutomaticLetters() => !InFirstPerson;

        public static void PauseForLetter(TickManager tickManager)
        {
            if (!InFirstPerson) tickManager.Pause();
        }

        public static IEnumerable<CodeInstruction> KeepLetterRunning(IEnumerable<CodeInstruction> instructions)
        {
            var pause = AccessTools.Method(typeof(TickManager), nameof(TickManager.Pause));
            var deliveryPause = AccessTools.Method(typeof(FirstPersonEventDelivery), nameof(PauseForLetter));
            int replaced = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(pause))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = deliveryPause;
                    replaced++;
                }
                yield return instruction;
            }
            if (replaced != 1)
                throw new InvalidOperationException($"Expected one automatic pause in letter delivery, found {replaced}.");
        }
    }
}
