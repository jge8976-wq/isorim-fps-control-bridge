using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using IsoRim;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static partial class VehicleControl
    {
        // Use the same decision for input and feedback so a blocked session cannot fail silently.
        internal static string InputBlockReason
        {
            get
            {
                if (!On) return L.T("未控制载具", "No vehicle control session");
                if (!Application.isFocused) return L.T("游戏窗口未获得焦点", "Focus the game window");
                if (LongEventHandler.ShouldWaitForEvent) return L.T("等待游戏加载完成", "Waiting for the game to finish loading");
                if (ControlKeys.Editing) return L.T("请先结束按键设置", "Finish editing key bindings");
                if (Find.TickManager == null) return L.T("等待地图就绪", "Waiting for the map");
                if (WorldRendererUtility.WorldSelected) return L.T("请返回地图", "Return to the local map");
                if (View3D.ControlsLocked) return L.T("镜头暂时锁定", "Camera controls are locked");
                if (GUIUtility.keyboardControl != 0) return L.T("请先退出文字输入框", "Leave the text input field");
                if (Find.WindowStack == null) return L.T("等待界面就绪", "Waiting for the interface");
                if (Find.WindowStack.WindowsPreventCameraMotion || Find.WindowStack.AnyWindowAbsorbingAllInput || Find.WindowStack.AnySearchWidgetFocused)
                    return L.T("请先关闭正在拦截输入的窗口", "Close the window capturing input");
                if (Bridge.MainTabBlocks) return L.T("请先关闭底部菜单", "Close the open main tab");
                if (Find.Targeter?.IsTargeting == true) return L.T("请先取消地图目标选择", "Cancel map targeting first");
                return null;
            }
        }
        internal static string ControlBlockReason => InputBlockReason
            ?? (FirstPersonTransition.Active ? L.T("正在进入载具视角", "Entering vehicle view")
                : Find.TickManager.Paused ? L.T("游戏已暂停，恢复时间后才能转炮塔和开火", "Game paused: resume time to aim and fire")
                : !capture.WantsCapture ? ControlKeys.Label(ControlAction.Capture) + L.T("：恢复鼠标控制", ": capture the mouse")
                : !capture.Captured || capture.SkipMouse ? L.T("正在恢复鼠标控制", "Restoring mouse control")
                : VehicleWeapons.Current == null ? L.T("本载具当前没有已安装的武器", "No weapon installed on this vehicle")
                : FireBlockReason);

        private static long vehicleFrames, vehicleGuiCalls, vehicleHudRepaints, vehicleFirePresses, vehicleFireHeldFrames;
        private static float vehicleNextSample;
        private static bool vehicleDiagnosticsLive;
        private static string vehicleDiagnosticSession;
        private static void BeginVehicleDiagnostics()
        {
            vehicleFrames = vehicleGuiCalls = vehicleHudRepaints = vehicleFirePresses = vehicleFireHeldFrames = 0;
            vehicleDiagnosticSession = Guid.NewGuid().ToString("N"); vehicleDiagnosticsLive = true;
            vehicleNextSample = Time.unscaledTime + 1f; WriteVehicleDiagnostics("enter");
        }
        internal static void VehicleDiagnosticsFrame()
        {
            if (!On || !vehicleDiagnosticsLive) return;
            vehicleFrames++;
            if (ControlKeys.Down(ControlAction.Fire)) vehicleFirePresses++;
            if (ControlKeys.Held(ControlAction.Fire)) vehicleFireHeldFrames++;
            if (Time.unscaledTime < vehicleNextSample) return;
            vehicleNextSample = Time.unscaledTime + 2f; WriteVehicleDiagnostics("sample");
        }
        private static void ObserveVehicleGui(bool rendered)
        {
            if (!On || !vehicleDiagnosticsLive) return;
            if (rendered) vehicleHudRepaints++; else vehicleGuiCalls++;
        }
        private static void EndVehicleDiagnostics()
        {
            if (!vehicleDiagnosticsLive) return;
            WriteVehicleDiagnostics("exit"); vehicleDiagnosticsLive = false;
        }
        private static void WriteVehicleDiagnostics(string phase)
        {
            try
            {
                object Gun(object gun) => new {
                    key = VehicleApi.TurretKey(gun), selected = ReferenceEquals(gun, VehicleWeapons.Current),
                    ammo = VehicleApi.Int(gun, "shellCount"), hasAmmo = VehicleApi.Bool(gun, "HasAmmo"),
                    disabled = VehicleApi.Bool(gun, "TurretDisabled"), componentDisabled = VehicleApi.Bool(gun, "ComponentDisabled"),
                    manned = VehicleApi.Bool(gun, "IsManned"), deployed = VehicleApi.Bool(gun, "DeploymentSatisfied"),
                    restricted = VehicleApi.Bool(gun, "TurretRestricted"), reloadTicks = VehicleApi.Int(gun, "ReloadTicks"),
                    overheated = VehicleApi.Bool(gun, "OnCooldown"), targetable = VehicleApi.Bool(gun, "IsTargetable"),
                    yaw = VehicleApi.Float(gun, "TurretRotation"), pitch = VehicleWeapons.PitchOf(gun)
                };
                var snapshot = new {
                    utc = DateTime.UtcNow, phase, session = vehicleDiagnosticSession,
                    build = typeof(VehicleControl).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
                    vehicle = Vehicle?.ThingID, crew = Crew?.ThingID, role = VehicleApi.RoleKey(Seat),
                    blocker = ControlBlockReason, weaponStatus = VehicleWeapons.StateLabel,
                    uncrewed = Uncrewed, canFire = CanFire, capable = OperatorCapable(true),
                    violentDisabled = Crew?.WorkTagIsDisabled(WorkTags.Violent) == true,
                    paused = Find.TickManager?.Paused, capture.WantsCapture, capture.Captured, capture.SkipMouse,
                    DebugSettings.enableDamage, DebugSettings.enablePlayerDamage,
                    ceShots = CeCompat.FpsLaunched, ceMoves = CeCompat.FpsMoved, ceDraws = CeCompat.FpsDraws, ceContacts = CeCompat.FpsContacts,
                    transition = FirstPersonTransition.Active, triggerArmed, machineTriggerArmed,
                    keyboardControl = GUIUtility.keyboardControl, focused = Application.isFocused,
                    windows = Find.WindowStack?.Windows.Select(w => w.GetType().FullName).ToArray(),
                    camera = CameraLabel, vehicleFrames, vehicleGuiCalls, vehicleHudRepaints, vehicleFirePresses, vehicleFireHeldFrames,
                    guns = VehicleApi.Turrets(Vehicle).Select(Gun).ToArray()
                };
                string folder = Path.Combine(GenFilePaths.SaveDataFolderPath, "Logs"); Directory.CreateDirectory(folder);
                string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(folder, "FpsBridge-vehicle-diagnostics.json"), json);
                if (phase == "exit") File.WriteAllText(Path.Combine(folder, "FpsBridge-vehicle-" + vehicleDiagnosticSession + ".json"), json);
            }
            catch (Exception e) { Log.WarningOnce("[FpsBridge] Vehicle diagnostics: " + e, 19271807); }
        }
    }
}
