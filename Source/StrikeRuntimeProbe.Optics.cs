using System;
using System.Linq;
using HarmonyLib;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    public sealed partial class StrikeRuntimeProbe
    {
        private static object opticsGun;
        private static int opticsStage;
        private static DateTime opticsDeadline;
        private static bool opticsShot, opticsHit;
        private static V3 opticsExpected, opticsActual;
        private static float opticsYawError, opticsLift;
        private static Vector2 opticsMarker;

        private static bool StepOptics(Map map)
        {
            if (opticsStage == 0)
            {
                opticsDeadline = DateTime.UtcNow.AddSeconds(35);
                SpawnCombatVehicle(map, "VVE_Bulldog", InstallPintle, "commander");
                var harmony = new Harmony("hsk.strike.workshop.optics-test");
                harmony.Patch(AccessTools.Method(typeof(CombatDiagnostics), "CeContact"),
                    prefix: new HarmonyMethod(typeof(StrikeRuntimeProbe), nameof(ObserveOpticsContact)));
                opticsStage = 1;
                return false;
            }
            Require(DateTime.UtcNow < opticsDeadline, $"Optics case {opticsStage} timed out (scoped={VehicleControl.Scoped}, contact={VehiclePrediction.Contact}, shot={opticsShot}, hit={opticsHit}).");
            if (FirstPersonTransition.Active || !VehicleControl.Captured) return false;
            var capture = (CapturePolicy)AccessTools.Field(typeof(VehicleControl), "capture").GetValue(null);
            if (capture.SkipMouse) return false;

            if (opticsStage == 1)
            {
                for (int i = 0; i < VehicleWeapons.Available().Count && VehicleWeapons.Current != opticsGun; i++)
                    VehicleWeapons.Select(1);
                Require(VehicleWeapons.Current == opticsGun && VehicleControl.CanFire, "Commander could not select the installed Pintle MG.");
                VehicleControl.MouseDown(KeyCode.Mouse1);
                Require(VehicleControl.Scoped, "The native right-click handler did not enter the upgraded turret optic. Aim binding=" + ControlKeys.Label(ControlAction.Aim));
                AimOptic(15f, 18f);
                opticsStage = 2;
                return false;
            }

            if (!opticsShot)
            {
                if (!VehiclePrediction.Contact) return false;
                V3 direction = VehiclePrediction.Direction;
                float actualYaw = Mathf.Atan2(direction.X, direction.Z) * Mathf.Rad2Deg;
                float wantedYaw = opticsStage == 2 ? 15f : 100f;
                if (Mathf.Abs(Mathf.DeltaAngle(wantedYaw, actualYaw)) > 1f) return false;
                opticsYawError = Mathf.Abs(Mathf.DeltaAngle(FirstPerson.Yaw, actualYaw));
                Require(opticsYawError < 1f, "Scope camera and actual firing bearing diverged.");

                Vector3 eye = VehicleControl.Eye(), forward = VehicleModels.U(direction);
                if (!VehicleModelPicking.TryOwnMeshTop(combatVehicle, VehicleModels.World(combatVehicle, Vector3.zero), out _)) return false;
                Require(VehicleScopeCamera.TrySecondaryAnchor(combatVehicle, opticsGun, true, out var raw),
                    "The real secondary weapon sight anchor was unavailable.");
                opticsLift = eye.y - raw.y;
                Require(Mathf.Abs(eye.x - raw.x) < .03f && Mathf.Abs(eye.z - raw.z) < .03f && opticsLift >= -.005f
                    && VehicleScopeCamera.OwnMeshLift(combatVehicle, eye, forward) < .01f,
                    "The scope eye is off the native mount or intersects the vehicle near the optic.");

                opticsExpected = VehiclePrediction.Impact;
                var area = new Rect(0, 0, 250, 300);
                Require(VehiclePrediction.TryMinimapPosition(area, true, out opticsMarker), "Dubs minimap had no predicted contact marker.");
                Vector3 logical = FloorSpace.Logical(map, opticsExpected, VehiclePrediction.ImpactCell);
                var expectedMarker = new Vector2(logical.x / map.Size.x * area.width, (1f - logical.z / map.Size.z) * area.height);
                Require(Vector2.Distance(opticsMarker, expectedMarker) < 1f, "The minimap marker did not map the predicted native contact.");

                int launches = CeCompat.FpsLaunched;
                opticsShot = true;
                Require((bool)AccessTools.Method(typeof(VehicleWeapons), "Fire").Invoke(null, new[] { opticsGun })
                    && CeCompat.FpsLaunched == launches + 1, "Upgraded Pintle MG did not launch a native CE round.");
                return false;
            }

            if (!opticsHit) return false;
            float contactError = (opticsExpected - opticsActual).Length;
            Require(contactError <= .5f, $"Predicted and live CE contacts differ by {contactError:0.###} cells ({opticsExpected} vs {opticsActual}).");
            Report(opticsStage == 2 ? "workshop-pintle-optics-first-bearing" : "workshop-pintle-optics-second-bearing", new
            {
                vehicle = combatVehicle.def.defName, turret = VehicleApi.TurretKey(opticsGun), opticsYawError, opticsLift, contactError,
                predicted = new { opticsExpected.X, opticsExpected.Y, opticsExpected.Z },
                actual = new { opticsActual.X, opticsActual.Y, opticsActual.Z }, minimap = new { opticsMarker.x, opticsMarker.y },
            });
            if (opticsStage++ == 2)
            {
                opticsShot = opticsHit = false;
                LoadPintle();
                AimOptic(100f, 18f);
                return false;
            }
            VehicleControl.MouseDown(KeyCode.Mouse1);
            Require(!VehicleControl.Scoped, "Right-click did not leave the upgraded turret optic.");
            DespawnCombatVehicle();
            return true;
        }

        private static void InstallPintle(Pawn vehicle)
        {
            var tree = VehicleApi.Get(vehicle, "CompUpgradeTree");
            var treeDef = VehicleApi.Get(VehicleApi.Get(tree, "Props"), "def");
            var node = VehicleApi.Call(treeDef, "GetNode", "PintleMG");
            var upgrade = VehicleApi.List(node, "upgrades").Single(u => u.GetType().Name == "TurretUpgrade");
            VehicleApi.Call(upgrade, "Unlock", vehicle, false);
            var turrets = VehicleApi.Turrets(vehicle);
            opticsGun = turrets.Single(t => VehicleApi.TurretKey(t) == "pintleMG");
            var parent = turrets.Single(t => VehicleApi.TurretKey(t) == "mainTurret");
            Require(ReferenceEquals(VehicleApi.Get(opticsGun, "attachedTo"), parent), "Pintle upgrade did not retain its actual native turret parent.");
            VehicleApi.Set(parent, "TurretRotation", 50f);
            LoadPintle();
        }

        private static void LoadPintle()
        {
            var filter = (ThingFilter)VehicleApi.Get(VehicleApi.Get(opticsGun, "def"), "ammunition");
            var ammo = filter.AllowedThingDefs.FirstOrDefault(a => a.defName.EndsWith("_FMJ", StringComparison.Ordinal)) ?? filter.AllowedThingDefs.First();
            VehicleApi.Set(opticsGun, "loadedAmmo", ammo);
            VehicleApi.Set(opticsGun, "shellCount", 4);
            VehicleApi.Set(opticsGun, "reloadTicks", 0);
        }

        private static void AimOptic(float yaw, float pitch) => VehicleWeapons.ScopeLook(
            Mathf.DeltaAngle(VehicleApi.Float(opticsGun, "TurretRotation"), yaw), pitch - VehicleWeapons.PitchOf(opticsGun));

        private static void ObserveOpticsContact(Thing projectile, Thing source)
        {
            if (!opticsShot || opticsHit || source != combatVehicle) return;
            var table = AccessTools.Field(typeof(CeCompat), "traces").GetValue(null);
            var args = new object[] { projectile, null };
            if ((bool)AccessTools.Method(table.GetType(), "TryGetValue").Invoke(table, args))
            {
                opticsActual = (V3)AccessTools.Field(args[1].GetType(), "pos").GetValue(args[1]);
                opticsHit = true;
            }
        }
    }
}
