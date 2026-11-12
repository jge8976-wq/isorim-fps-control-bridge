using System;
using System.Collections.Generic;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AimPicker
    {
        private static int frame = -1;
        private static Pawn owner;
        private static Verb weapon;
        private static LocalTargetInfo cached = LocalTargetInfo.Invalid;
        public static void Reset() { frame = -1; owner = null; weapon = null; }

        public static LocalTargetInfo Target(Pawn pawn, Verb verb)
        {
            if (frame == Time.frameCount && owner == pawn && weapon == verb) return cached;
            frame = Time.frameCount; owner = pawn; weapon = verb;
            cached = Pick(pawn, verb);
            return cached;
        }

        private static LocalTargetInfo Pick(Pawn pawn, Verb verb)
        {
            V3 eye = Ballistics.Eye(pawn), direction = ControlCamera.ShotDirection(pawn);
            float reach = Mathf.Clamp(verb.verbProps.range + 2f, 2f, 120f);
            return Ballistics.PickTarget(pawn, eye, eye + direction * reach);
        }
    }
}
