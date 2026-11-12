using System;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static partial class CeCompat
    {
        internal static float TurretFlightTicks(Thing gun,Verb verb,IntVec3 target)
        {
            ThingDef projectile=ProjectileFor(verb);
            if(projectile==null)return 1f;
            // The same charge-selected speed and integer-tick arc drive the HUD and shot.
            FieldInfo current=AccessTools.Field(typeof(Verb),"currentTarget");object previous=current.GetValue(verb);
            try
            {
                current.SetValue(verb,new LocalTargetInfo(target));
                float speed=ViewRules.Speed(Math.Max(projectile.projectile.speed,Convert.ToSingle(Property(launchType,"ShotSpeed").GetValue(verb)))/60f,FpsMod.Settings.speed);
                FloorBand goal=FloorSpace.Band(gun.Map,target);
                V3 source=FloorSpace.Spot(gun)+new V3(0,1,0);
                V3 destination=new V3(target.x+.5f,goal.Height,target.z-goal.Shift+.5f);
                return ArtilleryTicks(destination-source,speed);
            }
            finally{current.SetValue(verb,previous);}
        }
        private static int ArtilleryTicks(V3 delta,float speed)
        {
            if(!float.IsFinite(speed)||speed<=0f)throw new InvalidOperationException("CE artillery requires a finite positive native launch speed.");
            float horizontal=MathF.Sqrt(delta.X*delta.X+delta.Z*delta.Z);
            float rise=(4f*Math.Clamp(horizontal*.3f,2f,30f)+Math.Abs(delta.Y))/NativeVerticalScale;
            return Math.Max(1,(int)Math.Ceiling(MathF.Sqrt(horizontal*horizontal+rise*rise)/speed));
        }
        private static void SetArtilleryArc(ShotScope context,float speed)
        {
            V3 delta=context.arcGoal-context.eye;
            int ticks=ArtilleryTicks(delta,speed);
            float x=delta.X/ticks,z=delta.Z/ticks;
            float y=NativeVerticalScale*MathF.Sqrt(Math.Max(0f,speed*speed-x*x-z*z));
            context.arcVelocity=new V3(x,y,z);
            context.arcGravity=2f*(y*ticks-delta.Y)/(ticks*(float)(ticks+1));
        }
        internal static void RequestReload(Verb verb)
        { if(!Ready||verb==null||!launchType.IsInstanceOfType(verb))return;object ammo=compAmmo.GetValue(verb);if(ammo!=null)startReload.Invoke(ammo,null); }
        internal static void BunkerShotOperator(Thing __instance,ref Pawn __result)
        {
            // FortifiedCE's shot callback credits PawnInside. Supply the active FPS operator
            // during this shot without inserting the pawn into the bunker or changing occupancy.
            if(__result==null&&scope?.launcher==__instance&&scope.pawn?.Spawned==true)
                __result=scope.pawn;
        }
        internal static bool TurretShot(Thing gun,Pawn by,Verb verb,LocalTargetInfo target)
        {
            if(!Ready||!launchType.IsInstanceOfType(verb))return false;
            target = FreeFireTarget(gun,verb,target);
            if(!target.IsValid)return false;
            var current=AccessTools.Field(typeof(Verb),"currentTarget");current.SetValue(verb,target);
            V3 eye=TurretControl.Muzzle(out V3 direction);
            ThingDef projectile=ProjectileFor(verb);bool artillery=projectile?.projectile?.flyOverhead==true;
            if (!artillery) direction = TurretControl.ManualShotDirection(by,direction,projectile);
            V3 goal=default;
            if(artillery)
            {
                eye=FloorSpace.Spot(gun)+new V3(0,1,0);
                FloorBand band=FloorSpace.Band(gun.Map,target.Cell);
                goal=new V3(target.Cell.x+.5f,band.Height,target.Cell.z-band.Shift+.5f);
            }
            V3 nativeDirection=WorldVelocityToNative(direction);
            CeNativeRules.TryAngles(nativeDirection.X,nativeDirection.Y,nativeDirection.Z,out float elevation,out float rotation);
            Vector3 nativeEye=WorldPointToNative(gun.Map,eye,FloorSpace.Band(gun.Map,gun.Position));
            // CE attributes a manned turret round to its native operator. Resolve that operator
            // even when a remote caller did not pass the pawn explicitly.
            Pawn shooter=VehicleApi.Get(verb,"ShooterPawn") as Pawn??by;
            var context=new ShotScope{previous=scope,verb=verb,pawn=shooter,launcher=gun,equipment=verb.EquipmentSource,
                eye=eye,origin=new Vector2(nativeEye.x,nativeEye.z),height=Math.Max(.001f,nativeEye.y),
                direction=direction,baseDirection=direction,elevation=elevation,rotation=rotation,artillery=artillery,arcGoal=goal};
            if(artillery)
            {
                float speed=ViewRules.Speed(Math.Max(projectile.projectile.speed,Convert.ToSingle(Property(launchType,"ShotSpeed").GetValue(verb)))/60f,FpsMod.Settings.speed);
                SetArtilleryArc(context,speed);
                context.direction=context.baseDirection=context.arcVelocity*(1f/context.arcVelocity.Length);
                nativeDirection=WorldVelocityToNative(context.direction);
                CeNativeRules.TryAngles(nativeDirection.X,nativeDirection.Y,nativeDirection.Z,out context.elevation,out context.rotation);
            }
            scope=context;
            try
            {
                bool fired=(bool)AccessTools.Method(verb.GetType(),"TryCastShot",Type.EmptyTypes).Invoke(verb,null);
                object ammo=compAmmo.GetValue(verb);if(ammo!=null&&!(bool)canFire.GetValue(ammo))startReload.Invoke(ammo,null);
                return fired;
            }
            finally{ReleaseScope(context);}
        }
        internal static bool? TurretLoaded(Verb verb)
        {
            if(!Ready||verb==null||!launchType.IsInstanceOfType(verb))return null;
            object ammo=compAmmo.GetValue(verb);return ammo==null?null:(bool)canFire.GetValue(ammo);
        }
        internal static ThingDef VehicleProjectile(object gun,ThingDef fallback)
        {
            if(!Ready||gun==null)return fallback;
            ThingDef loaded=VehicleApi.Get(gun,"loadedAmmo") as ThingDef;
            Def def=VehicleApi.Get(gun,"def") as Def;
            if(def?.modExtensions!=null)foreach(DefModExtension ext in def.modExtensions)
            {
                if(ext.GetType().FullName!="Vehicles.CETurretDataDefModExtension"&&ext.GetType().FullName!="CombatExtended.CETurretDataDefModExtension")continue;
                object set=AccessTools.Field(ext.GetType(),"_ammoSet")?.GetValue(ext);
                if(set==null&&AccessTools.Field(ext.GetType(),"ammoSet")?.GetValue(ext) is string name)
                {
                    var lookup=AccessTools.Field(VehicleApi.TurretType,"LookupAmmosetCE")?.GetValue(null) as Delegate;
                    set=lookup?.DynamicInvoke(name);if(set!=null)AccessTools.Field(ext.GetType(),"_ammoSet")?.SetValue(ext,set);
                }
                if(set==null)continue;
                if(AccessTools.Field(set.GetType(),"ammoTypes")?.GetValue(set) is System.Collections.IEnumerable links)
                    foreach(object link in links)
                        if(ReferenceEquals(AccessTools.Field(link.GetType(),"ammo")?.GetValue(link),loaded))
                            return AccessTools.Field(link.GetType(),"projectile")?.GetValue(link) as ThingDef;
                // A real CE ammo set cannot silently turn an unknown loaded round into its default projectile.
                return null;
            }
            if(fallback?.thingClass!=null&&projectileType.IsAssignableFrom(fallback.thingClass))return fallback;
            MethodInfo convert=AccessTools.Method(Named("CE_Utility"),"GetProjectile",new[]{typeof(ThingDef)});
            return fallback!=null?convert?.Invoke(null,new object[]{fallback}) as ThingDef??fallback:fallback;
        }
        internal static float VehicleBlastRadius(ThingDef projectile)
        {
            float radius=projectile?.projectile?.explosionRadius??0;
            if(Ready&&projectile?.comps!=null)foreach(CompProperties comp in projectile.comps)
                if(comp.GetType().FullName=="CombatExtended.CompProperties_ExplosiveCE")
                    radius=Math.Max(radius,Convert.ToSingle(AccessTools.Field(comp.GetType(),"explosiveRadius")?.GetValue(comp)??0));
            return radius;
        }
        private static bool VehicleOrigin(Thing projectile,object[] args,out V3 launchDirection)
        {
            launchDirection=default;
            object gun=VehicleWeapons.Firing??VehicleCeSpeed.Current;
            Pawn vehicle=gun==null?null:VehicleApi.Get(gun,"vehicle") as Pawn;
            if(!Ready||vehicle==null||args[0] as Thing!=vehicle||projectile.Map==null)return false;
            bool manual=ReferenceEquals(gun,VehicleWeapons.Firing)&&vehicle==VehicleControl.Vehicle;
            if(!manual&&(!View3D.Active||!Api.ModelsOn||string.IsNullOrEmpty(VehicleModels.Profile(vehicle).bodyModel)))return false;
            V3 muzzle=VehicleWeapons.FlightMuzzle(vehicle,gun);
            if(AerialFlight.Airborne(vehicle))
            {
                // The native CE angle callback now starts from the physical bore.
                // Keep its recoil, sway and pellet spread intact; relocate only the muzzle.
                float pitch=Convert.ToSingle(args[2]),heading=Convert.ToSingle(args[3]);
                launchDirection=NativeVelocityToWorld(HitMath.Direction(-heading,-pitch*Mathf.Rad2Deg));
                Vector3 aerial=ToNative(vehicle.Map,muzzle,vehicle);
                args[1]=vector2.Make2(new Vector2(aerial.x,aerial.z));args[4]=Math.Max(.01f,aerial.y);
                return true;
            }
            V3 direction=manual?VehicleWeapons.Direction(vehicle,gun):VehicleCeSpeed.Aim(vehicle,gun,projectile.def,muzzle);
            if(!manual){float deviation=CeNativeRules.HeadingDelta(Convert.ToSingle(args[3]),-VehicleApi.Float(gun,"TurretRotation"));direction=VehicleModels.V(Quaternion.Euler(0,-deviation,0)*VehicleModels.U(direction));VehicleWeapons.NativePitch(gun,direction);}
            launchDirection=direction;
            V3 nativeDirection=WorldVelocityToNative(direction);
            CeNativeRules.TryAngles(nativeDirection.X,nativeDirection.Y,nativeDirection.Z,out float angle,out float rotation);
            // AdoptVehicle decodes the launch in the vehicle's stored row band.
            // A muzzle above a ProjectZ floor still belongs to that band at launch.
            Vector3 at=ToNative(vehicle.Map,muzzle,vehicle);
            args[1]=vector2.Make2(new Vector2(at.x,at.z));args[2]=angle;args[3]=rotation;args[4]=Math.Max(.01f,at.y);
            return true;
        }
        private static void AdoptVehicle(Thing projectile,V3 launchDirection)
        {
            object gun=VehicleWeapons.Firing??VehicleCeSpeed.Current;
            Pawn vehicle=gun==null?null:VehicleApi.Get(gun,"vehicle") as Pawn;
            if(vehicle==null||projectile.Map==null||projectile.Destroyed)return;
            Vector3 native=vector3.Read3(exactPos.GetValue(projectile));FloorBand band=FloorSpace.Band(vehicle.Map,vehicle.Position);
            V3 pos=NativePointToWorld(vehicle.Map,native,band);
            bool bomb=AerialArmament.Bomb(gun);
            float originalSpeed=ActualLaunchSpeed(projectile,bomb);
            float multiplier=originalSpeed>0f?ViewRules.Speed(originalSpeed,FpsMod.Settings.speed)/originalSpeed:1f;
            V3 velocity=ReadNativeVelocity(projectile)*multiplier;
            RegisterVehicleTrace(projectile,vehicle,gun,pos,velocity,band,true,AerialTargetLock.Capture(gun));
            FpsLaunched++;VehicleWeapons.CeLaunched();
            CombatDiagnostics.CeLaunch(projectile, vehicle, pos);
        }
        internal static void AdoptRaidRound(Thing projectile,Pawn vehicle,object gun,V3 position,V3 velocity,Thing guidedTarget)
        {
            if(projectile?.Spawned!=true||projectile.Map!=vehicle.Map)return;
            RegisterVehicleTrace(projectile,vehicle,gun,position,velocity,FloorSpace.Band(vehicle.Map,vehicle.Position),false,guidedTarget);
        }

        private static void RegisterVehicleTrace(Thing projectile,Pawn vehicle,object gun,V3 pos,V3 velocity,FloorBand band,bool nativePhysics,Thing guidedTarget)
        {
            bool bomb=AerialArmament.Bomb(gun);
            traces.Remove(projectile);traces.Add(projectile,new Trace{map=projectile.Map,launcher=vehicle,pos=pos,previous=pos,
                velocity=AerialArmament.ReleaseVelocity(vehicle,gun,velocity*(1f/Math.Max(.00001f,velocity.Length)),velocity.Length),contacts=new Ballistics.ExternalTrace(vehicle),
                explosiveGun=AerialArmament.QuickGun(gun),
                structure=StructureFor(projectile,vehicle.def.defName,VehicleApi.Bool(gun,"FullAuto") || VehicleWeapons.IsMachineGun(gun) ? 2 : 1),
                gravity=ViewRules.Gravity(FpsMod.Settings.gravity),nativePhysics=nativePhysics,
                nativeVelocityMotion=bomb||AerialArmament.Data(gun)?.lockableMissile==true,
                nativeHeight=band.Height,nativeShift=band.Shift,nativeLevel=band.Level,nativeUnitsVersion=1,lastTick=Find.TickManager.TicksGame});
            if(AerialArmament.Data(gun)?.lockableMissile==true&&traces.TryGetValue(projectile,out Trace flight))
            {flight.gravity=0;flight.aerialTarget=guidedTarget;flight.aerialTurn=AerialArmament.Data(gun).guidanceDegreesPerSecond * FpsMod.Settings.missileTurn;}
        }
    }
}
