using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
namespace HSK.FpsBridge
{
    internal static partial class CeCompat
    {
        private sealed class NativeAirThreat
        {
            internal Map Map;
            internal V3 Origin;
        }

        private static readonly ConditionalWeakTable<Thing, NativeAirThreat> nativeAirThreats = new();

        private static void RememberNativeAirThreat(Thing round)
        {
            if (!round.Spawned || IsExplosiveDef(round.def) || nativeAirThreats.TryGetValue(round, out _)) return;
            if (VehicleApi.Get(round, "launcher") is not Thing launcher
                || launcher is Pawn aircraft && AerialFlight.Airborne(aircraft)) return;
            if (VehicleApi.Get(round, "intendedTarget") is not LocalTargetInfo intended
                || !AerialCombat.Target(intended, out _)) return;
            V3 origin = NativeWorld(round.Map, vector3.Read3(exactPos.GetValue(round)));
            nativeAirThreats.Add(round, new NativeAirThreat { Map = round.Map, Origin = origin });
        }

        internal static void DrawNativeAirThreats(Map map)
        {
            if (!Ready || !View3D.Active || map == null) return;
            foreach (var entry in nativeAirThreats)
            {
                var round = entry.Key;
                var flight = entry.Value;
                if (flight.Map != map || round.Map != map || !round.Spawned || (bool)ceLanded.GetValue(round)) continue;
                V3 point = NativeWorld(map, vector3.Read3(exactPos.GetValue(round)));
                V3 previous = NativeWorld(map, vector3.Read3(exactLastPos.GetValue(round)));
                IntVec3 cell = FloorSpace.Cell(map, point);
                if (!cell.InBounds(map) || cell.Fogged(map)) continue;
                Visuals.Tracer(point, point - previous, Math.Min(5f, (point - flight.Origin).Length), Vector3.zero, FpsMod.Settings.groundToAirTracer);
            }
        }

        [ThreadStatic] private static Building_Turret nativeFiringTurret;

        internal static void BeginNativeTurretShot(Verb __instance, out Building_Turret __state)
        {
            __state = nativeFiringTurret;
            nativeFiringTurret = __instance.Caster as Building_Turret;
        }

        internal static void EndNativeTurretShot(Building_Turret __state) => nativeFiringTurret = __state;

        private static bool ObserveNativeTurretRound(Thing round)
        {
            if (!FpsMod.Settings.turretTracers || !round.Spawned || (bool)ceLanded.GetValue(round)) return false;
            Thing launcher = nativeFiringTurret ?? VehicleApi.Get(round, "launcher") as Thing;
            if (!TurretTracers.IsMount(launcher)) return false;
            var weapon = VehicleApi.Get(round, "equipmentDef") as ThingDef;
            var target = VehicleApi.Get(round, "intendedTarget") is LocalTargetInfo intended ? intended : LocalTargetInfo.Invalid;
            return TurretTracers.ObserveCe(round, launcher, weapon, NativeWorld(round.Map, vector3.Read3(exactPos.GetValue(round))), target);
        }

        internal static bool OwnsTurretTracerFlight(Thing round) => traces.TryGetValue(round, out _) || (bool)ceLanded.GetValue(round);

        internal static bool NativeTurretTracerPosition(Thing round, out V3 point)
        {
            point = NativeWorld(round.Map, vector3.Read3(exactPos.GetValue(round)));
            return point.Finite;
        }

        internal static bool IsInstantRound(ThingDef round) =>
            round?.projectile != null && AccessTools.Field(round.projectile.GetType(), "isInstant")?.GetValue(round.projectile) is true;

