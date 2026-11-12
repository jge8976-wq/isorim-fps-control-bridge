using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    public sealed partial class StrikeRuntimeProbe : GameComponent
    {
        private static readonly Dictionary<string, object> Results = new();
        private static string folder;
        private bool enabled, finished, selected, combatPassed, aircraftPassed, structurePassed, altPassed, opticsPassed;
        private int frames;

        public StrikeRuntimeProbe(Game game) { }

        public override void LoadedGame()
        {
            folder = GenFilePaths.SaveDataFolderPath;
            enabled = GenCommandLine.CommandLineArgPassed("strike-r6-native-probe")
                && Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) == "CE-Auto"
                && File.Exists(Path.Combine(folder, "Strike-R6-Native-Probe.enabled"));
        }

        public override void GameComponentUpdate()
        {
            if (!enabled || finished || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) return;
            try
            {
                if (++frames == 1) ValidateProfile(Find.CurrentMap);
                // The range predates the complete list. VEF can queue more than one migration prompt.
                foreach (Window window in Find.WindowStack.Windows.ToArray())
                    if (window.GetType().FullName == "VEF.Factions.Dialog_NewFactionSpawning")
                        Find.WindowStack.TryRemove(window, false);
                if (frames == 85) VerifyUiMods(Find.CurrentMap);
                Require(frames < 15000, "Timed out before all native cases completed.");
                if (frames < 90 || LongEventHandler.ShouldWaitForEvent) return;
                if (!selected)
                {
                    selected = StepSelection(Find.CurrentMap);
                    return;
                }
                if (!combatPassed) { combatPassed = StepCombat(Find.CurrentMap); return; }
                if (!aircraftPassed) { aircraftPassed = StepAircraftDamage(Find.CurrentMap); return; }
                if (!structurePassed) { structurePassed = StepStructure(Find.CurrentMap); return; }
                if (!altPassed) { altPassed = StepAltOverlay(Find.CurrentMap); return; }
                if (!opticsPassed) { opticsPassed = StepOptics(Find.CurrentMap); return; }
                if (StepInteraction(Find.CurrentMap)) Finish("PASS", null);
            }
            catch (Exception e) { Finish("FAIL", e); }
        }

        private static void ValidateProfile(Map map)
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "setup.json")));
            string Normalize(string id) => id.ToLowerInvariant().Replace("_steam", "");
            string[] expected = manifest.RootElement.GetProperty("mods").EnumerateArray()
                .Select(m => Normalize(m.GetProperty("package").GetString())).ToArray();
            string[] loaded = LoadedModManager.RunningModsListForReading.Select(m => Normalize(m.PackageId)).ToArray();
            Require(expected.SequenceEqual(loaded), "Actual active mod sequence differs. Missing: "
                + string.Join(", ", expected.Except(loaded)) + "; extra: " + string.Join(", ", loaded.Except(expected))
                + "; expected=" + expected.Length + "; loaded=" + loaded.Length);
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var fps = typeof(VehicleControl).Assembly;
            var fpsPin = manifest.RootElement.GetProperty("mods").EnumerateArray()
                .Single(m => m.GetProperty("package").GetString() == "hsk.fpsbridge.control.r4");
            string fpsPath = Path.Combine(fpsPin.GetProperty("path").GetString(), "1.7", "Assemblies", "HSK.FpsBridge.dll");
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fps.Location)));
            Require(fps.Location.Equals(fpsPath, StringComparison.OrdinalIgnoreCase)
                && sha == fpsPin.GetProperty("critical").GetProperty("sha256").GetString(), "Running FPS DLL did not match the R6 candidate pin.");
            Report("r6-fps-source", new { path = fps.Location, sha256 = sha,
                build = fps.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion });
            Require(assemblies.Any(a => a.GetName().Name == "SimpleSidearms"), "Simple Sidearms assembly is missing.");
            Require(assemblies.Any(a => a.GetName().Name == "RimHUD"), "RimHUD assembly is missing.");
            Require(CeCompat.Ready && VehicleApi.Ready && Ballistics.Enabled, "Combat bindings are unavailable.");
            Require(LanguageDatabase.activeLanguage?.folderName.StartsWith("ChineseSimplified", StringComparison.Ordinal) == true,
                "Simplified Chinese is not active.");
            Report("profile", new { mods = loaded, language = LanguageDatabase.activeLanguage.folderName,
                map = map.uniqueID, mapSize = map.Size.ToString(), floorBands = FloorSpace.Bands(map).Count,
                gameVersion = VersionControl.CurrentVersionStringWithRev,
                enableDamage = DebugSettings.enableDamage, enablePlayerDamage = DebugSettings.enablePlayerDamage });
            View3D.Active = true;
            Pawn pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            Require(pawn != null, "The native UI fixture has no player colonist.");
            Find.Selector.ClearSelection();
            Find.Selector.Select(pawn, false, false);
            InstallAltProbe();
        }

        private static void VerifyUiMods(Map map)
        {
            Pawn pawn = Find.Selector.SingleSelectedThing as Pawn ?? map.mapPawns.FreeColonistsSpawned.First();
            var gizmo = pawn.GetGizmos().FirstOrDefault(g => g.GetType().FullName == "SimpleSidearms.rimworld.Gizmo_SidearmsList");
            Require(gizmo != null, "Simple Sidearms did not produce a native sidearms gizmo.");
            Type state = AccessTools.TypeByName("RimHUD.Engine.State");
            var activated = AccessTools.Property(state, "Activated")?.GetValue(null);
            var hudPawn = AccessTools.Property(state, "SelectedPawn")?.GetValue(null);
            Require(activated is true && hudPawn == pawn, "RimHUD is inactive or did not bind the selected pawn.");
            Report("ui-mods", new { pawn = pawn.ThingID, sidearms = gizmo.GetType().FullName, rimHudActive = true });
        }

        internal static void Require(bool ok, string why)
        {
            if (!ok) throw new InvalidOperationException(why);
        }

        internal static void Report(string name, object details)
        {
            Results[name] = details;
            Write("RUNNING", null);
            Log.Message("[STRIKE_R3_PROBE] " + name + " passed");
        }

        private void Finish(string status, Exception error)
        {
            finished = true;
            Write(status, error?.ToString());
            if (error == null) Log.Message("[STRIKE_R3_PROBE] PASS");
            else Log.Error("[STRIKE_R3_PROBE] FAIL " + error);
            Application.Quit();
        }

        private static void Write(string status, string error)
        {
            File.WriteAllText(Path.Combine(folder, "Strike-R6-Native-Probe-result.json"),
                JsonSerializer.Serialize(new { status, error, results = Results }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
