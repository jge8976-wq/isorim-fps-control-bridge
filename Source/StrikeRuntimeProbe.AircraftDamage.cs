using System;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HSK.FpsBridge
{
    public sealed partial class StrikeRuntimeProbe
    {
        private Pawn aircraftDamageTarget;
        private int aircraftDamageStage;
        private float aircraftDamageBefore, aircraftDamageMinimum, aircraftDamageMaximum;
        private int aircraftDamageLaunches, aircraftDamageContacts;
        private DateTime aircraftDamageDeadline;

        private bool StepAircraftDamage(Map map)
        {
            if (aircraftDamageStage >= 6) return true;
            bool airborne = aircraftDamageStage >= 3;
            switch (aircraftDamageStage % 3)
            {
                case 0:
                    if (combatTarget != null && combatTarget.Spawned) combatTarget.Destroy();
                    SpawnCombatVehicle(map, "VVE_Bulldog");
                    aircraftDamageTarget = SpawnAircraftDamageTarget(map, airborne);
                    combatTarget = aircraftDamageTarget;
                    var (hp, maximum) = AircraftDamage.ComponentHealth(aircraftDamageTarget);
                    aircraftDamageBefore = aircraftDamageMinimum = hp;
                    aircraftDamageMaximum = maximum;
                    aircraftDamageDeadline = DateTime.UtcNow.AddSeconds(20);
                    aircraftDamageStage++;
                    return false;

                case 1:
                    Require(DateTime.UtcNow < aircraftDamageDeadline, "Aircraft damage fixture timed out entering Bulldog control.");
                    if (FirstPersonTransition.Active || !VehicleControl.Captured) return false;
                    if (airborne)
                    {
                        // Hold the real simulated aircraft at a clean 8-cell altitude for the shot.
                        Require(AerialFlight.TryGet(aircraftDamageTarget, out var flight) && flight.Airborne,
                            "The aircraft target lost its simulated flight before the shot.");
                        var motor = flight.Motor;
                        motor.Position.Y = flight.HomeHeight + 8f;
                        motor.Velocity = default;
                        flight.Motor = motor;
                    }
                    combatBefore = CombatCounters();
                    aircraftDamageLaunches = CeCompat.FpsLaunched;
                    aircraftDamageContacts = CeCompat.FpsContacts;
                    FireCombatVehicle("mainTurret");
                    aircraftDamageStage++;
                    return false;

                default:
                    var (remaining, _) = AircraftDamage.ComponentHealth(aircraftDamageTarget);
                    aircraftDamageMinimum = Math.Min(aircraftDamageMinimum, remaining);
                    if (aircraftDamageTarget.Spawned && !aircraftDamageTarget.Destroyed)
                    {
                        Require(DateTime.UtcNow < aircraftDamageDeadline,
                            $"Aircraft damage fixture failed to finish aircraft (airborne={airborne}; componentHp={aircraftDamageBefore}->{aircraftDamageMinimum}/{aircraftDamageMaximum}; contacts={CeCompat.FpsContacts - aircraftDamageContacts}).");
                        return false;
                    }
                    Require(aircraftDamageMinimum < aircraftDamageBefore
                        && CeCompat.FpsLaunched > aircraftDamageLaunches && CeCompat.FpsContacts > aircraftDamageContacts,
                        "Expected real CE projectile launch, contact and component HP loss before the aircraft was lost.");
                    Report(airborne ? "airborne-aircraft-terminal-damage" : "parked-aircraft-terminal-damage", new
                    {
                        vehicle = aircraftDamageTarget.ThingID,
                        hpBefore = aircraftDamageBefore,
                        minimumHp = aircraftDamageMinimum,
                        maximumHp = aircraftDamageMaximum,
                        destroyed = aircraftDamageTarget.Destroyed,
                        despawned = !aircraftDamageTarget.Spawned,
                        launches = CeCompat.FpsLaunched - aircraftDamageLaunches,
                        contacts = CeCompat.FpsContacts - aircraftDamageContacts,
                    });
                    DespawnCombatVehicle();
                    aircraftDamageStage++;
                    return aircraftDamageStage >= 6;
            }
        }

        private static Pawn SpawnAircraftDamageTarget(Map map, bool airborne)
        {
            ThingDef def = ThingDef.Named("VVE_Mosquito");
            var spawn = AccessTools.Method(AccessTools.TypeByName("Vehicles.VehicleSpawner"), "GenerateVehicle",
                new[] { def.GetType(), typeof(Faction) });
            Pawn plane = (Pawn)spawn.Invoke(null, new object[] { def, Faction.OfPlayer });
            GenSpawn.Spawn(plane, combatOrigin + new IntVec3(0, 0, 25), map, Rot4.North);
            object stats = VehicleApi.Get(plane, "statHandler");
            // Seed the same near-terminal state observed in the saved game. The real
            // shell must still remove native component HP and trigger native kill handling.
            foreach (object part in VehicleApi.List(stats, "components"))
                VehicleApi.Call(part, "SetHealth", VehicleApi.Float(part, "MaxHealth") * .018f);
            VehicleApi.Call(stats, "MarkAllDirty");
            if (airborne)
            {
                AerialMotor raised = VehicleGeometry.Pose(plane);
                float home = raised.Position.Y;
                raised.Position.Y += 8f;
                AerialFlight.Store.Flights[plane] = new AerialFlightRecord { Motor = raised, HomeHeight = home, Airborne = true };
            }
            return plane;
        }
    }
}
