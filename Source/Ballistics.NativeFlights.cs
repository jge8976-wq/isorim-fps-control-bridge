using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static partial class Ballistics
    {
        [ThreadStatic] private static Verb_LaunchProjectile nativeShot;
        private static void InstallNativeFlights(Harmony harmony,MethodInfo launch)
        {
            var methods=new HashSet<MethodInfo>();
            foreach(ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
                if(def.Verbs!=null)foreach(VerbProperties props in def.Verbs)
                {
                    if(props.IsMeleeAttack || props.verbClass==null || !typeof(Verb_LaunchProjectile).IsAssignableFrom(props.verbClass))continue;
                    var method=AccessTools.Method(props.verbClass,"TryCastShot",Type.EmptyTypes);
                    if(method!=null)method=AccessTools.DeclaredMethod(method.DeclaringType,method.Name,Type.EmptyTypes);
                    if(method!=null && !method.IsAbstract)methods.Add(method);
                }
            // Even an empty startup test fixture validates the base route; real defs add their overrides.
            methods.Add(AccessTools.Method(typeof(Verb_LaunchProjectile),"TryCastShot",Type.EmptyTypes));
            foreach(var method in methods)harmony.Patch(method,
                prefix:new HarmonyMethod(typeof(Ballistics),nameof(CaptureNativeShot)){priority=Priority.First},
                finalizer:new HarmonyMethod(typeof(Ballistics),nameof(ReleaseNativeShot)){priority=Priority.Last});
            harmony.Patch(launch,postfix:new HarmonyMethod(typeof(Ballistics),nameof(AfterNativeLaunch)){priority=Priority.Last});
            Log.Message("[FpsBridge] NATIVE_3D_FLIGHTS READY shotMethods="+methods.Count+"; native attacks and costs preserved");
        }
        public static void CaptureNativeShot(Verb __instance,out Verb_LaunchProjectile __state)
        {
            __state=nativeShot;
            nativeShot=Enabled && Bridge.Active && !TurretControl.Manned && !VehicleControl.On && __instance.Caster==Bridge.ShootingPawn
                && WeaponCompatibility.Native3D(__instance)?__instance as Verb_LaunchProjectile:null;
        }
        public static Exception ReleaseNativeShot(Exception __exception,Verb_LaunchProjectile __state)
        {nativeShot=__state;return __exception;}
        public static void AfterNativeLaunch(Projectile __instance,Thing launcher)
        {
            AerialProximityFuse.Arm(__instance,launcher,launcher is Pawn pawnSource?pawnSource.equipment?.Primary:null,__instance.intendedTarget);
            AdoptAerialThreat(__instance,launcher);
            if (!shots.ContainsKey(__instance)) TurretTracers.ObserveVanilla(__instance);
            Verb_LaunchProjectile verb=nativeShot;
            if(verb==null || launcher!=verb.Caster || __instance.Destroyed || !__instance.Spawned || shots.ContainsKey(__instance)
                || !WeaponCompatibility.SafeProjectile(__instance.def) || !(launcher is Pawn pawn))return;
            V3 eye=Eye(pawn),direction=ControlCamera.ShotDirection(pawn);
            // Keep a native multi-projectile attack's horizontal fan/scatter about the current crosshair.
            Vector3 origin=originRef(__instance),actual=destinationRef(__instance)-origin;
            LocalTargetInfo target=verb.CurrentTarget;
            Vector3 nominal=target.IsValid?(target.HasThing?target.Thing.DrawPos:target.Cell.ToVector3Shifted())-origin:actual;
            if(actual.sqrMagnitude>.0001f && nominal.sqrMagnitude>.0001f)
            {
                float angle=Mathf.DeltaAngle(Mathf.Atan2(nominal.x,nominal.z)*Mathf.Rad2Deg,Mathf.Atan2(actual.x,actual.z)*Mathf.Rad2Deg);
                Vector3 d=Quaternion.AngleAxis(angle,Vector3.up)*new Vector3(direction.X,direction.Y,direction.Z);direction=new V3(d.x,d.y,d.z);
            }
            float speed=ViewRules.Speed(BulletSpeeds.ForWeapon(verb.EquipmentSource?.def.defName,__instance.def.projectile.SpeedTilesPerTick),FpsMod.Settings.speed);
            AdoptNativeRound(__instance,pawn,verb,eye,direction*speed,ViewRules.Gravity(FpsMod.Settings.gravity));
        }
        internal static void AdoptNativeRound(Projectile p,Pawn pawn,Verb_LaunchProjectile verb,V3 start,V3 velocity,float gravity)
        {
            V3 far=Landing(pawn.Map,start,velocity,gravity);
            IntVec3 end=FloorSpace.Cell(pawn.Map,far).ClampInsideMap(pawn.Map);
            FloorSpace.OwnFlight(p);
            Vector3 origin=FloorSpace.Logical(pawn.Map,start,pawn.Position);origin.y=p.def.Altitude;originRef(p)=origin;
            destinationRef(p)=FloorSpace.Logical(pawn.Map,far,end);
            float flat=Mathf.Max(.01f,Mathf.Sqrt(velocity.X*velocity.X+velocity.Z*velocity.Z));
            ticksRef(p)=Math.Max(1,Mathf.CeilToInt(new Vector2(far.X-start.X,far.Z-start.Z).magnitude/flat));
            var shot=new Shot{kind=Kind.Round,pos=start,vel=velocity,gravity=gravity,startY=start.Y,
                muzzleY=FloorSpace.Offset(pawn).y+IsoRim.Projectiles.MuzzleHeight(pawn),lastTick=Find.TickManager.TicksGame,launcher=pawn};
            shots[p]=shot;p.Position=FloorSpace.Cell(pawn.Map,start).ClampInsideMap(pawn.Map);
            if(p.GetType().FullName==RatkinTurretShots.BoltClass || p.GetType().FullName==RatkinTurretShots.ShellClass)
                shot.ratkin=new RatkinFlight{cannon=p.GetType().FullName==RatkinTurretShots.ShellClass};
            else AttachPenetration(p,verb);
        }
    }
}
