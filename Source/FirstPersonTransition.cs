using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class FirstPersonTransition
    {
        private sealed class Snapshot
        {
            internal Game game; internal Map map; internal Vector3 eye, focus; internal Quaternion look;
            internal Matrix4x4 projection; internal bool view3D, overview;
        }
        private static Snapshot observed, start;
        private static Thing target;
        private static double began, duration;
        private static float blend;
        private static Vector3 eye;
        private static float yaw, pitch;
        private static DescentKind kind;
        internal static bool Enabled { get; private set; }
        internal static bool Active { get; private set; }
        private static bool Session => Bridge.Active || VehicleControl.On || RemoteTurret.On;
        internal static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(View3D), "ApplyCamera"),
                prefix: new HarmonyMethod(typeof(FirstPersonTransition), nameof(Prepare)) { priority = Priority.Last },
                postfix: new HarmonyMethod(typeof(FirstPersonTransition), nameof(Observe)),
                transpiler: new HarmonyMethod(typeof(FirstPersonTransition), nameof(CameraPose)));
            Enabled = true;
        }
        internal static void Begin(Thing thing)
        {
            if (!Enabled || !FpsMod.Settings.descentAnimation || thing == null || observed == null || observed.game != Current.Game || observed.map != Find.CurrentMap) return;
            start = observed; target = thing; began = Time.realtimeSinceStartup;
            kind = VehicleControl.On ? DescentKind.Vehicle : TurretControl.Manned || RemoteTurret.On ? DescentKind.Turret
                : thing is Pawn pawn && pawn.RaceProps.IsMechanoid ? DescentKind.Mechanoid : DescentKind.Pawn;
            duration = DescentTiming.Duration(FpsMod.Settings.descentSeconds, start.overview, kind, FpsMod.Settings.descentProfiles);
            Active = true; blend = 0; eye = start.eye;
            Cursor.visible=false; Cursor.lockState=CursorLockMode.Locked;
            Angles(start.look, out yaw, out pitch);
            FireControl.CancelInput(); WeaponView.Release();
        }
        internal static void Reset() { Active = false; target = null; start = null; blend = 0; }
        internal static void Forget() { Reset(); observed = null; }
        internal static bool Frame()
        {
            if (!Active) return false;
            if (!Valid()) { Reset(); return false; }
            if (Input.GetKeyDown(KeyCode.Escape) || ControlKeys.Down(ControlAction.ExitRecall))
            { Cancel(); return true; }
            if (ControlKeys.Down(ControlAction.ViewCycle) && Time.realtimeSinceStartup - began > .15) { Reset(); return true; }
            if(Time.realtimeSinceStartup-began>=duration) {Reset();return false;}
            return Active;
        }
        private static bool Valid() => Session && target != null && !target.Destroyed && target.Spawned
            && start != null && start.game == Current.Game && start.map == Find.CurrentMap && target.Map == start.map
            && !(target is Pawn p && (p.Dead || p.Downed));
        private static void Cancel()
        {
            Snapshot restore = start; Reset();
            if (VehicleControl.On) VehicleControl.End();
            else if (RemoteTurret.On) RemoteTurret.End(null);
            else if (FirstPerson.On) FirstPerson.Leave(null);
            if (restore == null || restore.game != Current.Game || restore.map != Find.CurrentMap) return;
            View3D.Active = restore.view3D;
            Find.CameraDriver?.JumpToCurrentMapLoc(restore.focus);
        }
        private static void Prepare()
        {
            if (!Active) return;
            if (!Valid()) { Reset(); return; }
            if (!FpsMod.Settings.descentAnimation) { Reset(); return; }
            double progress = DescentTiming.Progress(Time.realtimeSinceStartup, began, duration);
            if (progress >= 1) { Reset(); return; }
            blend = DescentTiming.Ease(progress);
            Vector3 end = ControlCamera.TargetEye();
            Quaternion endLook = Quaternion.Euler(FirstPerson.Pitch, FirstPerson.Yaw, 0);
            Vector3 forward = endLook * Vector3.forward;
            float distance = (end - start.eye).magnitude;
            float arc = !FpsMod.Settings.descentProfiles ? 1 : kind == DescentKind.Vehicle ? 1.5f : kind == DescentKind.Mechanoid ? 1.2f : kind == DescentKind.Turret ? .8f : 1;
            Vector3 p1 = Vector3.Lerp(start.eye, end, .24f) + Vector3.up * Mathf.Min(4*arc, distance*.08f*arc);
            Vector3 p2 = end + Vector3.up * Mathf.Min(2.5f*arc, distance*.06f*arc) - forward*.45f;
            float u = 1-blend;
            eye = u*u*u*start.eye + 3*u*u*blend*p1 + 3*u*blend*blend*p2 + blend*blend*blend*end;
            // Look toward the moving destination, then smoothly take the controlled pawn's exact facing.
            Vector3 aim = end-eye;
            Quaternion aimLook = aim.sqrMagnitude > .0025f ? Quaternion.LookRotation(aim, Mathf.Abs(aim.normalized.y)>.995f ? Vector3.forward : Vector3.up) : endLook;
            Quaternion dive = Quaternion.Slerp(start.look, aimLook, DescentTiming.Ease(progress*2));
            Quaternion look = Quaternion.Slerp(dive, endLook, DescentTiming.Ease((progress-.35)/.65));
            Angles(look, out yaw, out pitch);
            if (start.overview && FpsMod.Settings.descentLandingShake)
            {
                float landing = DescentTiming.Landing(progress);
                eye += Vector3.up * (landing * .012f);
                pitch += landing * .18f;
            }
        }
        private static void Angles(Quaternion look, out float y, out float p)
        {
            Vector3 d = look*Vector3.forward;
            y = Mathf.Atan2(d.x,d.z)*Mathf.Rad2Deg;
            p = -Mathf.Atan2(d.y,Mathf.Sqrt(d.x*d.x+d.z*d.z))*Mathf.Rad2Deg;
        }
        private static void Observe(Camera main)
        {
            if (Active || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) return;
            observed = new Snapshot { game=Current.Game, map=Find.CurrentMap, eye=View3D.EyePosition,
                focus=main.transform.position, look=Quaternion.LookRotation(View3D.Forward, View3D.Up),
                projection=main.projectionMatrix, view3D=View3D.Active, overview=!Session };
        }
        // In 2D the IsoRim camera is not applied; capture the actual vanilla camera before possession.
        internal static void Observe2D()
        {
            if (Session || Active || View3D.Active || Find.Camera == null || Find.CurrentMap == null) return;
            Camera c=Find.Camera;
            observed=new Snapshot {game=Current.Game,map=Find.CurrentMap,eye=c.transform.position,focus=c.transform.position,
                look=c.transform.rotation,projection=c.projectionMatrix,view3D=false,overview=true};
        }
        internal static Vector3 RenderEye(Vector3 physical) => Active ? eye : physical;
        public static float RenderYaw() => Active ? yaw : View3D.Yaw;
        public static float RenderPitch() => Active ? pitch : View3D.Pitch;
        public static void Projection(Camera camera, Matrix4x4 final)
        {
            if (Active && start != null)
                for(int i=0;i<16;i++) final[i] = Mathf.Lerp(start.projection[i],final[i],blend);
            camera.projectionMatrix=final;
        }
        public static IEnumerable<CodeInstruction> CameraPose(IEnumerable<CodeInstruction> instructions)
        {
            int p=0,y=0,projection=0;
            foreach(var i in instructions)
            {
                if(i.Calls(AccessTools.PropertyGetter(typeof(View3D),nameof(View3D.Pitch)))) {i.opcode=OpCodes.Call;i.operand=AccessTools.Method(typeof(FirstPersonTransition),nameof(RenderPitch));p++;}
                else if(i.Calls(AccessTools.PropertyGetter(typeof(View3D),nameof(View3D.Yaw)))) {i.opcode=OpCodes.Call;i.operand=AccessTools.Method(typeof(FirstPersonTransition),nameof(RenderYaw));y++;}
                else if(i.Calls(AccessTools.PropertySetter(typeof(Camera),nameof(Camera.projectionMatrix)))) {i.opcode=OpCodes.Call;i.operand=AccessTools.Method(typeof(FirstPersonTransition),nameof(Projection));projection++;}
                yield return i;
            }
            if(p!=1 || y<1 || projection!=1) throw new InvalidOperationException("Descent camera signature changed: pitch="+p+" yaw="+y+" projection="+projection);
        }
    }
}