        private static bool AdoptTurretAirThreat(Thing projectile)
        {
            var turret = nativeFiringTurret ?? VehicleApi.Get(projectile, "launcher") as Building_Turret;
            if (turret == null || !projectile.Spawned || projectile.Destroyed || traces.TryGetValue(projectile, out _)
                || (!AerialTurretTargeting.DirectTurret(turret.AttackVerb) && !AerialProximityFuse.IsArmed(projectile))) return false;
            var intended = VehicleApi.Get(projectile, "intendedTarget") is LocalTargetInfo info && info.IsValid
                ? info : turret.AttackVerb.CurrentTarget;
            if (!AerialCombat.Target(intended, out var target)) return false;
            V3 point = VehicleModels.V(AerialTargetLock.Point(target));
            V3 start = VehicleModels.V(AerialCombat.Origin(turret));
            V3 bore = AerialTurretTargeting.TryAim(turret, out var planned) ? planned : point-start;
            start = AerialTurretFire.Muzzle(turret, bore);
            float speed = ViewRules.Speed(ActualLaunchSpeed(projectile), FpsMod.Settings.speed);
            if (!AerialCombat.Intercept(target, start, point, speed, FpsMod.Settings.antiAirLead, out var velocity))
            {
                V3 delta = point - start;
                velocity = delta * (speed / Math.Max(.001f, delta.Length));
            }
            var band = FloorSpace.Band(projectile.Map, turret.Position);
            Pawn operatorPawn = AerialTurretFire.Operator(turret) ?? VehicleApi.Get(projectile, "launcher") as Pawn;
            traces.Add(projectile, new Trace
            {
                map = projectile.Map, launcher = turret, turretOperator = operatorPawn, pos = start, previous = start, velocity = velocity,
                contacts = new Ballistics.ExternalTrace(turret, operatorPawn), gravity = ViewRules.Gravity(FpsMod.Settings.gravity),
                structure = StructureFor(projectile, turret.def.defName, turret.AttackVerb.verbProps.burstShotCount),
                nativeHeight = band.Height, nativeShift = band.Shift, nativeLevel = band.Level,
                nativeUnitsVersion = 1, lastTick = Find.TickManager.TicksGame
            });
            Vector3 nativeStart=WorldPointToNative(projectile.Map,start,band);
            exactPos.SetValue(projectile,vector3.Make3(nativeStart));
            exactLastPos.SetValue(projectile,vector3.Make3(nativeStart));
            V3 nativeVelocity=WorldVelocityToNative(velocity);
            if(CeNativeRules.TryAngles(nativeVelocity.X,nativeVelocity.Y,nativeVelocity.Z,out float elevation,out float rotation))
            {
                AccessTools.Field(projectileType,"shotAngle")?.SetValue(projectile,elevation);
                AccessTools.Field(projectileType,"shotRotation")?.SetValue(projectile,rotation);
            }
            AerialTurretFire.Record(turret, start, velocity);
            FpsLaunched++;
            CombatDiagnostics.CeLaunch(projectile, turret, start);
            return true;
        }

        private static void ArmProximity(Thing round)
        {
            if (!AerialProximityFuse.Airburst(round.def)) return;
            Thing launcher = scope?.launcher ?? nativeFiringTurret ?? VehicleApi.Get(round, "launcher") as Thing;
            Thing equipment = scope?.equipment ?? nativeFiringTurret?.AttackVerb?.EquipmentSource;
            var target = VehicleApi.Get(round, "intendedTarget") is LocalTargetInfo intended ? intended : LocalTargetInfo.Invalid;
            if (!target.HasThing)
            {
                if (nativeFiringTurret != null) target = nativeFiringTurret.AttackVerb.CurrentTarget;
                else if ((VehicleWeapons.Firing ?? VehicleCeSpeed.Current) is object gun
                    && VehicleApi.Get(gun, "targetInfo") is LocalTargetInfo gunTarget) target = gunTarget;
                else if (scope?.verb != null) target = scope.verb.CurrentTarget;
            }
            AerialProximityFuse.Arm(round, launcher, equipment, target);
        }

