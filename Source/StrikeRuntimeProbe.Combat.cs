using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace HSK.FpsBridge
{
    public partial class StrikeRuntimeProbe
    {
        private static Pawn combatActor, combatVehicle;
        private static Thing combatTarget;
        private static IntVec3 combatOrigin;
        private static int combatStage, combatShotTick, combatHp, combatAttempts;
        private static float combatDamage, combatOriginalWood;
        private static DateTime combatDeadline;
        private static (int launches, int moves, int draws, int contacts, int structureContacts) combatBefore;

        internal static bool StepCombat(Map map)
        {
            if (combatStage == 0)
            {
                PrepareCombatRange(map);
                combatTarget = GenSpawn.Spawn(PawnGenerator.GeneratePawn(PawnKindDef.Named("Muffalo")),
                    combatOrigin + IntVec3.North, map, Rot4.South);
                ArmCombatActor("MeleeWeapon_Knife");
                EnterCombatActor();
                NextCombatStage();
                return false;
            }
            if (combatStage == 7) return true;
            if (DateTime.UtcNow > combatDeadline)
                throw new TimeoutException($"Combat stage {combatStage}: target={combatTarget?.ThingID}, dealt={combatDamage}, " +
                    $"last CE gate={CeCompat.LastGate}, counters={CombatCounters()}, before={combatBefore}, melee={Melee.Status}");
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            if (FirstPersonTransition.Active) return false;

            if (combatStage == 1)
            {
                if (!Bridge.Active || FirstPerson.Pawn != combatActor) return false;
                if (combatDamage <= 0)
                {
                    if (combatAttempts > 0 && Find.TickManager.TicksGame - combatShotTick < 36) return false;
                    NeedCombat(++combatAttempts <= 12, "First-person knife failed to injure the test animal: " + Melee.Status);
                    Pawn target = (Pawn)combatTarget;
                    target.jobs.StopAll();
                    target.Position = combatOrigin + IntVec3.North;
                    target.Notify_Teleported();
                    var parts = new List<HitVolume>();
                    BodyRegions.AddVolumes(target, parts);
                    HitVolume torso = parts.First(p => p.Region == HitRegion.Torso);
                    AimCombatActor(torso.Kind == VolumeKind.Capsule ? (torso.A + torso.B) * .5f : torso.A);
                    Verb knife = combatActor.equipment.PrimaryEq.PrimaryVerb;
                    NeedCombat(knife.GetType().FullName.StartsWith("CombatExtended.", StringComparison.Ordinal), "Knife is missing its CE verb");
                    Melee.Reset();
                    combatShotTick = Find.TickManager.TicksGame;
                    Melee.Frame(combatActor, knife, true, false, false);
                    return false;
                }
                Report("first-person-ce-melee", new { weapon = combatActor.equipment.Primary.def.defName,
                    target = combatTarget.def.defName, damageDealt = combatDamage, attempts = combatAttempts, status = Melee.Status });
                combatTarget.Destroy(DestroyMode.Vanish);
                ArmCombatActor("Gun_AssaultRifle");
                var rifle = combatActor.equipment.PrimaryEq.PrimaryVerb;
                var modes = VehicleApi.Get(rifle, "CompFireModes");
                var mode = AccessTools.Property(modes.GetType(), "CurrentFireMode");
                mode.SetValue(modes, Enum.Parse(mode.PropertyType, "SingleFire"));
                SpawnCombatWall(map, 42);
                NextCombatStage();
                return false;
            }

            if (combatStage == 2)
            {
                if (!Bridge.CeControlsAllowed) return false;
                Verb rifle = combatActor.equipment.PrimaryEq.PrimaryVerb;
                AimCombatActor(FloorSpace.Spot(combatTarget) + new V3(0, 1.15f, 0));
                CeCompat.Frame(combatActor, rifle, true, true);
                NeedCombat(CeCompat.FpsLaunched > combatBefore.launches, "CE rifle failed to launch: " + CeCompat.LastGate);
                NextCombatStage(false);
                return false;
            }

            if (combatStage == 3)
            {
                if (!RecordCombatImpact("first-person-ce-rifle")) return false;
                FirstPerson.Leave(null);
                // Keep these single-shot damage fixtures in one lane. Dedicated structure cases test traversal.
                combatOriginalWood = FpsMod.Settings.penetrationArmorWood;
                FpsMod.Settings.penetrationArmorWood = ObstacleArmor.Maximum;
                SpawnCombatVehicle(map, "VVE_Bulldog");
                SpawnCombatWall(map, 27);
                NextCombatStage();
                return false;
            }

            if (combatStage == 4)
            {
                if (!VehicleControl.Captured) return false;
                FireCombatVehicle("mainTurret");
                NextCombatStage(false);
                return false;
            }

            if (combatStage == 5)
            {
                if (!RecordCombatImpact("first-person-ce-bulldog")) return false;
                DespawnCombatVehicle();
                SpawnCombatVehicle(map, "VVE_Mosquito");
                SpawnCombatWall(map, 29);
                NextCombatStage();
                return false;
            }

            if (combatStage == 6)
            {
                if (!VehicleControl.Captured) return false;
                if (CeCompat.FpsLaunched == combatBefore.launches)
                {
                    NeedCombat(AerialFlight.TryGet(combatVehicle, out var flight), "Mosquito has no flight state");
                    var motor = flight.Motor;
                    motor.Position.Y = flight.HomeHeight + 8;
                    motor.Velocity = default;
                    flight.Motor = motor;
                    flight.Airborne = true;
                    flight.EngineStopped = false;
                    FireCombatVehicle("VTOL_Loadout_0_Cannon30");
                }
                if (!RecordCombatImpact("first-person-ce-mosquito-airborne")) return false;
                FpsMod.Settings.penetrationArmorWood = combatOriginalWood;
                DespawnCombatVehicle();
                combatStage = 7;
                return true;
            }
            throw new InvalidOperationException("Unexpected combat probe stage: " + combatStage);
        }

        private static void PrepareCombatRange(Map map)
        {
            NeedCombat(CeCompat.Ready && VehicleApi.Ready, "Full CE and Vehicle Framework are required for combat probe");
            combatActor = map.mapPawns.FreeColonistsSpawned.First(p => !p.Downed && !p.WorkTagIsDisabled(WorkTags.Violent)
                && !p.WorkTagIsDisabled(WorkTags.Shooting));
            combatOrigin = new IntVec3(map.Size.x / 2, 0, FloorSpace.Bands(map)[0].Rows / 2 - 24);
            foreach (IntVec3 c in new CellRect(combatOrigin.x - 7, combatOrigin.z - 6, 15, 53).Cells)
            {
                NeedCombat(c.InBounds(map), "Combat fixture does not fit the map");
                foreach (Thing t in c.GetThingList(map).ToArray())
                {
                    if ((t is Plant || t is Building) && t.def.destroyable) t.Destroy(DestroyMode.Vanish);
                    // The copied range has parked vehicles between the firing point and the wall.
                    else if (t is Pawn p && p != combatActor && p.Spawned) p.DeSpawn();
                }
                map.roofGrid.SetRoof(c, null);
                map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                map.fogGrid.Unfog(c);
            }
            combatActor.jobs.StopAll();
            combatActor.Position = combatOrigin;
            combatActor.Notify_Teleported();
            combatActor.skills?.GetSkill(SkillDefOf.Melee)?.Learn(50000, true);
            var observe = AccessTools.Method(typeof(StrikeRuntimeProbe), nameof(ObserveCombatDamage));
            new Harmony("hsk.strike.runtime.combat").Patch(AccessTools.Method(typeof(Thing), nameof(Thing.TakeDamage), new[] { typeof(DamageInfo) }),
                postfix: new HarmonyMethod(observe));
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
        }

        private static void ObserveCombatDamage(Thing __instance, DamageInfo dinfo, DamageWorker.DamageResult __result)
        {
            if (__instance == combatTarget && (dinfo.Instigator == combatActor || dinfo.Instigator == combatVehicle))
                combatDamage += __result?.totalDamageDealt ?? 0f;
        }

        private static void ArmCombatActor(string defName)
        {
            combatActor.jobs.StopAll();
            combatActor.stances.SetStance(new Stance_Mobile());
            combatActor.equipment.DestroyAllEquipment();
            var weapon = (ThingWithComps)ThingMaker.MakeThing(ThingDef.Named(defName),
                ThingDef.Named(defName).MadeFromStuff ? ThingDefOf.Steel : null);
            combatActor.equipment.AddEquipment(weapon);
            object ammo = weapon.AllComps.FirstOrDefault(c => c.GetType().FullName == "CombatExtended.CompAmmoUser");
            if (ammo == null) return;
            object selected = VehicleApi.Get(ammo, "SelectedAmmo") ?? VehicleApi.Get(ammo, "CurrentAmmo");
            if (selected == null)
                selected = VehicleApi.List(VehicleApi.Get(ammo, "CurAmmoSet"), "ammoTypes").Select(link => VehicleApi.Get(link, "ammo")).First();
            AccessTools.Method(ammo.GetType(), "ResetAmmoCount").Invoke(ammo, new[] { selected });
        }

        private static void EnterCombatActor()
        {
            Find.Selector.ClearSelection();
            Find.Selector.Select(combatActor, false, false);
            FirstPerson.Toggle();
        }

        private static void AimCombatActor(V3 target)
        {
            V3 d = target - Ballistics.Eye(combatActor);
            float yaw = Mathf.Atan2(d.X, d.Z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Atan2(d.Y, new Vector2(d.X, d.Z).magnitude) * Mathf.Rad2Deg;
            FirstPerson.Look(Mathf.DeltaAngle(FirstPerson.Yaw, yaw), Mathf.DeltaAngle(FirstPerson.Pitch, pitch));
        }

        private static void SpawnCombatWall(Map map, int distance)
        {
            if (combatTarget is { Destroyed: false }) combatTarget.Destroy(DestroyMode.Vanish);
            combatTarget = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog),
                combatOrigin + new IntVec3(0, 0, distance), map);
            combatHp = combatTarget.HitPoints;
            combatDamage = 0;
        }

        private static void SpawnCombatVehicle(Map map, string name, Action<Pawn> configure = null, string role = null)
        {
            ThingDef def = ThingDef.Named(name);
            var generate = AccessTools.Method(AccessTools.TypeByName("Vehicles.VehicleSpawner"), "GenerateVehicle", new[] { def.GetType(), typeof(Faction) });
            combatVehicle = (Pawn)generate.Invoke(null, new object[] { def, Faction.OfPlayer });
            GenSpawn.Spawn(combatVehicle, combatOrigin, map, Rot4.North);
            if (name == "VVE_Mosquito")
            {
                object tree = VehicleApi.Get(combatVehicle, "CompUpgradeTree");
                object treeDef = VehicleApi.Get(VehicleApi.Get(tree, "Props"), "def");
                object node = VehicleApi.Call(treeDef, "GetNode", "VTOL_Weapon_VTOL_Loadout_0_Cannon30");
                foreach (object upgrade in VehicleApi.List(node, "upgrades").Where(u => u.GetType().Name == "TurretUpgrade"))
                    VehicleApi.Call(upgrade, "Unlock", combatVehicle, false);
                AerialDebugActions.Supply(combatVehicle);
            }
            configure?.Invoke(combatVehicle);
            combatActor.jobs.StopAll();
            object seat = VehicleApi.Handlers(combatVehicle).First(h => role == null
                ? VehicleApi.Movement(h) : VehicleApi.RoleKey(h) == role);
            NeedCombat(VehicleApi.Board(combatVehicle, combatActor, seat), "Native boarding failed: " + name);
            Find.Selector.ClearSelection();
            Find.Selector.Select(combatVehicle, false, false);
            NeedCombat(VehicleControl.TrySelected() && VehicleControl.Owns(combatVehicle), "Could not enter vehicle control: " + name);
        }

        private static void DespawnCombatVehicle()
        {
            VehicleControl.End();
            VehicleApi.Call(combatVehicle, "DisembarkPawn", combatActor);
            combatVehicle.Destroy(DestroyMode.Vanish);
            combatVehicle = null;
        }

        private static void FireCombatVehicle(string key)
        {
            object gun = VehicleApi.Turrets(combatVehicle).Single(t => VehicleApi.TurretKey(t) == key);
            object def = VehicleApi.Get(gun, "def");
            var ammunition = ((ThingFilter)VehicleApi.Get(def, "ammunition")).AllowedThingDefs;
            ThingDef ammo = ammunition.FirstOrDefault(a => a.defName.EndsWith("_AP", StringComparison.Ordinal)) ?? ammunition.First();
            VehicleApi.Set(gun, "loadedAmmo", ammo);
            VehicleApi.Set(gun, "shellCount", 4);
            VehicleApi.Set(gun, "reloadTicks", 0);
            AimCombatVehicle(gun, VehicleModels.U(FloorSpace.Spot(combatTarget)) + Vector3.up * 1.1f);
            bool fired = (bool)AccessTools.Method(typeof(VehicleWeapons), "Fire").Invoke(null, new[] { gun });
            NeedCombat(fired && CeCompat.FpsLaunched > combatBefore.launches && VehicleApi.Int(gun, "shellCount") == 3,
                "Vehicle native shot did not launch and consume CE ammunition: " + key + "/" + ammo.defName);
        }

        private static void AimCombatVehicle(object gun, Vector3 target)
        {
            var pitches = (Dictionary<object, float>)AccessTools.Field(typeof(VehicleWeapons), "pitches").GetValue(null);
            for (int i = 0; i < 4; i++)
            {
                Vector3 d = target - VehicleModels.Muzzle(combatVehicle, gun);
                float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                VehicleApi.Set(gun, "TurretRotation", yaw);
                ThingDef projectile = VehicleWeapons.ProjectileFor(gun);
                float nativeSpeed = VehicleApi.Float(VehicleApi.Get(gun, "def"), "projectileSpeed");
                float speed = ViewRules.Speed(nativeSpeed > 0 ? nativeSpeed / 60f : projectile.projectile.SpeedTilesPerTick, FpsMod.Settings.speed);
                float gravity = ViewRules.Gravity(FpsMod.Settings.gravity);
                float range = new Vector2(d.x, d.z).magnitude;
                float best = 0f, error = float.PositiveInfinity;
                for (float pitch = -65f; pitch <= 70f; pitch += .1f)
                {
                    float angle = pitch * Mathf.Deg2Rad;
                    float time = range / (speed * Mathf.Cos(angle));
                    if (time > 240f) continue;
                    float difference = Mathf.Abs(-speed * Mathf.Sin(angle) * time - gravity * time * (time + 1f) * .5f - d.y);
                    if (difference < error) { error = difference; best = pitch; }
                }
                pitches[gun] = best;
                VehicleControl.SetLook(yaw, best);
            }
        }

        private static bool RecordCombatImpact(string name)
        {
            if (combatDamage <= 0 || combatTarget is { Destroyed: false } && combatTarget.HitPoints >= combatHp) return false;
            var now = CombatCounters();
            int launches = now.launches - combatBefore.launches, moves = now.moves - combatBefore.moves;
            int draws = now.draws - combatBefore.draws, contacts = now.contacts - combatBefore.contacts;
            int structureContacts = now.structureContacts - combatBefore.structureContacts;
            int hpAfter = combatTarget.Destroyed ? 0 : combatTarget.HitPoints;
            NeedCombat(launches > 0 && moves > 0 && draws > 0 && contacts + structureContacts > 0 && combatDamage > 0,
                $"{name}: hit points {combatHp}->{hpAfter}, damage={combatDamage}, launches={launches}, moves={moves}, draws={draws}, contacts={contacts}, structureContacts={structureContacts}");
            Report(name, new { damageDealt = combatDamage, hpBefore = combatHp, hpAfter, launches, moves, draws, contacts, structureContacts });
            return true;
        }

        private static (int launches, int moves, int draws, int contacts, int structureContacts) CombatCounters() =>
            (CeCompat.FpsLaunched, CeCompat.FpsMoved, CeCompat.FpsDraws, CeCompat.FpsContacts, CeCompat.FpsStructureContacts);

        private static void NextCombatStage(bool resetCounters = true)
        {
            combatStage++;
            combatDeadline = DateTime.UtcNow.AddSeconds(25);
            if (resetCounters) combatBefore = CombatCounters();
        }

        private static void NeedCombat(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException(reason);
        }
    }
}
