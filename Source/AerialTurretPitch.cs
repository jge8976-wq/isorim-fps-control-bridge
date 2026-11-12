using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using HSK.FpsBridge.Interop;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialTurretPitch
    {
        private sealed class ShotPitch { internal int tick; internal float pitch; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Building_Turret, ShotPitch> lastShot = new();
        private static readonly MethodInfo partsFor = AccessTools.Method(typeof(DynamicModels), "PartsFor");
        [ThreadStatic] private static Building_Turret drawing;
        [ThreadStatic] private static TurretTop top;

        internal static void Install(Harmony harmony)
        {
            var draw = AccessTools.Method(typeof(TurretTop), "DrawTurret");
            harmony.Patch(draw,
                prefix: new HarmonyMethod(typeof(AerialTurretPitch), nameof(BeginDraw)) { priority = Priority.First + 200 },
                finalizer: new HarmonyMethod(typeof(AerialTurretPitch), nameof(EndDraw)),
                transpiler: new HarmonyMethod(typeof(AerialTurretPitch), nameof(PatchFlatTop)));
            if (partsFor != null)
                harmony.Patch(AccessTools.Method(typeof(DynamicModels), "Draw"),
                    prefix: new HarmonyMethod(typeof(AerialTurretPitch), nameof(DrawModel)));
        }

        internal static void Record(Thing shooter, V3 velocity)
        {
            if (shooter is not Building_Turret turret || !velocity.Finite) return;
            var pose = lastShot.GetOrCreateValue(turret);
            pose.tick = Find.TickManager.TicksGame;
            pose.pitch = Pitch(velocity);
        }

        private static float Pitch(V3 velocity) => -Mathf.Atan2(velocity.Y, new Vector2(velocity.X, velocity.Z).magnitude) * Mathf.Rad2Deg;

        private static float CurrentPitch(Building_Turret turret)
        {
            if (turret == null || !View3D.Active) return 0;
            if (TurretControl.Current is { AimMode: TurretAimMode.Direct } manual && manual.Thing == turret)
                return FirstPerson.Pitch;
            if (AerialTurretTargeting.TryAim(turret, out var direction)) return Pitch(direction);
            return lastShot.TryGetValue(turret, out var shot) && Find.TickManager.TicksGame - shot.tick < 90 ? shot.pitch : 0f;
        }

        private static void BeginDraw(TurretTop __instance, Building_Turret ___parentTurret, out (Building_Turret turret, TurretTop top) __state)
        {
            __state = (drawing, top);
            drawing = ___parentTurret;
            top = __instance;
        }

        private static void EndDraw((Building_Turret turret, TurretTop top) __state)
        {
            drawing = __state.turret;
            top = __state.top;
        }

        private static IEnumerable<CodeInstruction> PatchFlatTop(IEnumerable<CodeInstruction> instructions)
        {
            var trs = AccessTools.Method(typeof(Matrix4x4), nameof(Matrix4x4.TRS), new[] { typeof(Vector3), typeof(Quaternion), typeof(Vector3) });
            var replacement = AccessTools.Method(typeof(AerialTurretPitch), nameof(FlatTopMatrix));
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(trs)) instruction.operand = replacement;
                yield return instruction;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Matrix4x4 FlatTopMatrix(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            float pitch = CurrentPitch(drawing);
            if (Math.Abs(pitch) > .01f && top != null)
            {
                Vector3 right = Quaternion.Euler(0, top.CurRotation, 0) * Vector3.right;
                rotation = Quaternion.AngleAxis(pitch, right) * rotation;
            }
            return Matrix4x4.TRS(position, rotation, scale);
        }

        private static bool DrawModel(Model __0, Vector3 __1, float __2, Vector3 __3, Color __4, string __5)
        {
            float pitch = CurrentPitch(drawing);
            if (__0 == null || Math.Abs(pitch) < .01f) return true;
            if (partsFor.Invoke(null, new object[] { __0, __4, __5 }) is not List<DynamicModels.Part> parts) return true;
            // IsoRim's turret models point along local +X.
            var local = Matrix4x4.Rotate(Quaternion.AngleAxis(-pitch, Vector3.forward)) * Matrix4x4.Scale(__3);
            DynamicModels.DrawParts(parts, __1, __2, local);
            return false;
        }
    }
}