        private static bool AdoptProximityFlight(Thing round)
        {
            if (!AerialProximityFuse.IsArmed(round) || !round.Spawned || traces.TryGetValue(round,out _)
                || VehicleApi.Get(round,"launcher") is not Thing launcher || launcher.Map != round.Map) return false;
            var band=FloorSpace.Band(round.Map,launcher.Position);
            V3 start=NativePointToWorld(round.Map,vector3.Read3(exactPos.GetValue(round)),band);
            traces.Add(round,new Trace
            {
                map=round.Map,launcher=launcher,pos=start,previous=start,velocity=ReadNativeVelocity(round),
                nativePhysics=true,contacts=new Ballistics.ExternalTrace(launcher),nativeLevel=band.Level,
                nativeShift=band.Shift,nativeHeight=band.Height,nativeUnitsVersion=1,lastTick=Find.TickManager.TicksGame
            });
            return true;
        }

        internal static void AimAtAircraft(Verb verb)
        {
            if(!AerialCombat.Target(verb.CurrentTarget,out var target)||verb.Caster==null||AerialTurretTargeting.DirectTurret(verb))return;
            V3 start=VehicleModels.V(AerialCombat.Origin(verb.Caster));
            V3 point=VehicleModels.V(AerialTargetLock.Point(target));
            float speed=VehicleApi.Float(verb,"ShotSpeed")/60f;
            if(speed<=0)speed=(projectileDef.GetValue(verb) as ThingDef)?.projectile?.SpeedTilesPerTick??1f;
            float gravity=ViewRules.Gravity(FpsMod.Settings.gravity);
            V3 aim=AerialCombat.LeadPoint(target,start,point,speed,gravity,FpsMod.Settings.antiAirLead);
            if(!AerialShotRules.Velocity(aim-start,speed,gravity,out V3 velocity))velocity=aim-start;
            AerialTurretPitch.Record(verb.Caster,velocity);
            if(CeNativeRules.TryAngles(velocity.X,velocity.Y,velocity.Z,out float elevation,out float rotation))
            {shotAngle.SetValue(verb,elevation);shotRotation.SetValue(verb,rotation);}
        }
        internal static V3 NativeWorld(Map map,Vector3 p)
        {FloorBand b=FloorSpace.Band(map,new IntVec3(p));return NativePointToWorld(map,p,b);}
        private static bool KeepNativeImpactFleck(Thing hitThing) => !View3D.Active
            || hitThing is not Pawn aircraft || !AerialFlight.Airborne(aircraft);

        public static bool AircraftCollision(Thing __instance,Thing thing,ref bool __result)
        {
            if(!(thing is Pawn plane)||!AerialFlight.Airborne(plane)||OwnFlight(__instance,out _))return true;
            var from=NativeWorld(__instance.Map,vector3.Read3(exactLastPos.GetValue(__instance)));
            var to=NativeWorld(__instance.Map,vector3.Read3(exactPos.GetValue(__instance)));
            var shapes=new List<HitVolume>();AerialGeometry.Volumes(plane,shapes);float fraction=float.PositiveInfinity;
            foreach(var shape in shapes)if(HitMath.Enter(from,to,shape,out float f))fraction=Math.Min(fraction,f);
            __result=false;if(!float.IsFinite(fraction))return false;
            if(ReferenceEquals(VehicleApi.Get(__instance,"launcher"),plane))return false;
            if((bool)ceBlockerContact.Invoke(null,new object[]{__instance,plane})){__result=true;return false;}
            V3 at=V3.Lerp(from,to,fraction);AerialBlast.Remember(__instance,__instance.Map,at);
            exactPos.SetValue(__instance,vector3.Make3(ToNative(__instance.Map,at,plane)));
            ceLanded.SetValue(__instance,true);
            Visuals.ImpactLight(__instance.Map, at, plane);
            using (VehicleBallisticDamage.Begin(__instance, plane, VehicleApi.Get(__instance, "launcher") as Thing, at, to - from))
                ceImpact.Invoke(__instance,new object[]{plane});
            __result=true;return false;
        }
    }
}
