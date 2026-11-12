using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Native queues still own ammunition, reloads, warmup, heat and shot timing.
    internal static class AerialNativeFire
    {
        [ThreadStatic] private static object firing;
        [ThreadStatic] private static V3 firingDirection;
        [ThreadStatic] private static bool ceAngle;
        [ThreadStatic] private static object targeting;
        [ThreadStatic] private static object raidShot;
        internal static void SetRaidShot(object gun) => raidShot=gun;
        internal static object Current=>firing;
        internal static int Blocked,Accepted,CeAngles,VanillaShots;
        internal static void Install(Harmony h)
        {
            var seen=new HashSet<MethodInfo>();
            foreach(Type type in GenTypes.AllTypes)
                if(VehicleApi.TurretType.IsAssignableFrom(type)&&!type.IsAbstract)
                {var method=AccessTools.DeclaredMethod(type,"FireTurret",Type.EmptyTypes);if(method!=null&&seen.Add(method))h.Patch(method,prefix:new HarmonyMethod(typeof(AerialNativeFire),nameof(BeforeFire)){priority=Priority.First+200},finalizer:new HarmonyMethod(typeof(AerialNativeFire),nameof(EndFire)));}
            h.Patch(AccessTools.Method(VehicleApi.TurretType,"SetTarget",new[]{typeof(LocalTargetInfo)}),postfix:new HarmonyMethod(typeof(AerialFireControl),nameof(AerialFireControl.NativeTargetAssigned)));
            h.Patch(AccessTools.Method(VehicleApi.TurretType,"AngleBetween",new[]{typeof(Vector3)}),prefix:new HarmonyMethod(typeof(AerialNativeFire),nameof(Traverse)));
            h.Patch(AccessTools.Method(VehicleApi.TurretType,"TurretTargeterTick"),prefix:new HarmonyMethod(typeof(AerialNativeFire),nameof(BeforeTargeter)),finalizer:new HarmonyMethod(typeof(AerialNativeFire),nameof(EndTargeter)));
            h.Patch(AccessTools.PropertyGetter(VehicleApi.TurretType,"RotationAligned"),prefix:new HarmonyMethod(typeof(AerialNativeFire),nameof(AutomaticAlignment)));
            var target=AccessTools.Method(AccessTools.TypeByName("Vehicles.TargetingHelper"),"TargetMeetsRequirements",new[]{VehicleApi.TurretType,typeof(LocalTargetInfo),typeof(IntVec3).MakeByRefType()});
            if(target!=null)h.Patch(target,prefix:new HarmonyMethod(typeof(AerialNativeFire),nameof(TargetRequirements)));
            var launch=AccessTools.Method(typeof(Projectile),"Launch",new[]{typeof(Thing),typeof(Vector3),typeof(LocalTargetInfo),typeof(LocalTargetInfo),typeof(ProjectileHitFlags),typeof(bool),typeof(Thing),typeof(ThingDef)});
            h.Patch(launch,postfix:new HarmonyMethod(typeof(AerialNativeFire),nameof(VanillaLaunch)));
            var ce=AccessTools.TypeByName("CombatExtended.Compatibility.VehiclesCompat.VehiclesCompat");
            var angle=AccessTools.Method(ce,"ProjectileAngleCE");
            if(angle!=null)
            {
                h.Patch(angle,prefix:new HarmonyMethod(typeof(AerialNativeFire),nameof(BeginCeAngle)),postfix:new HarmonyMethod(typeof(AerialNativeFire),nameof(AfterCeAngle)),finalizer:new HarmonyMethod(typeof(AerialNativeFire),nameof(EndCeAngle)));
                var basic=AccessTools.Method(AccessTools.TypeByName("CombatExtended.CE_Utility"),"GetShotAngle",new[]{typeof(float),typeof(float),typeof(float),typeof(bool),typeof(float)});
                h.Patch(basic,postfix:new HarmonyMethod(typeof(AerialNativeFire),nameof(CeBaseAngle)));
            }
        }
        public static bool Traverse(object __instance,ref bool __result)
        {
            if(!AerialWeaponSolution.Owns(__instance,out var vehicle)||VehicleControl.Owns(vehicle)||AerialWeaponSolution.Fixed(vehicle,__instance))return true;
            __result=true;
            return false;
        }
        public static void BeforeTargeter(object __instance,out object __state)
        {
            __state=targeting;
            if(!AerialWeaponSolution.Owns(__instance,out var vehicle)||!AerialAutoAttack.Automatic(vehicle))return;
            targeting=__instance;
            var target=(LocalTargetInfo)VehicleApi.Get(__instance,"targetInfo");
            if(!VehicleApi.Bool(__instance,"TargetLocked")&&AerialWeaponSolution.Solve(vehicle,__instance,target,out _,out _,false))
            {
                VehicleApi.Set(__instance,"TargetLocked",true);
                VehicleApi.Call(__instance,"ResetPrefireTimer");
            }
        }
        public static void EndTargeter(object __state)=>targeting=__state;
        public static bool AutomaticAlignment(object __instance,ref bool __result)
        {
            if(!ReferenceEquals(targeting,__instance))return true;
            __result=true;
            return false;
        }
        public static bool BeforeFire(object __instance,out (object gun,V3 direction) __state)
        {
            __state=(firing,firingDirection);
            if(VehicleApi.Get(__instance,"vehicle") is Pawn aircraft&&!AircraftSystems.CanFire(aircraft,__instance))return false;
            if(ReferenceEquals(raidShot,__instance)||!AerialWeaponSolution.Owns(__instance,out var v))return true;
            // Populate VF's lazy fuel cache before legacy turret subclasses read its field.
            VehicleApi.Get(v,"CompFueledTravel");
            AerialAccuracy.Apply(__instance);
            if(ReferenceEquals(VehicleWeapons.Firing,__instance))return true;
            var target=(LocalTargetInfo)VehicleApi.Get(__instance,"targetInfo");
            AerialWeaponSolution.Lay(v,__instance,target);
            if(!AerialWeaponSolution.CanFire(v,__instance,target,out _)){Blocked++;return false;}
            firing=__instance;
            V3 bore=AerialWeaponSolution.Bore(v,__instance);
            float distance=(AerialWeaponSolution.Point(v,target)-VehicleWeapons.FlightMuzzle(v,__instance)).Length;
            firingDirection=AerialArmament.Bomb(__instance) ? bore : AerialAiAim.Scatter(v,bore,distance,AerialArmament.AiRange(__instance));
            Accepted++;return true;
        }
        public static void EndFire((object gun,V3 direction) __state)=>(firing,firingDirection)=__state;
        private static V3 ShotBore(Pawn aircraft,object gun) =>
            ReferenceEquals(gun,firing) ? firingDirection : AerialWeaponSolution.Bore(aircraft,gun);
        public static bool TargetRequirements(object __0,LocalTargetInfo __1,ref IntVec3 __2,ref bool __result)
        {
            if(!AerialWeaponSolution.Owns(__0,out var v))return true;
            if(ReferenceEquals(VehicleWeapons.Firing,__0))
            {
                if(VehicleProjectileRules.NativeAreaFlight(VehicleWeapons.ProjectileFor(__0)?.thingClass))return true;
                // VF needs a synthetic cell to launch. The 3D projectile traces
                // the bore and resolves actual cover, terrain and map boundaries.
                __2=__1.Cell;__result=__1.IsValid&&__2.InBounds(v.Map);return false;
            }
            if(VehicleControl.Owns(v))return true;
            __2=__1.Cell;__result=AerialWeaponSolution.Solve(v,__0,__1,out _,out _);return false;
        }
        public static void BeginCeAngle(out bool __state)
        {__state=ceAngle;ceAngle=AerialWeaponSolution.Owns(VehicleCeSpeed.Current,out _);}
        public static void EndCeAngle(bool __state)=>ceAngle=__state;
        public static void CeBaseAngle(ref float __result)
        {
            if(!ceAngle||!AerialWeaponSolution.Owns(VehicleCeSpeed.Current,out var v))return;
            V3 native=CeCompat.WorldVelocityToNative(ShotBore(v,VehicleCeSpeed.Current));
            if(CeNativeRules.TryAngles(native.X,native.Y,native.Z,out float angle,out _))__result=angle;
        }
        public static void AfterCeAngle(ref Vector2 __result)
        {
            if(!ceAngle||!AerialWeaponSolution.Owns(VehicleCeSpeed.Current,out var v))return;
            V3 native=CeCompat.WorldVelocityToNative(ShotBore(v,VehicleCeSpeed.Current));
            if(CeNativeRules.TryAngles(native.X,native.Y,native.Z,out _,out float heading))
                __result.x+=heading+VehicleApi.Float(VehicleCeSpeed.Current,"TurretRotation");
            CeAngles++;
        }
        public static void VanillaLaunch(Projectile __instance,Thing launcher,Vector3 origin,LocalTargetInfo usedTarget,LocalTargetInfo intendedTarget)
        {
            if(firing==null||launcher is not Pawn v||VehicleApi.Get(firing,"vehicle")!=v||__instance.Destroyed||CeCompat.NativeProjectile(__instance.def))return;
            if(VehicleProjectileRules.NativeAreaFlight(__instance.GetType()))return;
            V3 muzzle=VehicleWeapons.FlightMuzzle(v,firing),direction=ShotBore(v,firing);
            // Native VF has already sampled its miss/cover destination. Preserve
            // that angular error instead of replacing it with perfect target aim.
            Vector3 intended=intendedTarget.CenterVector3-origin,used=usedTarget.CenterVector3-origin;
            if(intended.sqrMagnitude>.001f&&used.sqrMagnitude>.001f)
                direction=VehicleModels.V(Quaternion.FromToRotation(intended.normalized,used.normalized)*VehicleModels.U(direction));
            Ballistics.AdoptVehicleRound(__instance,v,muzzle,direction,VehicleWeapons.NativeSpeed(firing,__instance.def),1,firing);VanillaShots++;
        }
    }
}
