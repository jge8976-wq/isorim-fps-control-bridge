using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HSK.FpsBridge
{
    internal static class WeaponCompatibility
    {
        private static readonly Dictionary<Type,bool> ordinary=new Dictionary<Type,bool>();
        private static readonly Dictionary<Type,bool> flights=new Dictionary<Type,bool>();
        internal static bool Pulse(ThingDef projectile) => projectile?.defName=="Bullet_RK_PrototypePulseRifleLight"
            || projectile?.defName=="Bullet_RK_PrototypePulseRifleHeavy";
        internal static bool Ordinary(Verb verb)
        {
            if (!(verb is Verb_Shoot) || !SafeProjectile((verb as Verb_LaunchProjectile)?.Projectile)
                || !typeof(Bullet).IsAssignableFrom(((Verb_LaunchProjectile)verb).Projectile.thingClass)) return false;
            Type type=verb.GetType();
            // Known pulse rifle only overrides mode/aim/XP wrappers, with the ordinary launch implementation.
            if (verb.EquipmentSource?.def.defName=="RK_PrototypePulseRifle" && type.FullName=="NewRatkin.Verb_PulseRifleShoot") return true;
            if(verb.verbProps.consumeFuelPerShot>0f)return false;
            // Native custom impacts (AP continuations, explosions, etc.) retain the real shot sequence.
            if(((Verb_LaunchProjectile)verb).Projectile.thingClass!=typeof(Bullet))return false;
            if (!ordinary.TryGetValue(type,out bool simple))
            {
                var shot=AccessTools.Method(type,"TryCastShot",Type.EmptyTypes);
                var warmup=AccessTools.Method(type,"WarmupComplete",Type.EmptyTypes);
                simple=(shot?.DeclaringType==typeof(Verb_LaunchProjectile) || shot?.DeclaringType==typeof(Verb_Shoot))
                    && (warmup?.DeclaringType==typeof(Verb_Shoot) || warmup?.DeclaringType==typeof(Verb_LaunchProjectile));
                ordinary[type]=simple;
            }
            if (!simple || verb.EquipmentSource?.def.comps==null)return simple;
            foreach(var comp in verb.EquipmentSource.def.comps)
            {
                string name=comp.GetType().Name+"|"+comp.compClass?.Name;
                foreach(string resource in new[]{"Reloadable","AmmoUser","ChangeableProjectile","Refuelable","Charged","EnergyWeapon"})
                    if(name.IndexOf(resource,StringComparison.OrdinalIgnoreCase)>=0)return false;
                if(comp.GetType().Assembly!=typeof(ThingComp).Assembly
                    || comp.compClass!=null && comp.compClass.Assembly!=typeof(ThingComp).Assembly)return false;
            }
            return true;
        }
        internal static bool SafeProjectile(ThingDef projectile)
        {
            Type type=projectile?.thingClass;
            if(type==null || projectile.projectile==null || projectile.projectile.flyOverhead)return false;
            if(type.FullName==RatkinTurretShots.BoltClass || type.FullName==RatkinTurretShots.ShellClass)return RatkinTurretShots.Enabled;
            if(!typeof(Bullet).IsAssignableFrom(type) && type.FullName!=RatkinTurretShots.BoltClass)return false;
            if(!flights.TryGetValue(type,out bool safe))
            {
                safe=true;
                foreach(string name in new[]{"Tick","TickInterval","Launch","ImpactSomething"})
                {
                    var method=name=="Launch"?AccessTools.Method(type,name,new[]{typeof(Thing),typeof(UnityEngine.Vector3),typeof(LocalTargetInfo),typeof(LocalTargetInfo),typeof(ProjectileHitFlags),typeof(bool),typeof(Thing),typeof(ThingDef)}):AccessTools.Method(type,name);
                    if(method!=null && method.DeclaringType!=typeof(Projectile) && method.DeclaringType!=typeof(Bullet))safe=false;
                }
                flights[type]=safe;
            }
            return safe;
        }
        internal static bool Native3D(Verb verb) => verb is Verb_LaunchProjectile v && SafeProjectile(v.Projectile);
    }
}
