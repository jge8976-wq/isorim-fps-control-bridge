using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Only aircraft in this opt-in flight simulation acquire a height-aware combat boundary.
    internal static class AerialCombat
    {
        internal static bool Target(LocalTargetInfo target, out Pawn plane)
        { plane=target.Thing as Pawn;return AerialFlight.Enabled&&AerialFlight.Airborne(plane); }

        internal static float TracerScale(Thing launcher, LocalTargetInfo target)
        {
            bool aircraftSource = launcher is Pawn source && AerialFlight.Airborne(source);
            bool aircraftTarget = Target(target, out _);
            if (aircraftSource == aircraftTarget) return 1f;
            return aircraftSource ? FpsMod.Settings.airToGroundTracer : FpsMod.Settings.groundToAirTracer;
        }

        internal static bool ExplosiveGunRound(Pawn aircraft, ThingDef projectile, object gun = null)
        {
            if (gun != null) return AerialArmament.QuickGun(gun);
            return VehicleApi.Turrets(aircraft).Any(candidate =>
                AerialArmament.QuickGun(candidate) && VehicleWeapons.ProjectileFor(candidate) == projectile);
        }

        internal static V3 LeadPoint(Pawn target, V3 start, V3 point, float speed, float gravity, float strength)
        {
            if (strength <= 0 || !AerialFlight.TryGet(target, out var flight)) return point;
            V3 motion = flight.Motor.Velocity * (strength / 60f);
            V3 aim = point;
            for (int i = 0; i < 4; i++)
            {
                V3 delta = aim - start;
                if (!AerialShotRules.Velocity(delta, speed, gravity, out V3 velocity)) return point;
                float planar = new Vector2(delta.X, delta.Z).magnitude;
                float horizontalSpeed = new Vector2(velocity.X, velocity.Z).magnitude;
                if (horizontalSpeed < .001f) break;
                aim = point + motion * Math.Min(120f, planar / horizontalSpeed);
            }
            return aim;
        }

        internal static bool Intercept(Pawn target, V3 start, V3 point, float speed, float strength, out V3 velocity)
        {
            float gravity = ViewRules.Gravity(FpsMod.Settings.gravity);
            V3 lead = LeadPoint(target, start, point, speed, gravity, strength);
            return AerialShotRules.Velocity(lead - start, speed, gravity, out velocity);
        }

        internal static Vector3 Origin(Thing shooter)
        {
            Vector3 at=VehicleModels.U(FloorSpace.Spot(shooter));
            return at+Vector3.up*Api.MuzzleHeight(shooter);
        }
        internal static bool InReach(Verb verb, Pawn plane, IntVec3 root)
        {
            if(verb.Caster==null||!verb.Caster.Spawned||verb.Caster.Map!=plane.Map)return false;
            Bounds box=AerialGeometry.BoundsAt(plane,AerialFlight.Store.Flights[plane].Motor);
            Vector3 origin=Origin(verb.Caster);
            // CanHitTargetFrom also evaluates prospective firing cells. Preserve that offset in true space.
            FloorBand band=FloorSpace.Band(plane.Map,root),own=FloorSpace.Band(plane.Map,verb.Caster.Position);
            origin+=new Vector3(root.x-verb.Caster.Position.x,band.Height-own.Height,
                root.z-band.Shift-(verb.Caster.Position.z-own.Shift));
            float reach=verb.verbProps.IsMeleeAttack?Math.Max(1.42f,verb.verbProps.range):verb.verbProps.range;
            return reach>0f&&(box.ClosestPoint(origin)-origin).sqrMagnitude<=reach*reach;
        }
        public static void CanHit(Verb __instance, IntVec3 root, LocalTargetInfo targ, ref bool __result)
        {
            if (!Target(targ, out Pawn plane)) return;
            if (AerialTurretTargeting.DirectTurret(__instance)) __result = AerialTurretTargeting.CanEngage(__instance, plane, root);
            else if (__result && !InReach(__instance,plane,root)) __result=false;
        }
        // Vanilla melee merely logs a failed CanHitTarget and still applies damage: guard execution too.
        public static bool MeleeShot(Verb __instance,ref bool __result)
        {
            if(!Target(__instance.CurrentTarget,out Pawn plane)||InReach(__instance,plane,__instance.Caster.Position))return true;
            __result=false;return false;
        }
        internal static void Install(Harmony harmony)
        {
            AerialBlast.Install(harmony);
            AerialBeamMotes.Install(harmony);
            AerialTurretPitch.Install(harmony);
            AerialTurretFire.Install(harmony);
            AerialProximityFuse.Install(harmony);
            AerialTurretTargeting.Install(harmony);
            AirSlabProtection.Install(harmony);
            AerialGeometry.InstallDrawFallback(harmony);
            AerialNativeFire.Install(harmony);
            AerialGroundFire.Install(harmony);
            var methods=new HashSet<MethodInfo>{AccessTools.Method(typeof(Verb),nameof(Verb.CanHitTargetFrom))};
            var melee=new HashSet<MethodInfo>{AccessTools.Method(typeof(Verb_MeleeAttack),"TryCastShot",Type.EmptyTypes)};
            foreach(Type type in GenTypes.AllTypes.Where(t=>!t.IsAbstract&&typeof(Verb).IsAssignableFrom(t)))
            {
                MethodInfo can=AccessTools.DeclaredMethod(type,nameof(Verb.CanHitTargetFrom),new[]{typeof(IntVec3),typeof(LocalTargetInfo)});
                if(can!=null)methods.Add(can);
                if(typeof(Verb_MeleeAttack).IsAssignableFrom(type))
                {var shot=AccessTools.DeclaredMethod(type,"TryCastShot",Type.EmptyTypes);if(shot!=null)melee.Add(shot);}
            }
            foreach(var method in methods)harmony.Patch(method,postfix:new HarmonyMethod(typeof(AerialCombat),nameof(CanHit)){priority=Priority.Last});
            foreach(var method in melee)harmony.Patch(method,prefix:new HarmonyMethod(typeof(AerialCombat),nameof(MeleeShot)){priority=Priority.First+100});
            Log.Message("[FpsBridge] AERIAL_COMBAT READY; melee=physical-reach+execution; small-arms=3D-target-height; rangeMethods="+methods.Count);
        }
    }
}
