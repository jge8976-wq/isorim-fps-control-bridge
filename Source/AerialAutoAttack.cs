using System;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialAutoAttack
    {
        internal static bool Automatic(Pawn vehicle) => AerialFlight.Airborne(vehicle) && !VehicleControl.Owns(vehicle);

        internal static bool Facing(Pawn vehicle, LocalTargetInfo target)
        {
            if (!AerialWeaponSolution.Valid(vehicle, target)) return false;
            var flight = AerialFlight.Store.Flights[vehicle];
            V3 delta = AerialWeaponSolution.Point(vehicle, target) - flight.Motor.Position;
            if (delta.X * delta.X + delta.Z * delta.Z <= 1f) return true;
            float yaw = flight.Motor.Yaw * Mathf.Deg2Rad;
            return delta.X * MathF.Sin(yaw) + delta.Z * MathF.Cos(yaw) >= -.001f;
        }

        internal static float PlanarDistance(V3 first, V3 second)
        {
            float x = first.X - second.X, z = first.Z - second.Z;
            return MathF.Sqrt(x * x + z * z);
        }

        internal static bool ProtectedUnit(Pawn pawn)
        {
            if (pawn.Dead || !(pawn.RaceProps.Humanlike || VehicleApi.IsVehicle(pawn)) || pawn.Faction == null) return false;
            return pawn.Faction == Faction.OfPlayer || pawn.Faction.RelationKindWith(Faction.OfPlayer) == FactionRelationKind.Ally;
        }

        internal static bool UnsafeExplosive(Pawn vehicle, object gun, LocalTargetInfo target)
        {
            if (vehicle.Faction != Faction.OfPlayer && vehicle.Faction?.RelationKindWith(Faction.OfPlayer) != FactionRelationKind.Ally) return false;
            if (!Automatic(vehicle) || !CeCompat.IsExplosiveDef(VehicleWeapons.ProjectileFor(gun)) || !target.IsValid) return false;
            V3 point = AerialWeaponSolution.Point(vehicle, target);
            return vehicle.Map.mapPawns.AllPawnsSpawned.Any(pawn => pawn != vehicle && ProtectedUnit(pawn) && NearTarget(pawn, point));
        }

        private static bool NearTarget(Pawn unit, V3 target)
        {
            if (!VehicleApi.IsVehicle(unit))
            {
                var unitFloor = FloorSpace.Band(unit.Map, unit.Position);
                return PlanarDistance(new V3(unit.Position.x + .5f, 0, unit.Position.z - unitFloor.Shift + .5f), target) <= 7.0001f;
            }
            var footprint = unit.OccupiedRect();
            var floor = FloorSpace.Band(unit.Map, unit.Position);
            float x = Math.Clamp(target.X, footprint.minX, footprint.maxX + 1f);
            float z = Math.Clamp(target.Z, footprint.minZ - floor.Shift, footprint.maxZ + 1f - floor.Shift);
            return PlanarDistance(new V3(x, 0, z), target) <= 7.0001f;
        }

        internal static bool InsidePatrol(Pawn vehicle, AerialFlightRecord flight, Thing target)
        {
            if (!flight.CombatOrbit) return true;
            var guns = VehicleApi.Turrets(vehicle);
            float radius = guns.Count == 0 ? 0 : guns.Max(AerialArmament.AiRange);
            return PlanarDistance(VehicleModels.V(AerialTargetLock.Point(target)), flight.NavTarget) <= radius;
        }

        internal static void TogglePatrol(Pawn vehicle, AerialFlightRecord flight)
        {
            flight.CombatOrbit = !flight.CombatOrbit;
            flight.CombatAttackRun = flight.CombatAim = false;
            flight.CombatGunKey = null;
            if (flight.CombatOrbit)
            {
                flight.CombatEnabled = true;
                flight.CombatTarget = null;
                if (!flight.Navigate) { flight.Cruise = false; AerialOrders.Hold(vehicle, flight); }
                flight.Status = L.T("盘旋自动攻击已开启", "Orbit auto-attack enabled");
            }
            else
            {
                flight.CombatEnabled = false;
                flight.CombatTarget = null;
                flight.Status = L.T("盘旋自动攻击已关闭", "Orbit auto-attack disabled");
            }
            foreach (var gun in VehicleApi.Turrets(vehicle))
            {
                AerialFireControl.Assign(gun, null);
                VehicleApi.Set(gun, "AutoTarget", false);
            }
            VehicleApi.ClearQueue(vehicle);
            AerialFireControl.Acknowledge(vehicle, flight.NavTarget);
        }
    }
}
