using System;
using System.Linq;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    public sealed class AerialWeaponExtension:DefModExtension
    {
        public string kind,ceAmmoSet;
        public float aiRange, vanillaSpreadDegrees;
        public bool quickFire;
        public bool lockableMissile;
        public bool freeFallBomb;
        public float lockSeconds=1.5f, seekerDegrees=25f, guidanceDegreesPerSecond=45f;
        public Vector3 mountFraction;
        public int barrels=1;
        public float barrelRadius=.07f,barrelLength=.65f;
        public VehicleScopeTemplate scopeTemplate=VehicleScopeTemplate.MachineGun;
    }
    internal static class AerialArmament
    {
        internal static AerialWeaponExtension Data(object turret)=>(VehicleApi.Get(turret,"def") as Def)?.GetModExtension<AerialWeaponExtension>();
        internal static float AiRange(object gun)
        {
            float configured = Data(gun)?.aiRange ?? 0f;
            if (configured > 0f && float.IsFinite(configured)) return configured;
            float native = VehicleApi.Float(gun, "MaxRange");
            // Unclassified airframe weapons retain shorter native ranges. CE's
            // mounted M240B is the upper bound for unclassified long-range stores.
            return float.IsFinite(native) ? Math.Clamp(native, 0f, 62f) : 0f;
        }
        internal static bool Trial(object turret)=>turret!=null&&(Data(turret)!=null||(VehicleApi.TurretKey(turret)??"").StartsWith("FPS_AirTrial",StringComparison.Ordinal));
        internal static bool QuickGun(object turret)=>Data(turret)?.quickFire??(Trial(turret)&&!VehicleApi.TurretKey(turret).Contains("Rocket"));
        internal static bool Bomb(object gun)=>Data(gun)?.freeFallBomb==true;
        internal static V3 ReleaseVelocity(Pawn v,object gun,V3 direction,float speed)
        {if(Bomb(gun)&&AerialFlight.TryGet(v,out var f))return f.Motor.Velocity*(1f/60f)+new V3(0,-.03f,0);return direction*speed;}
        internal static void Ensure(Pawn v)
        {
            if(!AerialFlight.Supports(v))return;
            object comp=VehicleApi.Get(v,"CompVehicleTurrets");if(comp==null)return;
            // Only retire our two R4-R8 temporary mounts. VF returns chambered rounds to cargo;
            // native/mod turrets and all other cargo remain owned by their original systems.
            if(VehicleApi.List(VehicleApi.Get(comp,"Props"),"turrets").Any(p=>Data(p)!=null))
                foreach(object old in VehicleApi.Turrets(v).Where(t=>(VehicleApi.TurretKey(t)??"").StartsWith("FPS_AirTrial",StringComparison.Ordinal)).ToArray())
                    VehicleApi.Call(comp,"RemoveTurret",old);
            foreach(object prototype in VehicleApi.List(VehicleApi.Get(comp,"Props"),"turrets").Where(Trial))
                if(!VehicleApi.Turrets(v).Any(t=>VehicleApi.TurretKey(t)==VehicleApi.TurretKey(prototype)))VehicleApi.Call(comp,"CopyAndAddTurret",prototype,null);
            foreach(object gun in VehicleApi.Turrets(v).Where(Trial)){VehicleApi.Set(gun,"autoTargeting",false);AerialAudio.Apply(gun);AerialAccuracy.Apply(gun);}
        }
        internal static VehicleWeaponView View(Pawn v,object turret)
        {
            if(!Trial(turret))return null;
            var calibrated=AerialMounts.View(v,turret);if(calibrated!=null)return calibrated;
            var data=Data(turret);
            if(data!=null)
            {
                Bounds local=AerialGeometry.LocalBounds(v);Vector3 f=data.mountFraction;
                return new VehicleWeaponView{turretKey=VehicleApi.TurretKey(turret),mount=new Vector3(local.center.x+f.x*local.extents.x,local.min.y+f.y*local.size.y,local.center.z+f.z*local.extents.z),
                    muzzle=new Vector3(0,0,data.barrelLength*2),sight=new Vector3(.1f,.18f,data.barrelLength),minPitch=-75,maxPitch=75,scopeTemplate=data.scopeTemplate};
            }
            bool rocket=VehicleApi.TurretKey(turret).Contains("Rocket");Bounds b=AerialGeometry.LocalBounds(v);
            return new VehicleWeaponView{turretKey=VehicleApi.TurretKey(turret),mount=new Vector3(rocket?b.extents.x*.75f:-b.extents.x*.75f,b.center.y,b.max.z*.65f),
                muzzle=new Vector3(0,0,1.1f),sight=new Vector3(.1f,.15f,.75f),minPitch=-45,maxPitch=80,scopeTemplate=rocket?VehicleScopeTemplate.Rocket:VehicleScopeTemplate.MachineGun};
        }
    }
}
