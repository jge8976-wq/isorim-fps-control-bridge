using System;
using System.Collections.Generic;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
namespace HSK.FpsBridge
{
    internal sealed class HeldWeaponBinding
    {
        internal Model Model;
        internal Matrix4x4 ToView;
        internal string Scope;
        internal float RearDistance,Magnification,ScopeZoom;
        internal bool Iron;
    }
    internal static class HeldWeaponModels
    {
        private static readonly WeaponModels.Hold identity=new(){slab=Matrix4x4.identity};
        internal static int Generation=>HeldWeaponModelsApi.Generation+ModelLibrary.OverrideGeneration;
        internal static int Draws;
        internal static Model LastDrawModel;
        internal static ModelSpec Find(Thing weapon,bool fpsExact)
        {
            if(weapon?.def==null)return null;
            try
            {
                HeldWeaponModel info=HeldWeaponModelsApi.Provided(weapon)??HeldWeaponVisuals.For(weapon);
                var ext=weapon.def.GetModExtension<HeldWeaponModelExtension>();
                if(info==null)
                {
                    if(ext?.disableAutomatic==true||!ModelLibrary.TryGet(weapon.def,out var entry))return null;
                    // Core models keep the FPS library's calibrated sights. External art wins.
                    if(fpsExact&&entry.def.modContentPack?.PackageIdPlayerFacing?.Equals("peren.isorim17",StringComparison.OrdinalIgnoreCase)==true)return null;
                    Model model=WeaponModels.ModelOf(weapon);
                    if(model?.positions==null||model.positions.Length<3)return null;
                    info=new HeldWeaponModel{model=model,id=entry.def.defName,kind=weapon.def.IsMeleeWeapon?"Blade":"Gun"};
                    if(ext?.overrideOrientation!=true)
                    {
                        var hold=WeaponModels.HoldOf(model,weapon.Graphic.MatSingleFor(weapon),weapon.Graphic.drawSize,weapon.def.equippedAngleOffset);
                        if(hold==null)return null;
                        // Recover sprite-fit orientation only. Never import unequal sprite scales.
                        Vector3 size=new(weapon.Graphic.drawSize.x,weapon.Graphic.drawSize.y,1);
                        Vector3 along=Vector3.Scale(hold.tip-hold.butt,size).normalized;
                        Matrix4x4 fit=Matrix4x4.Scale(size)*hold.slab;
                        info.forward=fit.inverse.MultiplyVector(along).normalized;
                        info.up=fit.inverse.MultiplyVector(new Vector3(-along.y,along.x,0)).normalized;
                        info.muzzle=hold.slab.inverse.MultiplyPoint3x4(hold.tip);
                    }
                    if(ext!=null)
                    {
                        Vector3 At(Vector3 p)=>ext.rawObjCoordinates?new Vector3(p.x,p.y,-p.z):p;
                        if(ext.overrideOrientation){info.forward=At(ext.forward);info.up=At(ext.up);}
                        if(ext.hasGrip)info.grip=At(ext.grip);if(ext.hasMuzzle)info.muzzle=At(ext.muzzle);
                        if(ext.hasSights){info.rearSight=At(ext.rearSight);info.frontSight=At(ext.frontSight);}
                        if(ext.hasScopeEye){info.scopeEye=At(ext.scopeEye);if(ext.scopeForward.sqrMagnitude>.0001f)info.scopeForward=At(ext.scopeForward);}
                        info.scope=ext.scope;info.rearDistance=ext.rearDistance;info.magnification=ext.magnification;
                        info.scopeZoom=ext.scopeZoom;
                        if(!string.IsNullOrEmpty(ext.kind))info.kind=ext.kind;
                    }
                }
                return Make(weapon,info);
            }
            catch(Exception e){Log.WarningOnce("[FpsBridge] Held 3D model unavailable; FPS fallback: "+weapon.def.defName+"; "+e.Message,weapon.def.shortHash+15390000);return null;}
        }
        internal static ModelSpec Make(Thing weapon,HeldWeaponModel info)
        {
            if(info?.model?.positions==null||info.model.positions.Length<3)return null;
            if(!Finite(info.forward)||!Finite(info.up)||info.forward.sqrMagnitude<.0001f||Vector3.Cross(info.forward,info.up).sqrMagnitude<.0001f)return null;
            Vector3 At(Vector3 p)=>info.rawObjCoordinates?new Vector3(p.x,p.y,-p.z):p;
            Quaternion rotation=Quaternion.Inverse(Quaternion.LookRotation(At(info.forward),At(info.up)));
            Vector3 min=new(float.MaxValue,float.MaxValue,float.MaxValue),max=-min;
            foreach(Vector3 raw in info.model.positions){if(!Finite(raw))return null;Vector3 p=rotation*raw;min=Vector3.Min(min,p);max=Vector3.Max(max,p);}
            if((max-min).z<.005f||(max-min).y<.001f||(max-min).x<.001f)return null;
            Vector3 grip=info.grip.HasValue?rotation*At(info.grip.Value):new Vector3((min.x+max.x)*.5f,min.y+(max.y-min.y)*.25f,min.z+(max.z-min.z)*.3f);
            if(!Finite(grip))return null;
            var anchors=new Dictionary<string,V3>();
            void Anchor(string key,Vector3 p){p-=grip;anchors[key]=new V3(p.x,p.y,p.z);}
            Anchor("VisualMuzzle",info.muzzle.HasValue?rotation*At(info.muzzle.Value):new Vector3((min.x+max.x)*.5f,(min.y+max.y)*.5f,max.z));
            bool iron=info.rearSight.HasValue&&info.frontSight.HasValue;
            if(iron&&(!Finite(info.rearSight.Value)||!Finite(info.frontSight.Value)||(info.frontSight.Value-info.rearSight.Value).sqrMagnitude<.0009f))return null;
            if(info.muzzle.HasValue&&!Finite(info.muzzle.Value))return null;
            if(iron){Anchor("RearSightAperture",rotation*At(info.rearSight.Value));Anchor("FrontSightTip",rotation*At(info.frontSight.Value));}
            if(info.scopeEye.HasValue)
            {
                Vector3 axis=info.scopeForward??info.forward;
                if(!Finite(info.scopeEye.Value)||!Finite(axis)||axis.sqrMagnitude<.0001f)return null;
                Anchor("ScopeEye",rotation*At(info.scopeEye.Value));
                Anchor("ScopeFront",rotation*At(info.scopeEye.Value+axis.normalized));
            }
            ModelKind kind=Enum.TryParse(info.kind,out ModelKind k)?k:ModelKind.Gun;if(weapon.def.IsMeleeWeapon)kind=ModelKind.Blade;
            ScopeStyle scope;
            if(!Enum.TryParse(info.scope,out scope))
            {
                scope=ScopeRules.Style(weapon.def.defName);
                if(info.scopeEye.HasValue&&scope==ScopeStyle.None)
                {
                    float range=weapon.def.Verbs?.Count>0?weapon.def.Verbs[0].range:0f;
                    scope=ScopeRules.TurretStyle(weapon.def.techLevel>=TechLevel.Spacer,range);
                }
            }
            if(kind==ModelKind.Blade||kind==ModelKind.Grenade)scope=ScopeStyle.None;
            min-=grip;max-=grip;
            return new ModelSpec(weapon.def.defName,"IsoRim:"+(info.id??weapon.def.defName),kind,"","",new V3(min.x,min.y,min.z),new V3(max.x,max.y,max.z),anchors)
            {Held=new HeldWeaponBinding{Model=info.model,ToView=Matrix4x4.Translate(-grip)*Matrix4x4.Rotate(rotation),Scope=scope.ToString(),
                RearDistance=Mathf.Clamp(info.rearDistance,.08f,.4f),Magnification=Mathf.Clamp(info.magnification,1f,2f),Iron=iron,
                ScopeZoom=float.IsFinite(info.scopeZoom)&&info.scopeZoom>0?Mathf.Clamp(info.scopeZoom,1f,ScopeRules.MaxZoom(scope)):ScopeRules.DefaultZoom(scope)}};
        }
        private static bool Finite(Vector3 v)=>float.IsFinite(v.x)&&float.IsFinite(v.y)&&float.IsFinite(v.z);
        internal static void Draw(Thing weapon,ModelSpec spec,Matrix4x4 render)
        {
            Model display=HeldWeaponAppearance.For(spec.Held.Model,FpsMod.Settings.firstPersonWeaponOutlines,FpsMod.Settings.readableHeldWeapons);
            LastDrawModel=display;
            if(!HeldWeaponLighting.Draw(display,R3DTextures.ModelTint(weapon),render*spec.Held.ToView))
                WeaponModels.Draw(weapon,display,identity,render*spec.Held.ToView);
            Draws++;
        }
    }
}
