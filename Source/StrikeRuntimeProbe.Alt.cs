using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    public partial class StrikeRuntimeProbe
    {
        private const string AltProbeId = "hsk.strike.runtimeprobe.alt";
        private static Pawn altAnimal;
        private static bool altHeld;
        private static int altInspectionFrames, altMeshFrames;
        private static int altKeyReads;
        private static KeyCode altKey, altSecondKey;
        private static string altLastTitle;
        private static int altStage, altReleaseFrame;
        private static TimeSpeed altPriorSpeed;
        private static DateTime altDeadline;

        private static bool StepAltOverlay(Map map)
        {
            if (altStage == 0)
            {
                altPriorSpeed = Find.TickManager.CurTimeSpeed;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                altAnimal = (Pawn)GenSpawn.Spawn(PawnGenerator.GeneratePawn(PawnKindDef.Named("Hare")),
                    combatOrigin + new IntVec3(0, 0, 10), map, Rot4.South);
                SpawnCombatVehicle(map, "VVE_Bulldog");
                altStage = 1;
                altDeadline = DateTime.UtcNow.AddSeconds(15);
                return false;
            }
            if (DateTime.UtcNow >= altDeadline)
                throw new TimeoutException($"Alt phase {altStage}: simulated={altHeld}, key={altKey}, keyReads={altKeyReads}, " +
                    $"held={ControlKeys.Held(ControlAction.Overlay)}, focused={Application.isFocused}, captured={VehicleControl.Captured}, " +
                    $"input={VehicleControl.InputBlockReason}, hud={altInspectionFrames}, meshes={altMeshFrames}, title={altLastTitle}");
            if (FirstPersonTransition.Active || !VehicleControl.Captured) return false;
            bool air = altStage >= 3;

            if (altStage is 1 or 3)
            {
                if (!FigureMeshes.Has(altAnimal)) return false;
                var result = AimAtAltAnimal();
                Require(result.Title == altAnimal.LabelShortCap.ToString() && result.Rows.Any(s => s.Contains(" HP")),
                    "The vehicle camera could not inspect the " + altAnimal.def.defName + " mesh and part HP.");
                if (air) VerifyTinyAnimalGeometry();
                altInspectionFrames = 0;
                altMeshFrames = 0;
                altKeyReads = 0;
                altHeld = true;
                altStage++;
                return false;
            }

            if (altStage is 2 or 4)
            {
                if (altHeld)
                {
                    if (altInspectionFrames == 0 || altMeshFrames == 0) return false;
                    var aimed = HitboxView.Inspect(combatVehicle);
                    Require(aimed.Title == altAnimal.LabelShortCap.ToString() && altLastTitle == aimed.Title,
                        "Alt HUD lost the aimed animal");
                    VehicleControl.SetLook(FirstPerson.Yaw, -70f);
                    var own = HitboxView.Inspect(combatVehicle);
                    Require(own.Title == combatVehicle.LabelShortCap.ToString() && own.Rows.Count > 1,
                        "Alt inspection did not expose native component hit points");
                    Report(air ? "alt-airborne-hud" : "alt-ground-hud", new
                    {
                        vehicle = combatVehicle.def.defName, animal = altAnimal.def.defName,
                        animalAge = altAnimal.ageTracker.AgeBiologicalYearsFloat, bodySize = altAnimal.BodySize,
                        aimedHp = aimed.Rows, componentHp = own.Rows, hudFrames = altInspectionFrames,
                        meshSubmissions = altMeshFrames, nativeKeyReads = altKeyReads
                    });
                    altHeld = false;
                    altReleaseFrame = Time.frameCount;
                    altInspectionFrames = 0;
                    altMeshFrames = 0;
                    return false;
                }
                if (Time.frameCount - altReleaseFrame < 3) return false;
                Require(altInspectionFrames == 0 && altMeshFrames == 0, "Alt overlay kept drawing after release");
                DespawnCombatVehicle();
                if (!air)
                {
                    altAnimal.Destroy(DestroyMode.Vanish);
                    var juvenile = new PawnGenerationRequest(PawnKindDef.Named("Squirrel"), fixedBiologicalAge: .15f,
                        fixedChronologicalAge: .15f, developmentalStages: DevelopmentalStage.Child | DevelopmentalStage.Adult);
                    altAnimal = (Pawn)GenSpawn.Spawn(PawnGenerator.GeneratePawn(juvenile), combatOrigin + new IntVec3(0, 0, 10),
                        map, Rot4.South);
                    Require(altAnimal.ageTracker.CurLifeStage.defName == "AnimalJuvenile",
                        "The tiny animal probe needs a juvenile Squirrel.");
                    SpawnCombatVehicle(map, "VVE_Mosquito");
                    Require(AerialFlight.TryGet(combatVehicle, out var flight), "Alt test Mosquito did not enter flight control");
                    var motor = flight.Motor;
                    motor.Position.Y = flight.HomeHeight + 8f;
                    flight.Motor = motor;
                    flight.Airborne = true;
                    altStage = 3;
                    altDeadline = DateTime.UtcNow.AddSeconds(15);
                    return false;
                }
                altAnimal.Destroy(DestroyMode.Vanish);
                altAnimal = null;
                Find.TickManager.CurTimeSpeed = altPriorSpeed;
                new Harmony(AltProbeId).UnpatchAll(AltProbeId);
                altStage = 5;
            }
            return altStage == 5;
        }

        private static (string Title, IReadOnlyList<string> Rows) AimAtAltAnimal()
        {
            V3 spot = FloorSpace.Spot(altAnimal);
            for (int i = 1; i <= 44; i++)
            {
                V3 aim = spot + new V3(0, i * .015f, 0);
                for (int k = 0; k < 3; k++)
                {
                    V3 d = aim - VehicleModels.V(VehicleControl.Eye());
                    float yaw = Mathf.Atan2(d.X, d.Z) * Mathf.Rad2Deg;
                    float pitch = -Mathf.Atan2(d.Y, new Vector2(d.X, d.Z).magnitude) * Mathf.Rad2Deg;
                    VehicleControl.SetLook(yaw, pitch);
                }
                var result = HitboxView.Inspect(combatVehicle);
                if (result.Title == altAnimal.LabelShortCap.ToString()) return result;
            }
            return default;
        }

        private static void VerifyTinyAnimalGeometry()
        {
            V3 eye = VehicleModels.V(VehicleControl.Eye());
            V3 aimEnd = eye + HitMath.Direction(FirstPerson.Yaw, FirstPerson.Pitch) * 40f;
            var trace = new Ballistics.ExternalTrace(combatVehicle);
            Map map = combatVehicle.Map;
            Require(FigureMeshes.Hit(altAnimal, eye, aimEnd, out float meshFraction, out _)
                && trace.Segment(map, eye, aimEnd, out _, out Thing hit, out _, out _, out _) && hit == altAnimal,
                "The aimed ray must hit the juvenile squirrel's drawn mesh through the projectile trace.");

            var oldVolumes = new List<HitVolume>();
            BodyRegions.AddVolumes(altAnimal, oldVolumes);
            V3 center = FloorSpace.Spot(altAnimal);
            V3 right = HitMath.Right(FirstPerson.Yaw);
            for (int y = 1; y <= 45; y++)
            for (int x = -30; x <= 30; x++)
            {
                V3 direction = center + right * (x * .02f) + new V3(0, y * .015f, 0) - eye;
                V3 end = eye + direction * (40f / direction.Length);
                if (HitMath.Nearest(eye, end, oldVolumes, out float volumeFraction) < 0
                    || FigureMeshes.Hit(altAnimal, eye, end, out _, out _)) continue;
                if (trace.Segment(map, eye, end, out _, out Thing target, out _, out _, out _) && target == altAnimal) continue;
                Report("alt-small-animal-geometry", new
                {
                    animal = altAnimal.def.defName, lifeStage = altAnimal.ageTracker.CurLifeStage.defName,
                    bodySize = altAnimal.BodySize, meshHit = meshFraction, emptyVolumeHit = volumeFraction
                });
                return;
            }
            throw new InvalidOperationException("Could not find a ray through the old volume's empty space around the squirrel mesh.");
        }

        private static void InstallAltProbe()
        {
            Require(ControlKeys.Keys(ControlAction.Overlay, out var binding), "Missing Alt overlay binding.");
            altKey = binding.keyBindingA;
            altSecondKey = binding.keyBindingB;
            Require(altKey is KeyCode.LeftAlt or KeyCode.RightAlt, "The isolated probe expects Alt as the first overlay binding.");
            var harmony = new Harmony(AltProbeId);
            var probe = typeof(StrikeRuntimeProbe);
            harmony.Patch(AccessTools.Method(typeof(Input), "GetKeyRaw", new[] { typeof(KeyCode) }),
                prefix: new HarmonyMethod(probe, nameof(SimulateAlt)));
            harmony.Patch(AccessTools.Method(typeof(HitboxView), "Panel"),
                postfix: new HarmonyMethod(probe, nameof(RecordAltHud)));
            harmony.Patch(AccessTools.Method(typeof(FigureMeshes), "DrawHitMesh"),
                postfix: new HarmonyMethod(probe, nameof(RecordAltMesh)));
        }

        private static bool SimulateAlt(KeyCode __0, ref bool __result)
        {
            if (altStage is < 1 or > 4 || __0 != altKey && __0 != altSecondKey) return true;
            altKeyReads++;
            __result = altHeld && __0 == altKey;
            return false;
        }

        private static void RecordAltHud(string title)
        {
            altInspectionFrames++;
            altLastTitle = title;
        }

        private static void RecordAltMesh(Pawn pawn, bool __result)
        {
            if (pawn == altAnimal && __result) altMeshFrames++;
        }
    }
}
