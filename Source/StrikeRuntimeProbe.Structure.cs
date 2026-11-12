using System;
using System.Linq;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace HSK.FpsBridge
{
    public sealed partial class StrikeRuntimeProbe
    {
        private Thing structureFront, structureRear;
        private IntVec3 structureRoof;
        private int structureStage, structureFrontHp, structureRearHp, structureContacts, structureLaunches;
        private float structureOriginalWood;
        private DateTime structureDeadline;

        private bool StepStructure(Map map)
        {
            if (structureStage == 8) return true;
            if (structureStage == 0)
            {
                structureOriginalWood = FpsMod.Settings.penetrationArmorWood;
                FpsMod.Settings.penetrationArmorWood = ObstacleArmor.Default.Wood;
                if (combatTarget?.Spawned == true) combatTarget.Destroy(DestroyMode.Vanish);
                structureFront = SpawnStructureWall(map, combatOrigin + new IntVec3(0, 0, 16));
                structureRear = SpawnStructureWall(map, combatOrigin + new IntVec3(0, 0, 19));
                structureFrontHp = structureFront.HitPoints;
                structureRearHp = structureRear.HitPoints;
                combatActor.Position = combatOrigin;
                combatActor.Notify_Teleported();
                ArmStructureRifle("FMJ");
                EnterCombatActor();
                NextStructureStage();
                return false;
            }

            Require(DateTime.UtcNow < structureDeadline, $"CE structure stage {structureStage} timed out. " +
                $"launches={CeCompat.FpsLaunched - structureLaunches}, contacts={CeCompat.FpsContacts - structureContacts}, gate={CeCompat.LastGate}.");
            if (FirstPersonTransition.Active) return false;
            switch (structureStage)
            {
                case 1:
                case 3:
                    if (!Bridge.CeControlsAllowed || FirstPerson.Pawn != combatActor) return false;
                    AimCombatActor(FloorSpace.Spot(structureRear) + new V3(0f, 1.15f, 0f));
                    CeCompat.Frame(combatActor, combatActor.equipment.PrimaryEq.PrimaryVerb, true, true);
                    Require(CeCompat.FpsLaunched == structureLaunches + 1, "Structure rifle failed to launch one native CE bullet: " + CeCompat.LastGate);
                    NextStructureStage(false);
                    return false;
                case 2:
                    if (CeCompat.FpsContacts == structureContacts) return false;
                    Require(structureFront.HitPoints < structureFrontHp && structureRear.HitPoints == structureRearHp,
                        $"FMJ should damage the 10 mm wood and stop. Front HP {structureFrontHp}->{structureFront.HitPoints}, rear HP {structureRearHp}->{structureRear.HitPoints}.");
                    Report("ce-structure-low-penetration-stopped", new { ammo = "556x45mmNATO_FMJ", nominalMm = 6, woodMm = 10,
                        frontHpBefore = structureFrontHp, frontHpAfter = structureFront.HitPoints, rearHp = structureRear.HitPoints });
                    structureFrontHp = structureFront.HitPoints;
                    ArmStructureRifle("AP");
                    NextStructureStage();
                    return false;
                case 4:
                    if (CeCompat.FpsContacts == structureContacts) return false;
                    Require(structureFront.HitPoints < structureFrontHp && structureRear.HitPoints < structureRearHp,
                        $"AP should damage the 10 mm wood and the wall behind it. Front HP {structureFrontHp}->{structureFront.HitPoints}, rear HP {structureRearHp}->{structureRear.HitPoints}.");
                    Report("ce-structure-high-penetration-passed", new { ammo = "556x45mmNATO_AP", nominalMm = 12, woodMm = 10,
                        frontHpBefore = structureFrontHp, frontHpAfter = structureFront.HitPoints,
                        rearHpBefore = structureRearHp, rearHpAfter = structureRear.HitPoints });
                    FirstPerson.Leave(null);
                    structureFront.Destroy(DestroyMode.Vanish);
                    structureRear.Destroy(DestroyMode.Vanish);
                    structureRoof = combatOrigin + new IntVec3(0, 0, 22);
                    Require(!FloorSpace.HasFloorAt(map, FloorSpace.CellAbove(map, structureRoof)), "Test roof has a ProjectZ floor above it.");
                    SpawnStructureWall(map, structureRoof + IntVec3.East);
                    map.roofGrid.SetRoof(structureRoof, RoofDefOf.RoofConstructed);
                    RoofShape.Forget();
                    // Terminal roof contact in this fixture: 200 mm is above the installed 30 mm AP round.
                    FpsMod.Settings.penetrationArmorWood = ObstacleArmor.Maximum;
                    SpawnCombatVehicle(map, "VVE_Mosquito");
                    NextStructureStage();
                    return false;
                case 5:
                    if (!VehicleControl.Captured) return false;
                    FireStructureRoof();
                    NextStructureStage(false);
                    return false;
                case 6:
                    if (CeCompat.FpsContacts == structureContacts) return false;
                    Require(map.roofGrid.RoofAt(structureRoof) == RoofDefOf.RoofConstructed,
                        "Constructed test roof should survive the first direct nonexplosive impact.");
                    FireStructureRoof();
                    NextStructureStage(false);
                    return false;
                default:
                    if (CeCompat.FpsContacts < structureContacts + 2) return false;
                    Require(map.roofGrid.RoofAt(structureRoof) == null, "Constructed roof survived two direct airborne AP contacts.");
                    Report("ce-airborne-constructed-roof-collapsed", new { cell = structureRoof.ToString(), vehicle = combatVehicle.ThingID,
                        type = RoofDefOf.RoofConstructed.defName, survivedFirst = true, collapsedSecond = true,
                        launches = CeCompat.FpsLaunched - structureLaunches, contacts = CeCompat.FpsContacts - structureContacts });
                    FpsMod.Settings.penetrationArmorWood = structureOriginalWood;
                    DespawnCombatVehicle();
                    NextStructureStage();
                    return true;
            }
        }

        private static Thing SpawnStructureWall(Map map, IntVec3 cell) =>
            GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog), cell, map);

        private static void ArmStructureRifle(string type)
        {
            ArmCombatActor("Gun_AssaultRifle");
            Verb rifle = combatActor.equipment.PrimaryEq.PrimaryVerb;
            object modes = VehicleApi.Get(rifle, "CompFireModes");
            var mode = AccessTools.Property(modes.GetType(), "CurrentFireMode");
            mode.SetValue(modes, Enum.Parse(mode.PropertyType, "SingleFire"));
            object ammo = combatActor.equipment.Primary.AllComps.FirstOrDefault(c => c.GetType().FullName == "CombatExtended.CompAmmoUser");
            Require(ammo != null, "The CE assault rifle has no ammo component.");
            ThingDef load = ThingDef.Named("Ammo_556x45mmNATO_" + type);
            AccessTools.Method(ammo.GetType(), "ResetAmmoCount").Invoke(ammo, new object[] { load });
            Require(CeCompat.ProjectileFor(rifle)?.defName == "Bullet_556x45mmNATO_" + type,
                "The CE assault rifle selected unexpected ammo for the structure fixture.");
        }

        private void FireStructureRoof()
        {
            Require(AerialFlight.TryGet(combatVehicle, out var flight), "The roof fixture Mosquito has no flight state.");
            var motor = flight.Motor;
            motor.Position.Y = flight.HomeHeight + 8f;
            motor.Velocity = default;
            flight.Motor = motor;
            flight.Airborne = true;
            flight.EngineStopped = false;
            object gun = VehicleApi.Turrets(combatVehicle).Single(t => VehicleApi.TurretKey(t) == "VTOL_Loadout_0_Cannon30");
            var ammo = (ThingFilter)VehicleApi.Get(VehicleApi.Get(gun, "def"), "ammunition");
            VehicleApi.Set(gun, "loadedAmmo", ammo.AllowedThingDefs.First(a => a.defName.EndsWith("_AP", StringComparison.Ordinal)));
            VehicleApi.Set(gun, "shellCount", 4);
            VehicleApi.Set(gun, "reloadTicks", 0);
            ThingDef round = VehicleWeapons.ProjectileFor(gun);
            Require(round != null && !CeCompat.IsExplosiveDef(round), "Roof fixture should fire nonexplosive 30 mm AP.");
            Vector3 roofCenter = structureRoof.ToVector3Shifted();
            roofCenter.y = RoofShape.Covers(combatVehicle.Map, structureRoof)
                ? RoofShape.HeightAt(combatVehicle.Map, roofCenter.x, roofCenter.z) : View3D.WallHeight;
            AimCombatVehicle(gun, roofCenter);
            int before = CeCompat.FpsLaunched;
            bool fired = (bool)AccessTools.Method(typeof(VehicleWeapons), "Fire").Invoke(null, new[] { gun });
            Require(fired && CeCompat.FpsLaunched == before + 1 && VehicleApi.Int(gun, "shellCount") == 3,
                "Airborne Mosquito failed to launch one native 30 mm AP shell.");
        }

        private void NextStructureStage(bool capture = true)
        {
            structureStage++;
            structureDeadline = DateTime.UtcNow.AddSeconds(25);
            if (!capture) return;
            structureLaunches = CeCompat.FpsLaunched;
            structureContacts = CeCompat.FpsContacts;
        }
    }
}
