using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using IsoRim;
using RimWorld.Planet;
using Verse;

namespace HSK.FpsBridge
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        internal const string Id = "hsk.fpsbridge.control.r4";
        internal static bool Ready;
        internal static string StartupFailure { get; private set; }

        // The game's language for L.T, rechecked only when the active language changes.
        private static LoadedLanguage lastLanguage;
        private static bool lastChinese = true;
        private static bool GameIsChinese()
        {
            LoadedLanguage lang = LanguageDatabase.activeLanguage;
            if (!ReferenceEquals(lang, lastLanguage))
            {
                lastLanguage = lang;
                string name = (lang?.folderName ?? "") + " " + (lang?.LegacyFolderName ?? "");
                lastChinese = lang == null || name.IndexOf("Chinese", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            return lastChinese;
        }

        static Bootstrap()
        {
            L.IsChinese = GameIsChinese;
            var harmony = new Harmony(Id);
            try
            {
                ValidateWorkshopSources();
                Bindings.Initialize();
                FloorSpace.Bind();
                var plan = new List<(MethodBase target, string prefix, string postfix)> {
                    (Bindings.Method(typeof(FirstPerson), "Toggle", true), null, nameof(Bridge.AfterToggle)),
                    (Bindings.Method(typeof(FirstPerson), "Leave", true, typeof(string)), nameof(Bridge.BeforeLeave), nameof(Bridge.AfterLeave)),
                    (Bindings.Method(typeof(FirstPerson), "Update", true), nameof(Bridge.BeforeFirstPerson), nameof(Bridge.AfterFirstPerson)),
                    (Bindings.Method(typeof(View3D), "HandleLook", true), nameof(Bridge.AllowOriginalLook), null),
                    (Bindings.AvatarCamera, nameof(Bridge.AllowAvatarCamera), null),
                    (Bindings.AvatarInput, null, nameof(Bridge.AfterMoveInput)),
                    (Bindings.AvatarPhysics, nameof(Bridge.BeforePhysics), nameof(Bridge.AfterPhysics)),
                    (Bindings.ProcessMovement, nameof(Bridge.BeforeProcessMovement), null),
                    (Bindings.AvatarRotate, nameof(Bridge.AllowAvatarRotation), null),
                    (Bindings.AvatarMouseRotate, nameof(Bridge.AllowAvatarRotation), null),
                    (Bindings.ControlsFrozen, null, nameof(Bridge.AfterControlsFrozen)),
                    (Bindings.StateClear, nameof(Bridge.BeforeAvatarClear), null),
                    (Bindings.CombatStance, nameof(Bridge.AllowCombatStance), null),
                    (Bindings.Firing, nameof(Bridge.BeforeFiring), null),
                    (Bindings.HoldFire, nameof(Bridge.AllowHoldFire), null),
                    (Bindings.CursorReticle, nameof(Bridge.AllowReticle), null),
                    (Bindings.Method(Bindings.AvatarType, "DrawEquipHint", false, typeof(bool)), nameof(Bridge.AllowEquipHint), null),
                    (Bindings.SelectorClick, nameof(Bridge.BeforeSelectorClick), null),
                    (Bindings.Method(typeof(WindowStack), "Add", false, typeof(Window)), nameof(Bridge.BeforeWindowAdd), null),
                    (Bindings.Method(typeof(CameraDriver), "CameraDriverOnGUI", false), nameof(Bridge.BeforeCameraGUI), null),
                    (AccessTools.Method(typeof(WindowStack), "TryRemove", new[] { typeof(Window), typeof(bool) }), null, nameof(Bridge.AfterWindowRemoved)),
                    (AccessTools.PropertyGetter(typeof(KeyBindingDef), "JustPressed"), nameof(Bridge.FilterKeyBinding), null),
                    (AccessTools.PropertyGetter(typeof(KeyBindingDef), "KeyDownEvent"), nameof(Bridge.FilterKeyBinding), null),
                    (AccessTools.PropertyGetter(typeof(KeyBindingDef), "IsDown"), nameof(Bridge.FilterKeyBinding), null),
                    (Bindings.Method(typeof(Verb), "TryCastNextBurstShot", false), nameof(Bridge.BeforeBurstShot), null),
                    (AccessTools.Constructor(typeof(Stance_Cooldown), new[] { typeof(int), typeof(LocalTargetInfo), typeof(Verb) })
                        ?? throw new MissingMethodException("Stance_Cooldown(int, LocalTargetInfo, Verb)"), nameof(Bridge.BeforeCooldown), null),
                    (AccessTools.Method(typeof(Root_Play), "Update") ?? throw new MissingMethodException("Root_Play.Update"), nameof(Bridge.FrameBegin), nameof(Bridge.FrameEnd)),
                    (AccessTools.Method(typeof(Game), "Dispose") ?? throw new MissingMethodException("Game.Dispose"), nameof(Bridge.GameDisposing), null)
                };
                MethodInfo worldSave = Bindings.Method(typeof(World), "ExposeData", false);
                foreach (var p in plan)
                    harmony.Patch(p.target, HM(p.prefix, Priority.First), HM(p.postfix, Priority.Last));
                harmony.Patch(worldSave, prefix: HM(nameof(Bridge.BeforeWorldSave), Priority.First));
                harmony.Patch(Bindings.AvatarPhysics, transpiler: HM(nameof(Bridge.WalkingFire), Priority.Last));
                harmony.Patch(Bindings.ProcessMovement, transpiler: HM(nameof(Bridge.WalkingFire), Priority.Last));
                // Our FPS_ViewCycle binding owns entry; never rewrite another mod's saved keyboard preferences.
                Bridge.InstallHudCallbacks(harmony);
                Bridge.InstallCapturedMouse(harmony);
                ControlCamera.Install(harmony);
                DubsMinimap.InstallRotation();
                var movementRecovery = new Harmony(Id + ".movement-guard");
                try { Bridge.InstallMovementRecovery(movementRecovery); }
                catch (Exception e) { movementRecovery.UnpatchAll(Id + ".movement-guard"); Log.Warning("[FpsBridge] MOVE_GUARD unavailable; existing movement retained: " + e.Message); }
                Ready = true;
                var eventDelivery = new Harmony(Id + ".event-delivery");
                try { FirstPersonEventDelivery.Install(eventDelivery); }
                catch (Exception e)
                {
                    eventDelivery.UnpatchAll(eventDelivery.Id);
                    Log.Warning("[FpsBridge] First-person event delivery unavailable: " + e.Message);
                }
                var descent = new Harmony(Id + ".descent");
                try { FirstPersonTransition.Install(descent); }
                catch (Exception e) { descent.UnpatchAll(Id + ".descent"); Log.Warning("[FpsBridge] Descent animation unavailable: " + e.Message); }
                var music = new Harmony(Id + ".combatmusic");
                try { CombatMusicManager.Install(music); }
                catch (Exception e) { music.UnpatchAll(Id + ".combatmusic"); Log.Warning("[FpsBridge] Combat music unavailable: " + e.Message); }
                CeCompat.Install();
                InstallBallistics();
                RoofHealth.Install(new Harmony(Id+".roof-health"));
                var combatDiagnostics = new Harmony(Id + ".combat-diagnostics");
                try { CombatDiagnostics.Install(combatDiagnostics); }
                catch (Exception e) { combatDiagnostics.UnpatchAll(combatDiagnostics.Id); Log.Warning("[FpsBridge] Damage diagnostics unavailable: " + e); }
                MountedSafety.Install(new Harmony(Id + ".mounted-safety"));
                InstallFloorSound();
                InstallStances();
                try { MountedControl.InstallPose(new Harmony(Id + ".mounted-pose")); }
                catch (Exception e) { Log.Warning("[FpsBridge] Mounted attachment unavailable: " + e.Message); }
                CorpsePoseRecovery.Install(new Harmony(Id+".corpse-pose"));
                InstallWork();
                var interaction = new Harmony(Id + ".interaction-wheel");
                try { InteractionWheel.Install(interaction); }
                catch (Exception e) { interaction.UnpatchAll(interaction.Id); Log.Warning("[FpsBridge] Interaction wheel unavailable; target marking retained: " + e); }
                var magazines = new Harmony(Id + ".magazines");
                try { Magazines.Install(magazines); }
                catch (Exception e) { magazines.UnpatchAll(Id + ".magazines"); Log.Error("[FpsBridge] Virtual magazine native hooks failed: " + e); }
                try { HSK.ImportedRendering.SurfaceAdapter.Install(); }
                catch (Exception e) { Log.Warning("[FpsBridge] Imported surface hook off: " + e.Message); }
                VehicleApi.Install();
                if (VehicleApi.Ready)
                {
                    var vehicleBallistics = new Harmony(Id + ".vehicle-ballistic-damage");
                    try { VehicleBallisticDamage.Install(vehicleBallistics); }
                    catch (Exception e) { vehicleBallistics.UnpatchAll(vehicleBallistics.Id); Log.Warning("[FpsBridge] Vehicle ballistic mapping unavailable: " + e); }
                    var crewOwnership = new Harmony(Id + ".vehicle-crew-ownership");
                    try { VehicleCrewOwnership.Install(crewOwnership); }
                    catch (Exception e) { crewOwnership.UnpatchAll(crewOwnership.Id); Log.Warning("[FpsBridge] Vehicle crew ownership unavailable: " + e); }
                    var vehicleClaim = new Harmony(Id + ".vehicle-claim");
                    try { VehicleClaim.Install(vehicleClaim); }
                    catch (Exception e) { vehicleClaim.UnpatchAll(vehicleClaim.Id); Log.Warning("[FpsBridge] Vehicle claiming unavailable: " + e); }
                    var vehicleRepair = new Harmony(Id + ".vehicle-repair-index");
                    try { VehicleRepairIndex.Install(vehicleRepair); }
                    catch (Exception e) { vehicleRepair.UnpatchAll(vehicleRepair.Id); Log.Warning("[FpsBridge] Vehicle repair index unavailable: " + e); }
                }
                if (VehicleApi.Ready)
                {
                    var aircraftDamage = new Harmony(Id + ".aircraft-damage");
                    try { AircraftDamage.Install(aircraftDamage); }
                    catch (Exception e) { aircraftDamage.UnpatchAll(aircraftDamage.Id); Log.Warning("[FpsBridge] Aircraft terminal damage unavailable: " + e); }
                }
                if (VehicleApi.Ready)
                {
                    var picking = new Harmony(Id + ".vehicle-model-pick");
                    try { VehicleModelPicking.Install(picking); }
                    catch (Exception e) { picking.UnpatchAll(picking.Id); Log.Warning("[FpsBridge] Vehicle model picking unavailable: " + e); }
                }
                if (VehicleApi.Ready)
                    try { AerialFlight.Install(new Harmony(Id + ".aerial-vtol")); }
                    catch (Exception e) { new Harmony(Id + ".aerial-vtol").UnpatchAll(Id + ".aerial-vtol"); Log.Warning("[FpsBridge] Optional VTOL hooks unavailable: " + e); }
                if (VehicleApi.Ready)
                    try { VehicleVveMovement.Install(); }
                    catch (Exception e) { new Harmony(Id + ".vve-throttle").UnpatchAll(Id + ".vve-throttle"); Log.Warning("[FpsBridge] VVE throttle hook off: " + e.Message); }
                var skills = new Harmony(Id + ".skills");
                try { CyberSkills.Install(skills); Log.Message("[FpsBridge] SKILLS ON (Sandevistan, Death Eye)"); }
                catch (Exception e) { skills.UnpatchAll(Id + ".skills"); Log.Warning("[FpsBridge] Sandevistan / Death Eye off: " + e.Message); }
                try { Social.Build(); Log.Message("[FpsBridge] SOCIAL ON (T: greet, hug, head pat, chat)"); }
                catch (Exception e) { Log.Warning("[FpsBridge] Social acts off: " + e); }
                Log.Message("[FpsBridge] CONTROL_R4 READY; hooks=" + (plan.Count + 6) + "; ballistics=" + (Ballistics.Enabled ? "ON" : "OFF")
                    + "; stances=" + (Stances.Enabled ? (Stances.SpeedEnabled ? "ON" : "ON_NO_SPEED") : "OFF")
                    + "; work=" + (WorkControl.Enabled ? "ON" : "OFF") + "; turrets=" + (TurretControl.Enabled ? "ON" : "OFF") + "; mechs=" + (MechControl.Enabled ? "ON" : "OFF") + "; vehicles=" + VehicleApi.Status
                    + "; descent=" + (FirstPersonTransition.Enabled ? "ON" : "OFF") + "; combatMusic=" + (CombatMusicManager.Enabled ? "ON" : "OFF") + "; source=" + Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
                LogAssembly("bridge", Assembly.GetExecutingAssembly());
                LogAssembly("IsoRim", typeof(FirstPerson).Assembly);
                LogAssembly("PerspectiveShift", Bindings.StateType.Assembly);
                LogAssembly("game", typeof(Pawn).Assembly);
            }
            catch (Exception e)
            {
                Ready = false;
                StartupFailure = e.Message;
                harmony.UnpatchAll(Id);
                Log.Error("[FpsBridge] CONTROL_R4 DISABLED: wrong dependency source or incompatible upstream hook; " + e);
            }
        }

        // Ballistics is separable: if its game hooks do not match, control keeps working with the game's own flight.
        private static void InstallBallistics()
        {
            var harmony = new Harmony(Id + ".ballistics");
            try
            {
                Ballistics.Install(harmony);
                try { ModelHits.Install(); Log.Message("[FpsBridge] MODEL_HITS ON (person hit volumes fitted to IsoRim's drawn model)"); }
                catch (Exception e) { Log.Warning("[FpsBridge] Model-fitted hit volumes off (proportional volumes used): " + e.Message); }
                var meshes = new Harmony(Id + ".meshhits");
                try { FigureMeshes.Install(meshes); Log.Message("[FpsBridge] MESH_HITS ON (rounds hit drawn people/animals/mechs on their triangles)"); }
                catch (Exception e) { meshes.UnpatchAll(Id + ".meshhits"); Log.Warning("[FpsBridge] Exact model hits off (volumes used): " + e.Message); }
            }
            catch (Exception e)
            {
                harmony.UnpatchAll(Id + ".ballistics");
                Ballistics.Disable();
                Log.Error("[FpsBridge] BALLISTICS DISABLED (control still active): incompatible game hook; " + e);
            }
        }

        // Postures are separable too: a mismatch turns them off and leaves control and ballistics alone.
        private static void InstallStances()
        {
            var harmony = new Harmony(Id + ".stances");
            try { Stances.Install(harmony); }
            catch (Exception e)
            {
                harmony.UnpatchAll(Id + ".stances");
                Log.Error("[FpsBridge] STANCES DISABLED (control still active): incompatible hook; " + e);
            }
            // Walking on tops: only the game's pather recovery is patched.
            var tops = new Harmony(Id + ".surface");
            try { Surface.Install(tops); Log.Message("[FpsBridge] SURFACE ON (climb onto wall/rock/roof tops)"); }
            catch (Exception e) { tops.UnpatchAll(Id + ".surface"); Log.Warning("[FpsBridge] Surface spike hook off: " + e.Message); }
        }

        // H auto work / F manual work are separable as well: without them H and F stay the game's keys.
        private static void InstallWork()
        {
            var harmony = new Harmony(Id + ".work");
            try { WorkControl.Install(harmony); }
            catch (Exception e)
            {
                harmony.UnpatchAll(Id + ".work");
                Log.Error("[FpsBridge] WORK CONTROL DISABLED (control still active): incompatible hook; " + e);
            }
            var mechs = new Harmony(Id + ".mech");
            try { MechControl.Install(mechs); }
            catch (Exception e)
            {
                mechs.UnpatchAll(Id + ".mech");
                Log.Error("[FpsBridge] MECH POSSESSION DISABLED (control still active): incompatible hook; " + e);
            }
            var turrets = new Harmony(Id + ".turret");
            try { TurretControl.Install(turrets); RemoteTurret.Install(turrets); }
            catch (Exception e)
            {
                turrets.UnpatchAll(Id + ".turret");
                Log.Error("[FpsBridge] TURRET CONTROL DISABLED (control still active): incompatible hook; " + e);
            }
            var ratkin = new Harmony(Id + ".ratkin-projectiles");
            try { RatkinTurretShots.Install(ratkin); }
            catch (Exception e) { ratkin.UnpatchAll(Id + ".ratkin-projectiles"); Log.Warning("[FpsBridge] Ratkin turret projectile adapter off: " + e.Message); }
        }

        // Sounds made upstairs (ProjectZ annex rows) are heard where that floor is drawn. Separable like ballistics.
        private static void InstallFloorSound()
        {
            var harmony = new Harmony(Id + ".floorsound");
            try { FloorSpace.InstallSound(harmony); }
            catch (Exception e)
            {
                harmony.UnpatchAll(Id + ".floorsound");
                Log.Warning("[FpsBridge] Upstairs sound fix off (control still active): " + e.Message);
            }
        }

        private static HarmonyMethod HM(string name, int priority) => name == null ? null : new HarmonyMethod(typeof(Bridge), name) { priority = priority };

        private static void ValidateWorkshopSources()
        {
            var expected = new[] {
                (package: "brrainz.harmony", workshop: "2009463077"),
                (package: "ray1203.simplecamerasetting", workshop: "3232415388"),
                (package: "ferny.resourcedeliveryhelper", workshop: "3696859196"),
                (package: "ferny.perspectiveshift", workshop: "3686618980"),
                (package: "peren.isorim17", workshop: "3819577715")
            };
            foreach (var item in expected)
            {
                ModMetaData mod = ModsConfig.ActiveModsInLoadOrder.FirstOrDefault(candidate =>
                    string.Equals(candidate?.PackageIdNonUnique, item.package, StringComparison.OrdinalIgnoreCase));
                bool supports17 = false;
                if (mod != null)
                    foreach (var version in mod.SupportedVersionsReadOnly)
                        if (version.Major == 1 && version.Minor == 7) supports17 = true;
                var source = DependencySourceRules.Validate(item.package, item.workshop,
                    mod?.PackageId, mod?.RootDir.FullName, mod?.OnSteamWorkshop == true, supports17);
                Log.Message("[FpsBridge] WORKSHOP_SOURCE " + mod.PackageId + "; root=" + source.LogicalRoot
                    + "; physical=" + source.PhysicalRoot + "; linked=" + source.Linked);
            }
        }

        private static void LogAssembly(string label, Assembly assembly)
        {
            string path = assembly.Location;
            string hash = File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "NO_FILE_LOCATION";
            Log.Message("[FpsBridge] " + label + "=" + assembly.FullName + "; SHA256=" + hash + "; path=" + path);
        }
    }
}

