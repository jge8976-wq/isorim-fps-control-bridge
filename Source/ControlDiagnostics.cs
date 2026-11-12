using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using HarmonyLib;
using IsoRim;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static partial class Bridge
    {
        private static long diagFrames, diagGuiCalls, diagGuiRepaints, diagHudRepaints, diagPsUpdates,
            diagPsGuiAttempts, diagPsGuiSuppressed, diagNativePhysics, diagPhysicsFallback,
            diagNativeRender, diagRenderFallback, diagMouseConsumed;
        private static bool diagInsidePhysicsFallback, diagInsideRenderFallback, diagLive;
        private static float diagNext;
        private static string diagSession;
        private static object diagEnvironment;

        private static void BeginControlDiagnostics()
        {
            diagFrames = diagGuiCalls = diagGuiRepaints = diagHudRepaints = diagPsUpdates = 0;
            diagPsGuiAttempts = diagPsGuiSuppressed = diagNativePhysics = diagPhysicsFallback = 0;
            diagNativeRender = diagRenderFallback = diagMouseConsumed = 0;
            diagInsidePhysicsFallback = diagInsideRenderFallback = false;
            diagLive = true; diagSession = Guid.NewGuid().ToString("N"); diagNext = Time.unscaledTime + 5f;
            WriteControlDiagnostics("enter");
        }

        private static void EndControlDiagnostics(string reason)
        {
            if (!diagLive) return;
            WriteControlDiagnostics("exit: " + reason); diagLive = false;
        }

        private static void ControlDiagnosticsFrame()
        {
            if (!diagLive || !Active) return;
            diagFrames++;
            if (Time.unscaledTime < diagNext) return;
            diagNext = Time.unscaledTime + 5f; WriteControlDiagnostics("sample");
        }

        private static string ControlBlocker()
        {
            if (!Active) return "no-session";
            if (!Application.isFocused) return "unfocused";
            if (LongEventHandler.ShouldWaitForEvent) return "long-event";
            if (WorldRendererUtility.WorldSelected) return "world-map";
            if (View3D.ControlsLocked) return "3d-controls-locked";
            if (GUIUtility.keyboardControl != 0) return "keyboard-focus";
            if (Find.WindowStack == null) return "no-window-stack";
            if (Find.WindowStack.WindowsPreventCameraMotion || Find.WindowStack.AnyWindowAbsorbingAllInput || Find.WindowStack.AnySearchWidgetFocused) return "window-input";
            if (MainTabBlocks || InventoryPanel.Blocking) return "panel";
            if (Find.Targeter?.IsTargeting == true) return "targeter";
            if (Find.TickManager?.Paused != false) return "paused";
            if (!policy.WantsCapture) return "pointer-released";
            if (!policy.Captured) return "capture-inactive";
            if (FirstPersonTransition.Active) return "view-transition";
            if (InteractionWheel.Blocking) return "interaction-wheel";
            if (WorkControl.Auto) return "auto-work";
            if (QuickTend.For(pawn)) return "tending";
            if (TurretControl.Manned) return "manned-turret";
            if (MechControl.Locked) return "mech-lock";
            if (ProjectZStairs.Riding(pawn)) return "stairs-elevator";
            if (Vault.Active) return "vault";
            if (MountInteraction.Mounting(pawn)) return "mounting";
            return "none";
        }

        private static object PatchState(MethodBase method)
        {
            if (method == null) return null;
            var info = Harmony.GetPatchInfo(method);
            object Row(Patch p) => new { p.owner, p.priority, method = p.PatchMethod.DeclaringType?.FullName + "." + p.PatchMethod.Name,
                assembly = p.PatchMethod.DeclaringType?.Assembly.FullName,
                loadContext = p.PatchMethod.DeclaringType == null ? null : AssemblyLoadContext.GetLoadContext(p.PatchMethod.DeclaringType.Assembly)?.Name };
            return new { method = method.DeclaringType.FullName + "." + method.Name,
                targetLoadContext = AssemblyLoadContext.GetLoadContext(method.DeclaringType.Assembly)?.Name,
                prefixes = info?.Prefixes.Select(Row).ToArray(), postfixes = info?.Postfixes.Select(Row).ToArray(),
                finalizers = info?.Finalizers.Select(Row).ToArray() };
        }

        private static object DiagnosticEnvironment()
        {
            if (diagEnvironment != null) return diagEnvironment;
            string[] names = { "HSK.FpsBridge", "PerspectiveShift", "IsoRim", "Assembly-CSharp", "UnityStubs", "UnityEngine.CoreModule", "UnityEngine.InputLegacyModule", "0Harmony" };
            var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && names.Contains(a.GetName().Name)).ToArray();
            diagEnvironment = new {
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                modules = assemblies.Select(a => new { name = a.FullName, path = a.Location,
                    mvid = a.ManifestModule.ModuleVersionId, loadContext = AssemblyLoadContext.GetLoadContext(a)?.Name,
                    sha256 = File.Exists(a.Location) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) : null }).ToArray(),
                duplicateModuleNames = assemblies.GroupBy(a => a.GetName().Name).Where(g => g.Count() > 1).Select(g => new { name = g.Key, count = g.Count() }).ToArray(),
                boundPsStateLoadContext = AssemblyLoadContext.GetLoadContext(Bindings.StateType.Assembly)?.Name,
                activeMods = LoadedModManager.RunningModsListForReading.Select(m => new { packageId = m.PackageIdPlayerFacing, root = m.RootDir }).ToArray()
            };
            return diagEnvironment;
        }

        private static void WriteControlDiagnostics(string reason)
        {
            try
            {
                object psActive = AccessTools.PropertyGetter(Bindings.StateType, "IsActive")?.Invoke(null, null);
                object psLock = cameraLockField?.GetValue(null);
                var keys = new[] { ControlAction.Forward, ControlAction.Backward, ControlAction.Left, ControlAction.Right, ControlAction.Aim }.Select(action => {
                    bool found = ControlKeys.Keys(action, out var k);
                    return new { action = action.ToString(), primary = found ? k.keyBindingA.ToString() : null,
                        secondary = found ? k.keyBindingB.ToString() : null,
                        rawHeld = found && (ControlKeys.RawHeld(k.keyBindingA) || ControlKeys.RawHeld(k.keyBindingB)) };
                }).ToArray();
                Vector3 move = avatar == null ? Vector3.zero : (Vector3)Bindings.MoveField.GetValue(avatar);
                Vector3? position = avatar == null ? null : (Vector3?)Bindings.PhysicsPosition.GetValue(avatar);
                string blocker = ControlBlocker();
                var snapshot = new {
                    schema = 1, session = diagSession, reason, utc = DateTimeOffset.UtcNow,
                    build = typeof(Bridge).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                    environment = DiagnosticEnvironment(), frame = Time.frameCount, ticks = Current.Game == null ? (int?)null : Find.TickManager?.TicksGame,
                    pawnId = pawn?.thingIDNumber, view = ControlCamera.Mode.ToString(), active = Active,
                    firstPerson = FirstPerson.On, sameAvatar = ReferenceEquals(Bindings.Avatar, avatar),
                    captured = policy.Captured, wantsCapture = policy.WantsCapture, focused = Application.isFocused,
                    blocker, psActive, psCameraLocked = psLock != null, cursor = Cursor.lockState.ToString(),
                    keyboardControl = GUIUtility.keyboardControl, openTab = Current.Game == null ? null : Find.MainTabsRoot?.OpenTab?.defName,
                    windowTypes = Find.WindowStack?.Windows.Select(w => w.GetType().FullName).ToArray(), keys,
                    move = new[] { move.x, move.y, move.z }, physicsPosition = position.HasValue ? new[] { position.Value.x, position.Value.y, position.Value.z } : null,
                    counts = new { frames = diagFrames, gui = diagGuiCalls, guiRepaints = diagGuiRepaints, hudRepaints = diagHudRepaints,
                        psUpdates = diagPsUpdates, psGuiAttempts = diagPsGuiAttempts, psGuiIntentionallySuppressed = diagPsGuiSuppressed,
                        nativePhysicsAttempts = diagNativePhysics, physicsFallbacks = diagPhysicsFallback,
                        nativeRenderAttempts = diagNativeRender, renderFallbacks = diagRenderFallback, mouseEventsConsumed = diagMouseConsumed },
                    patches = new[] { PatchState(AccessTools.Method(typeof(Root_Play), "Update")),
                        PatchState(AccessTools.Method(typeof(UIRoot_Play), "UIRootOnGUI")),
                        PatchState(AccessTools.DeclaredMethod(Bindings.StateType, "Update")), PatchState(Bindings.StateOnGUI),
                        PatchState(Bindings.AvatarPhysics), PatchState(Bindings.AvatarRender) }
                };
                string folder = Path.Combine(GenFilePaths.SaveDataFolderPath, "Logs"); Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "FpsBridge-control-diagnostics.json"), JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
                Log.Message("[FpsBridge] CONTROL_DIAG reason=" + reason + "; blocker=" + blocker + "; frames=" + diagFrames
                    + "; psUpdate=" + diagPsUpdates + "; nativePhysics=" + diagNativePhysics + "; fallbackPhysics=" + diagPhysicsFallback
                    + "; nativeRender=" + diagNativeRender + "; fallbackRender=" + diagRenderFallback + "; hud=" + diagHudRepaints + "; mouse=" + diagMouseConsumed);
            }
            catch (Exception e) { Log.WarningOnce("[FpsBridge] Control diagnostic snapshot unavailable: " + e.Message, 19271731); }
        }
    }
}
