using System.Collections.Generic;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class LeanControl
    {
        private const float Reach = .45f, Clearance = .1f;
        private static readonly LeanKeys keys = new LeanKeys();
        private static readonly float[] probeHeights = { 1f, .72f, .45f };
        private static Pawn owner;
        private static float amount, footY;
        private static Vector3 offset, slope;
        public static Vector3 EyeOffset => offset;

        public static void Begin(Pawn pawn)
        {
            Reset(); owner = pawn;
            keys.Cancel(ControlKeys.Held(ControlAction.LeanLeft), ControlKeys.Held(ControlAction.InteractLeanRight));
        }

        public static void Reset()
        {
            ClearPose(); owner = null;
            keys.Cancel(false, false);
        }

        public static bool ReadKeys(bool allowed)
        {
            keys.Step(allowed, ControlKeys.Held(ControlAction.LeanLeft), ControlKeys.Held(ControlAction.InteractLeanRight), Time.unscaledTime);
            if (!allowed) ClearPose();
            return keys.TapE;
        }

        public static void Frame(Pawn pawn)
        {
            bool allowed = pawn == owner && Stances.For(pawn) && !TurretControl.Manned && !Vault.Active
                && !ProjectZStairs.Riding(pawn) && !SocialHands.Busy && !Melee.Whirling(pawn)
                && !Stances.State.Airborne && !Stances.State.Falling && !Stances.State.Sliding
                && !Stances.State.Sprint && Stances.State.Posture != Posture.Prone;
            if (!allowed) { ClearPose(); return; }
            Vector3 eye = FirstPerson.Eye - offset;
            footY = FloorSpace.Offset(pawn).y + Surface.HeightOf(pawn);
            float headHeight = Mathf.Max(.3f, eye.y - footY);
            Vector3 right = Quaternion.Euler(0f, FirstPerson.Yaw, 0f) * Vector3.right;
            float next = Mathf.MoveTowards(amount, keys.Direction, Mathf.Min(Time.unscaledDeltaTime, .1f) * 7f);
            Vector3 wanted = right * (next * Reach);
            float fraction = 1f;
            if (wanted.sqrMagnitude > .000001f)
            {
                // Reuse the bridge's floor-aware wall/roof raycast. No map mesh or collision rebuilds.
                foreach (float h in probeHeights)
                {
                    Vector3 start = eye; start.y = footY + headHeight * h;
                    Vector3 delta = wanted * h;
                    Vector3 end = start + delta + delta.normalized * Clearance;
                    V3 a = new V3(start.x, start.y, start.z), b = new V3(end.x, end.y, end.z);
                    if (Ballistics.SightContact(pawn, a, b, out V3 hit, includePawns: false))
                        fraction = Mathf.Min(fraction, Mathf.Clamp01(((hit - a).Length - Clearance) / delta.magnitude));
                }
            }
            amount = next * fraction;
            Vector3 nextOffset = right * (amount * Reach), nextSlope = nextOffset / headHeight;
            if ((nextOffset - offset).sqrMagnitude > 1e-10f || (nextSlope - slope).sqrMagnitude > 1e-10f)
            {
                offset = nextOffset; slope = nextSlope;
                Invalidate();
            }
        }

        private static void ClearPose()
        {
            if (offset.sqrMagnitude > 0f || slope.sqrMagnitude > 0f)
            {
                offset = slope = Vector3.zero; Invalidate();
            }
            amount = 0f;
        }

        private static void Invalidate()
        {
            if (owner == null) return;
            ModelHits.Invalidate(owner); FigureMeshes.Invalidate(owner); BodyRegions.Invalidate(owner);
        }

        public static void Figure(Pawn pawn, float yaw, float size, ref Matrix4x4 pose)
        {
            if (pawn != owner || slope.sqrMagnitude < 1e-10f) return;
            Vector3 local = Quaternion.Euler(0f, -yaw, 0f) * slope;
            float pivot = Surface.HeightOf(pawn) / Mathf.Max(.1f, size);
            Matrix4x4 shear = Matrix4x4.identity;
            shear.m01 = local.x; shear.m21 = local.z;
            shear.m03 = -local.x * pivot; shear.m23 = -local.z * pivot;
            pose = shear * pose;
        }

        public static void Volumes(Pawn pawn, List<HitVolume> into, int start)
        {
            if (pawn != owner || slope.sqrMagnitude < 1e-10f) return;
            V3 s = new V3(slope.x, 0f, slope.z);
            for (int i = start; i < into.Count; i++) into[i] = LeanGeometry.Volume(into[i], s, footY);
        }
    }
}
