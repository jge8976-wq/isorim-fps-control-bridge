using System;
using System.Linq;
using System.Collections.Generic;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    public enum AerialPropulsion { None, Propeller, Rotor, Jet, VerticalThruster }
    // Positions are measured in scaled SOURCE-model coordinates, never AABB percentages.
    public sealed class AerialMountDef:Def
    {
        public string vehicleDef,turretKey,turretDef,bodyModel,weaponModel,pylonModel;
        public Vector3 mount,muzzle;
        public Vector3 pylonPosition,pylonAngles;
        public Vector3 pylonStretch=Vector3.one;
        public float weaponScale=1,pylonScale=1;
        public float barrelRadius=.04f,barrelLength=.3f;
    }
    internal static class AerialMounts
    {
        static readonly Dictionary<(Def,string),ModelHandle> loaded=new();
        internal static ModelHandle Load(AerialMountDef s,string path)
        {if(string.IsNullOrEmpty(path))return null;var key=((Def)s,path);if(!loaded.TryGetValue(key,out var m))loaded[key]=m=Api.LoadModel(s.modContentPack,path);return m;}
        internal static bool Draw(Pawn v,object t,Vector3 root,Quaternion hull)
        {
            var s=Spec(v,t);if(s==null)return false;var w=Load(s,s.weaponModel);var p=Load(s,s.pylonModel);if(w==null||p==null)return false;
            // Root is already in raw engine coordinates; all offsets remain source-local.
            Quaternion q=Fixed(v,t)?hull*Quaternion.FromToRotation(Vector3.forward,AerialDeparture.ModelForward(v)):AerialGeometry.Aim(t);
            Api.DrawModel(p,root+hull*s.pylonPosition,0,Matrix4x4.Rotate(hull*Quaternion.Euler(s.pylonAngles))*Matrix4x4.Scale(s.pylonStretch*s.pylonScale),Color.white);
            Api.DrawModel(w,root+hull*s.mount,0,Matrix4x4.Rotate(q)*Matrix4x4.Scale(Vector3.one*s.weaponScale),Color.white);
            return true;
        }
        internal static AerialMountDef Spec(Pawn v,object t)
        {
            if(v==null)return null;
            var choices=DefDatabase<AerialMountDef>.AllDefsListForReading.Where(d=>d.vehicleDef==v.def.defName&&d.turretKey==VehicleApi.TurretKey(t)&&d.bodyModel==VehicleModels.Profile(v).bodyModel);
            string def=(VehicleApi.Get(t,"def") as Def)?.defName;
            return choices.FirstOrDefault(d=>!string.IsNullOrEmpty(d.turretDef)&&d.turretDef==def)??choices.FirstOrDefault(d=>string.IsNullOrEmpty(d.turretDef));
        }
        internal static bool Fixed(Pawn v,object t) => v!=null&&AerialArmament.Trial(t)&&(AerialFlight.Profile(v)?.kind==AerialFlightKind.FixedWing
            || AerialArmament.Data(t)?.lockableMissile==true || AerialArmament.Data(t)?.kind?.StartsWith("Rocket",StringComparison.OrdinalIgnoreCase)==true || AerialArmament.Bomb(t));
        internal static Quaternion Rotation(Pawn v) => AerialGeometry.Hull(v)*Quaternion.FromToRotation(Vector3.forward,AerialDeparture.ModelForward(v));
        internal static VehicleWeaponView View(Pawn v,object t)
        {
            var s=Spec(v,t);if(s==null)return null;
            var d=AerialArmament.Data(t);
            return new VehicleWeaponView{turretKey=s.turretKey,mount=s.mount,muzzle=s.muzzle,sight=s.muzzle+new Vector3(0,.15f,-.2f),
                minPitch=Fixed(v,t)?0:-75,maxPitch=Fixed(v,t)?0:75,scopeTemplate=d?.scopeTemplate??VehicleScopeTemplate.MachineGun};
        }
    }
}
