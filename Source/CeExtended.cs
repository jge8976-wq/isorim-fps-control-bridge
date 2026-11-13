using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static partial class CeCompat
    {
        private static MethodInfo nativeLaunch;
        internal static int HeadContacts, InstantRays, Pellets, RestoredFlights, Bounces;
        internal static ThingDef ProjectileFor(Verb verb) => Ready && verb!=null && launchType.IsInstanceOfType(verb) ? projectileDef.GetValue(verb) as ThingDef : null;
        internal static bool IsExplosiveDef(ThingDef def) => def?.projectile!=null && (def.projectile.explosionRadius>0f || def.projectile.explosionDelay>0 || VehicleBlastRadius(def)>0f);
        internal static bool IsExplosive(Verb verb) => IsExplosiveDef(ProjectileFor(verb));
        internal static bool IsGrenade(Verb verb)
        {
            for (Type type = verb?.GetType(); type != null; type = type.BaseType)
            {
                if (type.FullName == "CombatExtended.Verb_ThrowGrenade") return true;
                if (type.FullName == "CombatExtended.Verb_ShootCEOneUse")
                    return verb.EquipmentSource?.def.weaponTags?.Contains("CE_AI_Grenade") == true
                        || verb.verbProps.soundCast?.defName == "ThrowGrenade";
            }
            return false;
        }
        private static float ThrowGravity(Verb verb)=>.02f;
        private static float ThrowSpeed(Verb verb,V3 direction)
        {
            float range=Math.Max(2f,verb.EffectiveRange);
            // Same first-person throw contract: view elevation sets range; 45 degrees reaches the native maximum.
            return Math.Max(.1f,(float)Math.Sqrt(range*ThrowGravity(verb)));
        }
        private static LocalTargetInfo PickTarget(Pawn pawn,Verb verb)
        {
            if(CyberSkills.Eye(pawn)&&!IsExplosive(verb) && CyberSkills.Target(pawn,verb,out _) is Pawn target)return new LocalTargetInfo(target);
            return AimPicker.Target(pawn,verb);
        }
        internal static LocalTargetInfo FreeFireTarget(Thing caster, Verb verb, LocalTargetInfo fallback)
        {
            var projectile = ProjectileFor(verb);
            if (fallback.IsValid || !FpsMod.Settings.ceFreeFire || caster?.Map == null || projectile?.projectile == null
                || projectile.projectile.flyOverhead || IsExplosiveDef(projectile) || IsGrenade(verb)) return fallback;
            // The cell only initializes the native CE verb. The shot scope
            // launches along the view and resolves collisions in 3D.
            foreach (IntVec3 offset in GenAdj.CardinalDirections)
            {
                IntVec3 cell = caster.Position + offset;
                if (cell.InBounds(caster.Map) && FloorSpace.Band(caster.Map,cell).Level == FloorSpace.Band(caster.Map,caster.Position).Level)
                    return new LocalTargetInfo(cell);
            }
            return fallback;
        }

        internal static void RefreshAiBurst(Verb __instance, ref bool __result)
        {
            if (!__result || !FpsMod.Settings.ceAiBurstTracking || Managed(__instance) || __instance.CasterPawn is not Pawn pawn
                || pawn == Bridge.ShootingPawn || AerialFlight.Airborne(pawn)
                || __instance.CurrentTarget.Thing is not Pawn { Spawned: true, Dead: false } target
                || target.pather?.Moving != true) return;
            string weapon = __instance.EquipmentSource?.def.defName;
            if (VehicleGunRules.MachineGun(weapon ?? "") || weapon == "Gun_LMG") __result = false;
        }

        private static void StopBurst(Pawn pawn,Verb verb,Cast cast)
        {
            verb.Reset();
            int ticks=cast.clock.CompleteFallback(Find.TickManager.TicksGame,CyberSkills.FireFactor(pawn));
            pawn.stances.SetStance(new Stance_Cooldown(ticks,LocalTargetInfo.Invalid,verb));
        }
        private static bool Managed(Verb verb) => Ready && (scope?.verb==verb || casts.TryGetValue(verb,out Cast cast) && cast.managed && cast.pawn==Bridge.ShootingPawn && Bridge.CeControlsAllowed);
        private static void InstallExtended(Harmony harmony)
        {
            harmony.Patch(Method(launchType, "TryCastShot", Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(CeCompat), nameof(BeginNativeTurretShot)),
                finalizer: new HarmonyMethod(typeof(CeCompat), nameof(EndNativeTurretShot)));
            var lockAim = AccessTools.PropertyGetter(launchType,"LockRotationAndAngle");
            if (lockAim != null) harmony.Patch(lockAim,postfix:new HarmonyMethod(typeof(CeCompat),nameof(RefreshAiBurst)));
            harmony.Patch(AccessTools.PropertyGetter(shootType,"ShotsPerBurst"),postfix:new HarmonyMethod(typeof(CeCompat),nameof(MagazineBurst)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Verb),"TicksBetweenBurstShots"),postfix:new HarmonyMethod(typeof(CeCompat),nameof(SkillBurstInterval)));
            harmony.Patch(Method(launchType,"CanHitTargetFrom",typeof(IntVec3),typeof(LocalTargetInfo)),prefix:new HarmonyMethod(typeof(CeCompat),nameof(FpsCanHit)));
            harmony.Patch(Method(launchType,"CanHitTargetFrom",typeof(IntVec3),typeof(LocalTargetInfo),typeof(string).MakeByRefType()),prefix:new HarmonyMethod(typeof(CeCompat),nameof(FpsCanHitReport)){priority=Priority.First});
            harmony.Patch(Method(typeof(Verb),"TryFindShootLineFromTo",typeof(IntVec3),typeof(LocalTargetInfo),typeof(ShootLine).MakeByRefType(),typeof(bool)),prefix:new HarmonyMethod(typeof(CeCompat),nameof(FpsGenericLine)){priority=1200});
            MethodInfo line=ShapeMethod(launchType,"TryFindCEShootLineFromTo","Verse.IntVec3","Verse.LocalTargetInfo","Verse.ShootLine&","UnityEngine.Vector3&");
            harmony.Patch(line,prefix:new HarmonyMethod(typeof(CeCompat),nameof(FpsShootLine)));
            nativeLaunch=ShapeMethod(projectileType,"Launch","Verse.Thing","UnityEngine.Vector2","System.Single","System.Single","System.Single","System.Single","Verse.Thing","System.Single");
            MethodInfo ray=ShapeMethod(projectileType,"RayCast","Verse.Thing","Verse.VerbProperties","UnityEngine.Vector2","System.Single","System.Single","System.Single","System.Single","System.Single","System.Single","Verse.Thing","System.Boolean");
            harmony.Patch(ray,prefix:new HarmonyMethod(typeof(CeCompat),nameof(FpsInstantRay)));
            harmony.Patch(Method(projectileType,"ExposeData",Type.EmptyTypes),postfix:new HarmonyMethod(typeof(CeCompat),nameof(ExposeFlight)));
            Type turret=Named("Building_TurretGunCE");if(turret!=null)CeTurret.Install(harmony,turret);
        }
        internal static void MagazineBurst(Verb __instance,ref int __result)
        {
            if(!Managed(__instance)||!casts.TryGetValue(__instance,out Cast cast)||cast.mode!="AutoFire")return;
            object ammo=compAmmo.GetValue(__instance);
            if(ammo==null||!(bool)hasMagazine.GetValue(ammo))return;
            object props=Property(ammo.GetType(),"Props").GetValue(ammo);
            object set=AccessTools.Field(props.GetType(),"ammoSet")?.GetValue(props);
            int perSet=set==null?1:Math.Max(1,Convert.ToInt32(AccessTools.Field(set.GetType(),"ammoConsumedPerShot")?.GetValue(set)??1));
            object verbProps=__instance.verbProps;
            int perVerb=Math.Max(1,Convert.ToInt32(AccessTools.Field(verbProps.GetType(),"ammoConsumedPerShotCount")?.GetValue(verbProps)??1));
            __result=CeNativeRules.MagazineBurst((int)curRounds.GetValue(ammo),perSet*perVerb);
        }
        internal static void SkillBurstInterval(Verb __instance,ref int __result)
        { if(Managed(__instance))__result=Math.Max(1,Mathf.RoundToInt(__result*CyberSkills.FireFactor(Bridge.ShootingPawn))); }
        internal static bool FpsCanHit(Verb __instance,LocalTargetInfo __1,ref bool __result)
        {
            if(!Managed(__instance))return true;
            __result=__1.IsValid&&__1.Cell.InBounds(__instance.caster.Map)&&(__1.Thing==null||__1.Thing.Map==__instance.caster.Map);
            return false; // Actual obstructions/roof/cover are swept by FPS, including logical rows on another floor.
        }
        internal static bool FpsShootLine(Verb __instance,IntVec3 __0,LocalTargetInfo __1,ref ShootLine __2,ref Vector3 __3,ref bool __result)
        {
            if (AerialTurretTargeting.DirectTurret(__instance) && AerialCombat.Target(__1, out var aircraft) && !Managed(__instance))
            {
                __2 = new ShootLine(__0, __1.Cell);
                __3 = __1.CenterVector3;
                __result = AerialTurretTargeting.CanEngage(__instance, aircraft, __0);
                return false;
            }
            if(!Managed(__instance))return true;
            __2=new ShootLine(__0,__1.Cell);__3=__1.CenterVector3;__result=__1.IsValid;return false;
        }
        internal static bool FpsCanHitReport(Verb __instance,IntVec3 __0,LocalTargetInfo __1,ref string __2,ref bool __result)
        {
            if (!Managed(__instance) && AerialTurretTargeting.DirectTurret(__instance) && AerialCombat.Target(__1, out var aircraft))
            {
                __result = AerialTurretTargeting.CanEngage(__instance, aircraft, __0);
                __2 = __result ? null : L.T("目标超出射程或视线受阻。", "Target is out of range or blocked.");
                return false;
            }
            if(!Managed(__instance))return true;
            __2=null;return FpsCanHit(__instance,__1,ref __result);
        }
        internal static bool FpsGenericLine(Verb __instance,IntVec3 __0,LocalTargetInfo __1,ref ShootLine __2,ref bool __result)
        {if(!Managed(__instance))return true;__2=new ShootLine(__0,__1.Cell);__result=__1.IsValid;return false;}
        private static void PelletDirection(ShotScope shot)
        {
            ThingDef def=ProjectileFor(shot.verb);
            int count=Convert.ToInt32(AccessTools.Field(def.projectile.GetType(),"pelletCount")?.GetValue(def.projectile)??1);
            shot.direction=shot.baseDirection;
            if(count>1&&shot.homing==null)
            {
                StatDef spread=DefDatabase<StatDef>.GetNamedSilentFail("ShotSpread");
                float degrees=spread!=null&&shot.equipment!=null?shot.equipment.GetStatValue(spread):4f;
                degrees*=Convert.ToSingle(AccessTools.Field(def.projectile.GetType(),"spreadMult")?.GetValue(def.projectile)??1f);
                var d=CeNativeRules.Pellet(shot.baseDirection.X,shot.baseDirection.Y,shot.baseDirection.Z,shot.pellet,count,degrees);
                shot.direction=new V3(d.x,d.y,d.z);Pellets++;
            }
            shot.pellet++;
            V3 nativeDirection=WorldVelocityToNative(shot.direction);
            CeNativeRules.TryAngles(nativeDirection.X,nativeDirection.Y,nativeDirection.Z,out shot.elevation,out shot.rotation);
        }
        internal static bool FpsInstantRay(Thing __instance,object[] __args)
        {
            ShotScope s=scope;if(s==null||s.verb==null||!s.adjusted)return true;
            if(!ReferenceEquals(__args[0],s.launcher)&&!ReferenceEquals(__args[0],s.pawn))return true;
            nativeLaunch.Invoke(__instance,new object[]{__args[0],vector2.Make2(s.origin),s.elevation,s.rotation,s.height,Convert.ToSingle(__args[6]),s.equipment,-1f});
            if(!traces.TryGetValue(__instance,out Trace trace))throw new InvalidOperationException("Instant CE shot did not initialize FPS flight");
            V3 from=trace.pos;float range=Math.Max(1f,s.verb.EffectiveRange*2f);
            trace.previous=from;trace.pos=from+trace.velocity*(range/Math.Max(.001f,trace.velocity.Length));
            if(!FloorSpace.InWorld(trace.map,trace.pos))
            {
                for(int i=0;i<16&&!FloorSpace.InWorld(trace.map,trace.pos);i++)trace.pos=V3.Lerp(from,trace.pos,.8f);
            }
            exactPos.SetValue(__instance,vector3.Make3(ToNative(trace.map,trace.pos)));
            bool hit=false;FpsCollision(__instance,ref hit);
            if(!hit&&!__instance.Destroyed){trace.completed=true;ceLanded.SetValue(__instance,true);ceImpact.Invoke(__instance,new object[]{null});}
            Ballistics.ExternalBeam(from,trace.pos);InstantRays++;return false;
        }
        private static Vector3 ToNative(Map map,V3 world,Thing surface=null)
        {
            IntVec3 cell=FloorSpace.Cell(map,world,surface);FloorBand band=FloorSpace.Band(map,cell);
            return WorldPointToNative(map,world,band);
        }
        private static readonly List<V3> throwPath = new List<V3>();
        private static int throwFrame = -1;
        private static Pawn thrower;
        private static Verb throwVerb;
        private static ThingDef throwProjectile;
        private static bool throwContact;
        private static ImpactMinimap.Impact throwImpact;

        internal static void BounceThrow(ref V3 position, ref V3 velocity, V3 normal)
        {
            float dot = V3.Dot(velocity, normal);
            velocity = (velocity - normal * (2f * dot)) * .45f;
            position += normal * .05f;
        }

        internal static bool TryThrowImpact(Pawn pawn, Verb verb, out ImpactMinimap.Impact impact)
        {
            impact = default;
            ThingDef projectile = ProjectileFor(verb);
            if (pawn?.Map == null || !NativeProjectile(projectile) || !IsGrenade(verb)) return false;
            if (throwFrame != Time.frameCount || thrower != pawn || throwVerb != verb || throwProjectile != projectile)
            {
                throwFrame = Time.frameCount;
                thrower = pawn;
                throwVerb = verb;
                throwProjectile = projectile;
                throwPath.Clear();
                V3 direction = ControlCamera.ShotDirection(pawn);
                throwContact = ImpactMinimap.Trace(pawn, pawn, projectile, Ballistics.Hand(pawn), direction * ThrowSpeed(verb, direction),
                    ThrowGravity(verb), out throwImpact, ceThrow: true, path: throwPath);
            }
            impact = throwImpact;
            return throwContact;
        }

        internal static void Preview(Pawn pawn, Verb verb)
        {
            if (TryThrowImpact(pawn, verb, out ImpactMinimap.Impact impact))
                Visuals.ThrowPreview(throwPath, impact.Point, impact.BlastRadius, Vector3.zero);
            else if (throwFrame == Time.frameCount && thrower == pawn && throwVerb == verb && throwPath.Count > 1)
                Visuals.ThrowPreview(throwPath, throwPath[throwPath.Count - 1], CeCompat.VehicleBlastRadius(throwProjectile), Vector3.zero);
        }
    }
}
