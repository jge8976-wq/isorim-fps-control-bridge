using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static partial class CeCompat
    {
        private static MethodInfo ceImpact, ceInterceptor, ceBlockerSegment, ceBlockerContact, ceSuppress;
        private static FieldInfo ceLanded;
        [ThreadStatic] private static ImpactScope impactScope;
        internal static bool InFpsImpact => impactScope != null;
        private sealed class ImpactScope { internal Thing target; internal BodyPartRecord part; internal ImpactScope previous; }
        internal static int FpsLaunched, FpsMoved, FpsDraws, FpsContacts, InstantCasts, WarmupTransferred, MobileReloadProtected;

        private static void InstallFpsFlight(Harmony harmony)
        {
            MethodInfo move = Method(projectileType, "MoveForward", Type.EmptyTypes);
            if (move.ReturnType != typeof(Vector3)) throw new TypeLoadException("Running CE MoveForward did not resolve to the Waylith vector ABI");
            MethodInfo tick = Method(projectileType, "Tick", Type.EmptyTypes);
            MethodInfo collision = Method(projectileType, "CheckForCollisionBetween", Type.EmptyTypes);
            harmony.Patch(Method(projectileType,"TryCollideWith",typeof(Thing)),prefix:new HarmonyMethod(typeof(CeCompat),nameof(AircraftCollision)){priority=Priority.First});
            MethodInfo ground = AccessTools.PropertyGetter(projectileType, "ShouldCollideWithSomething")
                ?? throw new MissingMethodException("ProjectileCE.ShouldCollideWithSomething");
            ceLanded = AccessTools.Field(projectileType, "landed");
            if (ceLanded?.FieldType != typeof(bool) || exactPos.GetSetMethod(true) == null) throw new MissingMemberException("CE projectile mutable contact state");
            ceImpact = Method(projectileType, "Impact", typeof(Thing));
            ceInterceptor = Method(projectileType, "CheckIntercept", typeof(Thing), typeof(CompProjectileInterceptor), typeof(bool));
            ceSuppress = Method(projectileType, "RayCastSuppression", typeof(IntVec3), typeof(IntVec3), typeof(Map));
            Type registry = Named("Compatibility.BlockerRegistry") ?? throw new TypeLoadException("CE blocker registry missing");
            ceBlockerSegment = ShapeMethodStatic(registry, "CheckForCollisionBetweenCallback", projectileType.FullName,"UnityEngine.Vector3","UnityEngine.Vector3");
            ceBlockerContact = ShapeMethodStatic(registry, "BeforeCollideWithCallback", projectileType.FullName,"Verse.Thing");
            harmony.Patch(tick, prefix: new HarmonyMethod(typeof(CeCompat), nameof(BeforeFpsTick)) { priority = Priority.First });
            harmony.Patch(move, prefix: new HarmonyMethod(typeof(CeCompat), nameof(FpsMove)) { priority = Priority.First });
            harmony.Patch(collision, prefix: new HarmonyMethod(typeof(CeCompat), nameof(FpsCollision)) { priority = Priority.First });
            harmony.Patch(ground, prefix: new HarmonyMethod(typeof(CeCompat), nameof(KeepFpsFlight)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(Thing),"TakeDamage",new[]{typeof(DamageInfo)}),
                prefix: new HarmonyMethod(typeof(CeCompat),nameof(ApplyFpsHitPart)) { priority = Priority.First });
            // Component bookkeeping stays native. Native motion runs once inside FpsMove,
            // where its result is converted through the projectile's saved launch-floor frame.
            Type worker = Named("BaseTrajectoryWorker");
            var hooked = new HashSet<MethodInfo>();
            foreach (string name in new[]{"BaseTrajectoryWorker","BallisticsTrajectoryWorker","LerpedTrajectoryWorker",
                "LerpedTrajectoryWorker_ExactPosDrawing","LegacyTrajectoryWorker","HomingBulletTrajectoryWorker","SmartRocketTrajectoryWorker"})
            {
                Type type=Named(name);
                if (type!=null && worker.IsAssignableFrom(type))
                {
                    MethodInfo notify = AccessTools.DeclaredMethod(type,"NotifyTicked",new[]{projectileType});
                    if (notify != null && !notify.IsAbstract && hooked.Add(notify))
                        harmony.Patch(notify,prefix:new HarmonyMethod(typeof(CeCompat),nameof(KeepFpsAirPhysics)));
                }
            }
            if (hooked.Count == 0) throw new MissingMethodException("CE trajectory NotifyTicked methods");
        }
        private static MethodInfo ShapeMethodStatic(Type type,string name,params string[] shape)
        {
            foreach(MethodInfo method in type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static))
            {
                if(method.Name!=name)continue;
                ParameterInfo[] p=method.GetParameters();if(p.Length!=shape.Length)continue;
                bool ok=true;for(int i=0;i<p.Length;i++)if(p[i].ParameterType.FullName!=shape[i]){ok=false;break;}
                if(ok)return method;
            }
            throw new MissingMethodException(type.FullName,name);
        }
        private static bool OwnFlight(Thing projectile,out Trace trace)
        {
            if(!traces.TryGetValue(projectile,out trace)||trace.completed||projectile.Destroyed)return false;
            if (trace.nativeRestorePending && projectile.Map != null) RestoreNativeFlight(projectile, trace);
            if(trace.map==null&&projectile.Spawned)
            {trace.map=projectile.Map;trace.contacts=new Ballistics.ExternalTrace(trace.launcher,trace.turretOperator ?? AerialTurretFire.Operator(trace.launcher));trace.lastTick=Find.TickManager.TicksGame-1;}
            return projectile.Map==trace.map&&trace.map!=null;
        }
        internal static bool BeforeFpsTick(Thing __instance)
        {
            if(!OwnFlight(__instance,out Trace flight))return true;
            int now=Find.TickManager.TicksGame;
            if(now==flight.lastTick)return false;
            if (flight.nativePhysics) PrepareNativeVelocity(__instance, flight.velocity);
            flight.lastTick=now;return true;
        }
        internal static bool FpsMove(Thing __instance,ref Vector3 __result)
        {
            if(!OwnFlight(__instance,out Trace flight))return true;
            V3 pos=flight.pos;flight.previous=pos;
            if (flight.nativePhysics) flight.velocity = ReadNativeVelocity(__instance);
            if(flight.homing!=null)
            {
                if(flight.homing.Spawned&&!flight.homing.Dead&&flight.homing.Map==flight.map)
                { Vector3 h=CyberSkills.HeadOf(flight.homing);V3 to=new V3(h.x,h.y,h.z)-pos;float length=to.Length,speed=flight.velocity.Length;
                  if(length>.0001f)flight.velocity=to*(Math.Min(speed,length)/length); }
                else flight.homing=null;
            }
            if(flight.aerialTarget!=null)
            {
                if(!AerialTargetLock.Valid(flight.aerialTarget,flight.map))flight.aerialTarget=null;
                else flight.velocity=AerialTargetLock.Steer(flight.aerialTarget,flight.map,pos,flight.velocity,flight.aerialTurn);
            }
            if (flight.nativePhysics)
            {
                FloorBand band = new FloorBand(flight.nativeLevel, flight.nativeShift, FloorSpace.Bands(flight.map)[0].Rows, flight.nativeHeight);
                flight.pos = NativeMotionStep(__instance, flight.map, pos, ref flight.velocity, band, flight.nativeVelocityMotion);
            }
            else
            {
                var step=CeNativeRules.FlightStep(pos.X,pos.Y,pos.Z,flight.velocity.X,flight.velocity.Y,flight.velocity.Z,flight.homing!=null?0f:flight.gravity);
                flight.velocity=new V3(flight.velocity.X,step.verticalVelocity,flight.velocity.Z);
                flight.pos=new V3(step.x,step.y,step.z);
            }
            __result=ToNative(flight.map,flight.pos);FpsMoved++;return false;
        }
        internal static bool KeepFpsAirPhysics(Thing __0) => !OwnFlight(__0,out Trace flight) || flight.nativePhysics;
        internal static bool KeepFpsFlight(Thing __instance,ref bool __result)
        {
            if(!OwnFlight(__instance,out _))return true;
            __result=false;return false; // The FPS sweep stops at ground/roof/map bounds, not CE's old target-flight timer.
        }
        internal static bool FpsCollision(Thing __instance,ref bool __result)
        {
            if(!OwnFlight(__instance,out Trace flight))return true;
            Map map=flight.map;
            V3 from=flight.previous,to=flight.pos;
            Vector3 last=ToNative(map,from),pos=ToNative(map,to);
            // Keep CE/third-party interception APIs, while replacing the random cell/plant geometry scan.
            foreach(Thing interceptor in map.listerThings.ThingsInGroup(ThingRequestGroup.ProjectileInterceptor))
                if((bool)ceInterceptor.Invoke(__instance,new object[]{interceptor,interceptor.TryGetComp<CompProjectileInterceptor>(),false}))
                {
                    // Match CE: high shields absorb overhead shells without a ground-level explosion below the shield.
                    if(__instance.def.projectile.flyOverhead)__instance.Destroy();
                    else{AerialBlast.Remember(__instance,map,to);ceLanded.SetValue(__instance,true);ceImpact.Invoke(__instance,new object[]{null});}
                    __result=true;return false;
                }
            if((bool)ceBlockerSegment.Invoke(null,new[]{(object)__instance,vector3.Make3(last),vector3.Make3(pos)}))
            {__result=true;return false;}
            bool hit;
            float fraction;
            Thing target;
            BodyPartRecord part;
            HitRegion region;
            V3 normal;
            CeStructureRoof.Contact? roof;
            while (true)
            {
                hit = flight.contacts.Segment(map, from, to, out fraction, out target, out part, out region, out normal,
                    flight.structure?.Things, flight.structure?.Roofs);
                roof = hit && target == null
                    && CeStructureRoof.TryContact(map, from, to, fraction, out var roofHit, flight.structure?.Roofs) ? roofHit : null;
                if (!hit) break;
                if (target != null && (bool)ceBlockerContact.Invoke(null, new object[] { __instance, target }))
                { __result = true; return false; }
                if (!ContinueStructure(__instance, flight, target, V3.Lerp(from, to, fraction), roof)) break;
            }
            bool homingFallback = false;
            if(!hit&&flight.homing!=null&&!flight.homing.Dead&&flight.homing.Map==map)
            {
                Vector3 h=CyberSkills.HeadOf(flight.homing);V3 head=new V3(h.x,h.y,h.z);
                if((head-to).Length<.025f){hit=true;homingFallback=true;fraction=1;target=flight.homing;part=CyberSkills.HeadPart(flight.homing);region=HitRegion.Head;}
            }
            if(FloorSpace.Band(map,new IntVec3(last)).Level==FloorSpace.Band(map,new IntVec3(pos)).Level)
                ceSuppress.Invoke(__instance,new object[]{new IntVec3(last),new IntVec3(pos),map});
            if(!hit){__result=AerialProximityFuse.TryBurst(__instance,flight.launcher,from,to);return false;}
            if (homingFallback
                && (bool)ceBlockerContact.Invoke(null, new object[] { __instance, target }))
            { __result = true; return false; }
            V3 at=V3.Lerp(from,to,fraction);
            if(flight.bounces>0&&target!=null&&!(target is Pawn)&&Math.Abs(normal.Y)<.5f)
            {
                flight.bounces--;float dot=V3.Dot(flight.velocity,normal);
                flight.velocity=(flight.velocity-normal*(2f*dot))*.45f;
                flight.pos=at+normal*.05f;exactPos.SetValue(__instance,vector3.Make3(ToNative(map,flight.pos)));
                Bounces++;__result=false;return false;
            }
            flight.pos=at;flight.completed=true;
            if (roof.HasValue && __instance.def.projectile.damageDef?.harmsHealth == true)
                CeStructureRoof.Hit(map, roof.Value, RoofHealth.ProjectileDamage(__instance));
            AerialBlast.Remember(__instance,map,at);
            if(target!=null&&target==flight.homing&&target is Pawn locked)
            {part=CyberSkills.HeadPart(locked);region=HitRegion.Head;HeadContacts++;}
            exactPos.SetValue(__instance,vector3.Make3(ToNative(map,at,target)));
            IntVec3 cell=FloorSpace.ContactCell(map,at,flight.velocity.Y,target);
            if(cell.InBounds(map))__instance.Position=cell;
            ceLanded.SetValue(__instance,true);
            var context=new ImpactScope{target=target,part=part,previous=impactScope};impactScope=context;
            Thing instigator = VehicleApi.Get(__instance, "launcher") as Thing ?? flight.launcher;
            object vehicleStats = target is Pawn vehicle && VehicleApi.IsVehicle(vehicle) && instigator != null
                ? VehicleApi.Get(vehicle, "statHandler") : null;
            try
            {
                // CE's normal cell scan registered its impact before applying damage.
                // This path replaces that scan with the actual model contact.
                if (vehicleStats != null) VehicleApi.Call(vehicleStats, "RegisterImpacter", instigator, cell);
                if(target is Pawn pawn)Ballistics.Notify(pawn,part,region);
                Visuals.ImpactLight(map,new V3(at.X,at.Y,at.Z),target);
                bool directFuze=AerialProximityFuse.DirectContact(__instance,target);
                float contactDamage=directFuze?Convert.ToSingle(VehicleApi.Get(__instance,"DamageAmount")):0f;
                if(directFuze)AerialBlast.DirectVictim(__instance,(Pawn)target);
                using (VehicleBallisticDamage.Begin(__instance, target, instigator, at, flight.velocity))
                    ceImpact.Invoke(__instance,new object[]{target});
                if(directFuze)AerialProximityFuse.ApplyDirectContact(__instance.def,(Pawn)target,instigator,at,flight.velocity,contactDamage);
                FpsContacts++;
                CombatDiagnostics.CeContact(__instance, flight.launcher, target);
            }
            finally
            {
                impactScope=context.previous;
                if (vehicleStats != null) VehicleApi.Call(vehicleStats, "DeregisterImpacter", instigator);
            }
            __result=true;return false;
        }
        internal static void ApplyFpsHitPart(Thing __instance,ref DamageInfo dinfo)
        {
            ImpactScope hit=impactScope;
            if(hit?.target==__instance && hit.part!=null && __instance is Pawn pawn && !pawn.health.hediffSet.PartIsMissing(hit.part))
                dinfo.SetHitPart(hit.part); // Preserve CE's amount, penetration units, damage def and secondary damage handling.
        }
    }
}
