using System;
using System.Collections.Generic;
using System.IO;
using IsoRim;
using Verse;

namespace HSK.FpsBridge
{
    // Optional dedicated eye-level asset. It does not register a world ModelDef.
    public sealed class HeldWeaponVisualDef : Def
    {
        public ThingDef weapon;
        public string model;
        public HeldWeaponModelExtension view=new();
    }
    internal static class HeldWeaponVisuals
    {
        private static Dictionary<ThingDef,HeldWeaponVisualDef> byWeapon;
        private static readonly Dictionary<HeldWeaponVisualDef,Model> loaded=new();
        internal static HeldWeaponModel For(Thing weapon)
        {
            if(byWeapon==null)
            {
                byWeapon=new();
                foreach(var d in DefDatabase<HeldWeaponVisualDef>.AllDefsListForReading)
                    if(d.weapon!=null)byWeapon[d.weapon]=d;
            }
            if(!byWeapon.TryGetValue(weapon.def,out var def))return null;
            if(!loaded.TryGetValue(def,out Model model))
            {
                string root=Path.GetFullPath(Path.Combine(def.modContentPack.RootDir,"Models"));
                string path=Path.GetFullPath(Path.Combine(root,def.model+".obj"));
                if(!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Held model outside its Models folder");
                loaded[def]=model=ObjLoader.Load(path);
            }
            var v=def.view;
            return new HeldWeaponModel{model=model,id=def.defName,forward=v.forward,up=v.up,grip=v.hasGrip?v.grip:null,muzzle=v.hasMuzzle?v.muzzle:null,
                rawObjCoordinates=v.rawObjCoordinates,
                rearSight=v.hasSights?v.rearSight:null,frontSight=v.hasSights?v.frontSight:null,scopeEye=v.hasScopeEye?v.scopeEye:null,
                scopeForward=v.scopeForward.sqrMagnitude>.0001f?v.scopeForward:null,scope=v.scope,scopeZoom=v.scopeZoom,
                rearDistance=v.rearDistance,magnification=v.magnification,kind=v.kind};
        }
    }
}
