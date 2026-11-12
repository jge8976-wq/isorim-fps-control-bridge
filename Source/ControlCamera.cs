using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class ControlCamera
    {
        private static int aimFrame = -1;
        private static Vector3 aimEye;
        private static float aimYaw, aimPitch;
        private static V3 aimPoint;
        public static ControlView Mode { get; private set; }
        public static bool External => Bridge.ShootingPawn != null && (Mode != ControlView.FirstPerson || TurretControl.Manned);
        public static string Label => Mode == ControlView.FirstPerson ? L.T("第一人称", "First person") : Mode == ControlView.Shoulder ? L.T("肩后跟随", "Over the shoulder") : L.T("俯视控制", "Overhead");
        public static void Reset() { Mode = ControlView.FirstPerson; aimFrame = -1; }
        public static void Set(ControlView mode)
        {
            WeaponView.Release(); AimMotion.Release();
            Mode = mode;
            aimFrame = -1;
            if (mode == ControlView.Overhead) FirstPerson.Look(0f, 55f - FirstPerson.Pitch);
            else if (FirstPerson.Pitch > 40f) FirstPerson.Look(0f, 8f - FirstPerson.Pitch);
            AimPicker.Reset();
            Log.Message("[FpsBridge] VIEW=" + mode);
        }
        public static void ClampLook()
        {
            if (Mode == ControlView.Overhead)
                FirstPerson.Look(0f, Mathf.Clamp(FirstPerson.Pitch, 35f, 75f) - FirstPerson.Pitch);
        }

        // Only the camera pipeline substitutes Eye. FirstPerson.Eye remains the physical head/muzzle contract.
        public static Vector3 RenderEye() => FirstPersonTransition.RenderEye(TargetEye());

        public static Vector3 TargetEye()
        {
            if (VehicleControl.On) return VehicleControl.Eye();
            if (RemoteTurret.On) return TurretControl.Eye();
            Vector3 eye = FirstPerson.Eye;
            Pawn pawn = Bridge.ShootingPawn;
            if (!External || pawn?.Map == null) return eye;
            if (TurretControl.Manned) return TurretControl.Eye();
            Vector3 f = Quaternion.Euler(FirstPerson.Pitch, FirstPerson.Yaw, 0f) * Vector3.forward;
            Vector3 right = Quaternion.Euler(0f, FirstPerson.Yaw, 0f) * Vector3.right;
            if (Mode == ControlView.Overhead)
            {
                // Ahead of the colonist, so the centre ray aims at ground ahead rather than at their feet.
                Vector3 ahead = Quaternion.Euler(0f, FirstPerson.Yaw, 0f) * Vector3.forward;
                return eye + ahead * 7f - f * 16f;
            }
            Vector3 wanted = eye - f * 3.8f + right * .65f + Vector3.up * .35f;
            Vector3 a = eye, b = wanted;
            // Reuse projectile solid/roof geometry; camera never creates a new physics world.
            V3 av = V(a), bv = V(b);
            if (Ballistics.SightContact(pawn, av, bv, out V3 hit, false))
            {
                Vector3 d = b - a;
                float length = d.magnitude;
                float allowed = Mathf.Max(0f, (U(hit) - a).magnitude - .18f);
                wanted = eye + d * (allowed / Mathf.Max(.001f, length));
            }
            Vector3 local = wanted;
            local.x = Mathf.Clamp(local.x, .1f, pawn.Map.Size.x - .1f);
            local.z = Mathf.Clamp(local.z, .1f, FloorSpace.Bands(pawn.Map)[0].Rows - .1f);
            local.y = Mathf.Max(.15f, local.y);
            return local;
        }

        public static V3 AimPoint(Pawn pawn)
        {
            Vector3 eye = RenderEye();
            if (aimFrame == Time.frameCount && aimEye == eye && aimYaw == FirstPerson.Yaw && aimPitch == FirstPerson.Pitch) return aimPoint;
            aimFrame = Time.frameCount; aimEye = eye; aimYaw = FirstPerson.Yaw; aimPitch = FirstPerson.Pitch;
            V3 a = V(eye);
            V3 dir = HitMath.Direction(FirstPerson.Yaw, FirstPerson.Pitch);
            V3 b = a + dir * 120f;
            aimPoint = Ballistics.SightContact(pawn, a, b, out V3 hit, true) ? hit : b;
            return aimPoint;
        }
        public static V3 ShotDirection(Pawn pawn)
        {
            V3 fallback = HitMath.Direction(FirstPerson.Yaw, FirstPerson.Pitch);
            if (!External) return fallback;
            V3 d = AimPoint(pawn) - Ballistics.Eye(pawn);
            // Degenerate or behind the muzzle: retain forward direction; collision still stops the shot.
            return d.Length > .05f ? d * (1f / d.Length) : fallback;
        }
        public static float AttackYaw(Pawn pawn)
        { V3 d = ShotDirection(pawn); return Mathf.Atan2(d.X, d.Z) * Mathf.Rad2Deg; }
        public static float AttackPitch(Pawn pawn)
        { V3 d = ShotDirection(pawn); return -Mathf.Atan2(d.Y, Mathf.Sqrt(d.X * d.X + d.Z * d.Z)) * Mathf.Rad2Deg; }
        private static V3 V(Vector3 v) => new V3(v.x, v.y, v.z);
        private static Vector3 U(V3 v) => new Vector3(v.X, v.Y, v.Z);

        public static Pawn HiddenPawn() => MountedControl.For(FirstPerson.Pawn) ? null : FirstPersonTransition.Active && (RenderEye()-TargetEye()).sqrMagnitude>.5f ? null : External
            && (Mode == ControlView.Overhead || (RenderEye() - FirstPerson.Eye).sqrMagnitude > .56f) ? null : FirstPerson.Pawn;
        public static IEnumerable<CodeInstruction> CameraEye(IEnumerable<CodeInstruction> code) => Replace(code, "Eye", nameof(RenderEye));
        private static readonly Func<Pawn> viewingPawn = AccessTools.PropertyGetter(typeof(FirstPerson), "ViewingPawn")?.CreateDelegate<Func<Pawn>>();
        public static Pawn HiddenViewingPawn() => Bridge.ShootingPawn == null ? viewingPawn?.Invoke() : HiddenPawn();
        public static IEnumerable<CodeInstruction> VisibleBody(IEnumerable<CodeInstruction> code)
        {
            int count = 0;
            var oldGetter = AccessTools.PropertyGetter(typeof(FirstPerson), "Pawn");
            var newGetter = AccessTools.PropertyGetter(typeof(FirstPerson), "ViewingPawn");
            foreach (var instruction in code)
            {
                bool modern = newGetter != null && instruction.Calls(newGetter);
                if (modern || instruction.Calls(oldGetter))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(ControlCamera), modern ? nameof(HiddenViewingPawn) : nameof(HiddenPawn));
                    count++;
                }
                yield return instruction;
            }
            if (count != 1) throw new InvalidOperationException("Expected one FirstPerson Pawn/ViewingPawn visibility call, got " + count);
        }
        private static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> code, string property, string replacement)
        {
            int count = 0;
            foreach (var i in code)
            {
                if (i.Calls(AccessTools.PropertyGetter(typeof(FirstPerson), property)))
                { i.opcode = OpCodes.Call; i.operand = AccessTools.Method(typeof(ControlCamera), replacement); count++; }
                yield return i;
            }
            if (count != 1) throw new InvalidOperationException("Expected one FirstPerson." + property + " call, got " + count);
        }
        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(View3D), "ApplyCamera"), transpiler: new HarmonyMethod(typeof(ControlCamera), nameof(CameraEye)));
            foreach (string name in new[] { "RenderPawnAt_Prefix", "DrawPawnLabel_Prefix" })
                harmony.Patch(AccessTools.Method(typeof(IsoRim.Patches), name), transpiler: new HarmonyMethod(typeof(ControlCamera), nameof(VisibleBody)));
        }
    }
}
