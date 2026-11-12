using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using HSK.FpsBridge.Interop;
using IsoRim;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // A camera/input session, not a pawn possession. FirstPerson.On and Pawn stay untouched.
    internal static class RemoteTurret
    {
        public static bool On { get; private set; }
        public static Map Map { get; private set; }
        public static IntVec3 Anchor { get; private set; }
        private static Game game;
        private static readonly CapturePolicy capture = new CapturePolicy();
        private static CursorLockMode oldLock;
        private static bool oldVisible, old3D;
        private static int enterFrame;
        private static bool triggerArmed;
        internal static bool Captured => On && capture.Captured && AllowsInput();

        internal static string Refusal(IManualTurret t)
        {
            if (t?.Thing == null || !t.Thing.Spawned || t.Thing.Destroyed || t.Thing.Faction != Faction.OfPlayer
                || t.Thing.Map != Find.CurrentMap || t.Thing.Position.Fogged(t.Thing.Map))
                return L.T("只能接管当前地图上可见的己方炮台。", "Select a visible turret of yours on the current map.");
            if (t.AimMode != TurretAimMode.Direct || !(t is IRemoteManualTurret remote))
                return L.T("此炮台不支持无人远程直射。", "This turret does not support remote direct fire.");
            return remote.RemoteRefusal;
        }

        internal static void Start(IManualTurret t)
        {
            if (On || VehicleControl.On || Bridge.Active || FirstPerson.On || Bindings.Avatar != null) return;
            string why = Refusal(t);
            if (why != null) { Messages.Message(why, MessageTypeDefOf.RejectInput, false); return; }
            oldLock = Cursor.lockState; oldVisible = Cursor.visible; old3D = View3D.Active;
            game = Current.Game; Map = t.Thing.Map; Anchor = t.Thing.Position;
            enterFrame = Time.frameCount;
            triggerArmed = false;
            On = true;
            capture.Enter();
            try
            {
                WeaponView.Release(); AimMotion.Release(); FireControl.CancelInput();
                ControlCamera.Reset();
                Find.Selector.ClearSelection();
                View3D.Active = true;
                Bindings.SetHeading(t.Rotation); Bindings.SetLookYaw(0f); Bindings.SetHeadingReady(true);
                TurretControl.TakeRemote(t);
                Find.CameraDriver.JumpToCurrentMapLoc(t.Thing.DrawPos);
                FirstPersonTransition.Begin(t.Thing);
                Log.Message("[FpsBridge] REMOTE_TURRET_ON thing=" + t.Thing.ThingID);
            }
            catch { End(null); throw; }
        }

        internal static void End(string message)
        {
            FirstPersonTransition.Reset();
            if (!On) return;
            Thing was = TurretControl.Current?.Thing;
            On = false;
            try { TurretControl.Reset(); }
            finally
            {
                WeaponLight.Release();
                capture.Exit();
                Map = null; game = null;
                Cursor.lockState = oldLock; Cursor.visible = oldVisible;
                ControlCamera.Reset();
                View3D.Active = old3D;
                if (was?.Spawned == true && was.Map == Find.CurrentMap)
                    Find.CameraDriver?.JumpToCurrentMapLoc(was.DrawPos);
            }
            if (message != null) Messages.Message(message, MessageTypeDefOf.SilentInput, false);
            Log.Message("[FpsBridge] REMOTE_TURRET_OFF thing=" + was?.ThingID);
        }

        private static bool AllowsInput() => On && Application.isFocused && !LongEventHandler.ShouldWaitForEvent
            && Find.TickManager != null && !WorldRendererUtility.WorldSelected && !View3D.ControlsLocked
            && GUIUtility.keyboardControl == 0 && Find.WindowStack != null && !Find.WindowStack.WindowsPreventCameraMotion
            && !Find.WindowStack.AnyWindowAbsorbingAllInput && !Find.WindowStack.AnySearchWidgetFocused
            && !Bridge.MainTabBlocks && Find.Targeter?.IsTargeting != true;

        internal static void Frame()
        {
            if (!On || FirstPersonTransition.Active) return;
            try
            {
                IManualTurret t = TurretControl.Current;
                if (Current.Game != game || Current.ProgramState != ProgramState.Playing || Find.CurrentMap != Map
                    || !View3D.Active || t?.Thing?.Spawned != true || t.Thing.Destroyed || t.Thing.Faction != Faction.OfPlayer
                    || t.Thing.Map != Map || FirstPerson.On || Bridge.Active)
                { End(L.T("远程接管结束。", "Remote control ended.")); return; }
                if (Time.frameCount <= enterFrame) return; // the gizmo click must never fire a round
                if (Application.isFocused && !LongEventHandler.ShouldWaitForEvent
                    && (ControlKeys.Down(ControlAction.ExitRecall) || ControlKeys.Down(ControlAction.ViewCycle))) { End(null); return; }
                bool allows = AllowsInput();
                capture.Step(allows, ControlKeys.Down(ControlAction.Capture), Input.GetKeyDown(KeyCode.Escape));
                bool input = Captured;
                Cursor.lockState = input ? CursorLockMode.Locked : CursorLockMode.None;
                Cursor.visible = !input;
                if (input && ControlKeys.Down(ControlAction.ManualWork)) { End(null); return; }
                bool paused = Find.TickManager.Paused;
                if (!input || paused) triggerArmed = false;
                else if (!ControlKeys.Held(ControlAction.Fire)) triggerArmed = true;
                if (input && !paused)
                {
                    if (ControlKeys.Down(ControlAction.LeanLeft)) TurretControl.Switch(null, -1);
                    else if (ControlKeys.Down(ControlAction.InteractLeanRight)) TurretControl.Switch(null, 1);
                    if (ControlKeys.Down(ControlAction.FireMode)) (TurretControl.Current as VanillaTurret)?.CycleHeatMode();
                }
                // Let normal pause/speed bindings through; all firing remains disabled during pause.
                TurretOptics.Frame(input && !capture.SkipMouse);
                if (input && !capture.SkipMouse)
                {
                    float dx = Input.GetAxisRaw("Mouse X"), dy = Input.GetAxisRaw("Mouse Y");
                    if (float.IsFinite(dx) && float.IsFinite(dy))
                    {
                        float sens = FpsLook.Scale(TurretOptics.LookScale, TurretOptics.Scoped);
                        FirstPerson.Look(dx * sens, dy * sens * (IsoRimMod.Settings.invertY ? 1f : -1f));
                    }
                }
                TurretControl.ClampLook(null, paused);
                if (!On) return;
                t = TurretControl.Current;
                Find.CameraDriver.JumpToCurrentMapLoc(t.Thing.DrawPos);
                bool fireInput = input && !capture.SkipMouse && triggerArmed;
                if (!fireInput || paused) (t as VanillaTurret)?.CancelTrigger();
                if (!paused) TurretControl.Frame(null, fireInput && ControlKeys.Held(ControlAction.Fire), fireInput && ControlKeys.Down(ControlAction.Fire));
            }
            catch (Exception e) { End(null); Log.ErrorOnce("[FpsBridge] Remote turret stopped: " + e, 19271728); }
        }

        // Only these camera methods consider a remote turret first person. Never spoof the global pawn state.
        internal static bool CameraReady { get; private set; }
        public static bool CameraOn() => On || VehicleControl.On || FirstPerson.On;
        public static IEnumerable<CodeInstruction> CameraMode(IEnumerable<CodeInstruction> instructions)
        {
            int count = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(AccessTools.PropertyGetter(typeof(FirstPerson), nameof(FirstPerson.On))))
                { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(RemoteTurret), nameof(CameraOn)); count++; }
                yield return instruction;
            }
            if (count != 1) throw new InvalidOperationException("Remote turret camera expected one FirstPerson.On call, got " + count);
        }

        public static bool OriginalKeys() => !On && !VehicleControl.On;
        public static bool SelectorGUI() => !Captured && !VehicleControl.Captured;

        public static void DrawGUI()
        {
            if (FirstPersonTransition.Active) return;
            if (!On || Event.current?.type != EventType.Repaint) return;
            try
            {
                TurretControl.DrawHud(null);
                if (!Captured)
                    Widgets.Label(new Rect(24, 104, 620, 28), L.T("远程炮台 · ~ 释放/捕获鼠标 · F / 侧键退出", "Remote turret · ~ release/capture mouse · F / side button to exit"));
            }
            catch (Exception e) { Log.ErrorOnce("[FpsBridge] Remote turret HUD failed: " + e, 19271729); }
        }

        public static void Gizmos(Thing __instance, ref IEnumerable<Gizmo> __result)
        {
            if (!Bootstrap.Ready || !TurretControl.Enabled || __instance?.Faction != Faction.OfPlayer) return;
            IManualTurret t = ManualTurrets.For(__instance);
            if (!(t is IRemoteManualTurret) || t.AimMode != TurretAimMode.Direct) return;
            __result = WithCommand(__result, t);
        }

        private static IEnumerable<Gizmo> WithCommand(IEnumerable<Gizmo> original, IManualTurret t)
        {
            foreach (Gizmo g in original) yield return g;
            var command = new Command_Action {
                defaultLabel = L.T("远程操纵", "Remote control"),
                defaultDesc = L.T("无需小人接管己方直射炮台。右键开镜，Q/E 切换附近炮台，F 或侧键退出。", "Control an own direct-fire turret without a pawn. RMB scope, Q/E switch nearby turrets, F or a side button to exit."),
                icon = t.Thing.def.uiIcon,
                action = () => Start(t)
            };
            string reason = On || Bridge.Active || FirstPerson.On || Bindings.Avatar != null
                ? L.T("请先退出当前控制视角。", "Leave the current control view first.") : Refusal(t);
            if (reason != null) command.Disable(reason);
            yield return command;
        }

        internal static void Install(Harmony harmony)
        {
            foreach (string name in new[] { "get_Pitch", "get_Turning", "TurnCamera", "ApplyCamera" })
                harmony.Patch(AccessTools.Method(typeof(View3D), name) ?? throw new MissingMethodException("View3D." + name),
                    transpiler: new HarmonyMethod(typeof(RemoteTurret), nameof(CameraMode)));
            harmony.Patch(AccessTools.Method(typeof(View3D), "HandleKeys"), prefix: new HarmonyMethod(typeof(RemoteTurret), nameof(OriginalKeys)));
            harmony.Patch(AccessTools.Method(typeof(UIRoot_Play), "UIRootOnGUI"), postfix: new HarmonyMethod(typeof(RemoteTurret), nameof(DrawGUI)));
            harmony.Patch(AccessTools.Method(typeof(Selector), "SelectorOnGUI"), prefix: new HarmonyMethod(typeof(RemoteTurret), nameof(SelectorGUI)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Building_TurretGun), "GetGizmos"), postfix: new HarmonyMethod(typeof(RemoteTurret), nameof(Gizmos)));
            CameraReady = true;
        }
    }
}
