using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static partial class Ballistics
    {
        // Keep the native projectile object, ammo comp, damage and impact method; only its trajectory becomes 3D.
        internal static void AdoptVehicleRound(Projectile p, Pawn vehicle, V3 start, V3 direction, float nativeSpeed, int burst, object gun=null, Thing guidedTarget=null)
        {
            if (!Enabled || p == null || VehicleProjectileRules.NativeAreaFlight(p.GetType())
                || p.Map == null || p.Destroyed || !start.Finite || !direction.Finite) return;
            gun??=VehicleWeapons.Firing;
            float spread = AerialArmament.Data(gun)?.vanillaSpreadDegrees ?? 0f;
            if (spread > 0f)
            {
                float radius = MathF.Sqrt(Rand.Value) * spread;
                float angle = Rand.Value * 2f * MathF.PI;
                Vector3 bore = VehicleModels.U(direction);
                Vector3 offset = Quaternion.LookRotation(bore) * new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f);
                direction = VehicleModels.V((bore + offset * MathF.Tan(radius * Mathf.Deg2Rad)).normalized);
            }
            float speed = ViewRules.Speed(Math.Max(.01f, nativeSpeed), FpsMod.Settings.speed);
            float gravity = ViewRules.Gravity(FpsMod.Settings.gravity);
            V3 velocity = AerialArmament.ReleaseVelocity(vehicle,gun,direction,speed), far = Landing(p.Map, start, velocity, gravity);
            IntVec3 cell = FloorSpace.Cell(p.Map, far).ClampInsideMap(p.Map);
            FloorSpace.OwnFlight(p);
            originRef(p) = FloorSpace.Logical(p.Map, start, vehicle.Position);
            destinationRef(p) = FloorSpace.Logical(p.Map, far, cell);
            ticksRef(p) = Math.Max(1, Mathf.CeilToInt((far - start).Length / speed));
            var shot = new Shot { kind = Kind.Round, launcher = vehicle, pos = start, vel = velocity,
                gravity = gravity, startY = start.Y, muzzleY = start.Y, lastTick = Find.TickManager.TicksGame,
                airLaunched = AerialFlight.Airborne(vehicle) };
            shot.explosiveGun = shot.airLaunched && CeCompat.IsExplosiveDef(p.def) && AerialCombat.ExplosiveGunRound(vehicle, p.def, gun);
            // Explosive/custom impacts remain native. Standard bullets reuse exactly the infantry penetration walker.
            if (p.def.thingClass == typeof(Bullet) && p.def.projectile.explosionRadius <= 0f)
            {
                PenetrationGroup group = PenetrationRules.Classify(vehicle.def.defName, burst, true);
                shot.pen = new PenetrationState(group, p.DamageAmount, p.ArmorPenetration, false, FpsMod.Settings.ObstacleArmor);
                shot.envelopes = new Dictionary<long, HitVolume>();
            }
            if(AerialArmament.Data(gun)?.lockableMissile==true)
            {shot.gravity=0;shot.aerialTarget=guidedTarget??AerialTargetLock.Capture(gun);shot.aerialTurn=AerialArmament.Data(gun).guidanceDegreesPerSecond * FpsMod.Settings.missileTurn;}
            shots[p] = shot;
            p.Position = FloorSpace.Cell(p.Map, start).ClampInsideMap(p.Map);
        }
        // Vehicle pawns are not humanoids. Native vehicle damage selects armour/components from the real impact.
        private static void CollectVehicle(Pawn vehicle, V3 a, V3 b)
        {
            volumes.Clear(); VehicleModels.Volumes(vehicle, volumes);
            if (HitMath.Nearest(a, b, volumes, out float t) < 0) return;
            infos.Add(new ContactInfo { thing = vehicle, region = HitRegion.Solid });
            contacts.Add(new Contact { T = t, Kind = ContactKind.ObstacleEnter, Entity = vehicle.thingIDNumber,
                Obstacle = ObstacleClass.Barrier, Ref = infos.Count - 1 });
        }
    }
}
