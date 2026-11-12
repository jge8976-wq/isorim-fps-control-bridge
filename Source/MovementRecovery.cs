using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static partial class Bridge
    {
        private static readonly MovementFrameGate movementGate = new MovementFrameGate();
        private static FieldInfo cameraLockField;
        private static bool recoveryInstalled, fallbackReported, cameraLockReported;

        internal sealed class MovementRecoveryState
        {
            internal object Avatar;
            internal Map Map;
            internal Vector3? CameraLock;
        }

        internal static bool IsPerspectiveKey(KeyBindingDef key) => key != null
            && ((key.defName?.StartsWith("PS_", StringComparison.Ordinal) ?? false)
                || string.Equals(key.modContentPack?.PackageIdPlayerFacing, "ferny.PerspectiveShift", StringComparison.OrdinalIgnoreCase));

        private static bool RecoveryEligible(object instance) => Ours(instance)
            && ReferenceEquals(Bindings.Avatar, instance) && pawn.Spawned && pawn.Map == Find.CurrentMap
            && !pawn.Dead && !pawn.Downed && !pawn.InMentalState && !WorldComponent_GravshipController.CutsceneInProgress
            && CanDrive() && !VehicleControl.On && !RemoteTurret.On && !TurretControl.Manned
            && !MountedControl.Flying && !QuickTend.For(pawn) && !MechControl.Locked
            && !ProjectZStairs.Riding(pawn) && !Vault.Active;

        internal static void InstallMovementRecovery(Harmony harmony)
        {
            cameraLockField = AccessTools.Field(Bindings.StateType, "CameraLockPosition");
            if (cameraLockField == null || !cameraLockField.IsStatic || cameraLockField.IsInitOnly
                || cameraLockField.FieldType != typeof(Vector3?))
                throw new MissingFieldException("PerspectiveShift.State.CameraLockPosition (Vector3?)");
            harmony.Patch(Bindings.AvatarPhysics,
                prefix: new HarmonyMethod(typeof(Bridge), nameof(BeforeRecoveryPhysics)) { priority = Priority.First + 200 },
                finalizer: new HarmonyMethod(typeof(Bridge), nameof(FinishRecoveryPhysics)) { priority = Priority.Last });
            harmony.Patch(Bindings.AvatarRender,
                prefix: new HarmonyMethod(typeof(Bridge), nameof(BeforeRecoveryRender)) { priority = Priority.First + 200 });
            recoveryInstalled = true;
            Log.Message("[FpsBridge] MOVE_GUARD READY; physics=once-per-frame; fallback=ON; cameraLock=scoped; PS_hotkeys=blocked-in-FPS; renderFallback=ON");
        }

        public static bool BeforeRecoveryPhysics(object __instance, out MovementRecoveryState __state)
        {
            __state = null;
            if (!Ours(__instance)) return true;
            if (!diagInsidePhysicsFallback) diagNativePhysics++;
            // Mark attempts too. A legitimate blocker must not become a request to bypass it via fallback.
            if (!movementGate.Enter(__instance, Time.frameCount)) return false;
            if (!RecoveryEligible(__instance)) return true;
            Vector3? old = (Vector3?)cameraLockField.GetValue(null);
            if (!old.HasValue) return true;
            __state = new MovementRecoveryState { Avatar = __instance, Map = Bindings.PawnOf(__instance)?.Map, CameraLock = old };
            cameraLockField.SetValue(null, null);
            if (!cameraLockReported)
            {
                cameraLockReported = true;
                Log.Message("[FpsBridge] MOVE_GUARD scoped upstream camera lock for FPS physics; previous state restored afterwards");
            }
            return true;
        }

        public static void FinishRecoveryPhysics(MovementRecoveryState __state)
        {
            if (__state == null || cameraLockField.GetValue(null) != null) return;
            // Do not overwrite a new lock, a different avatar or a floor/map transition.
            if (ReferenceEquals(Bindings.Avatar, __state.Avatar)
                && Bindings.PawnOf(__state.Avatar)?.Map == __state.Map)
                cameraLockField.SetValue(null, __state.CameraLock);
        }

        private static void MovementRecoveryFrame()
        {
            if (!recoveryInstalled || !RecoveryEligible(avatar) || movementGate.Seen(avatar, Time.frameCount)) return;
            if (!fallbackReported)
            {
                fallbackReported = true;
                Log.Message("[FpsBridge] MOVE_GUARD upstream physics not observed this frame; dispatching guarded fallback for controlled pawn");
            }
            // Invoke the same patched upstream method. Existing pause, work, floor and other-mod guards still run.
            diagPhysicsFallback++; diagInsidePhysicsFallback = true;
            try { Bindings.AvatarPhysics.Invoke(avatar, null); }
            finally { diagInsidePhysicsFallback = false; }
        }

        private static void ResetMovementRecovery()
        {
            movementGate.Reset(); renderGate.Reset(); fallbackReported = false; cameraLockReported = false;
            renderFallbackReported = false; hudRenderedReported = false;
        }
    }
}
