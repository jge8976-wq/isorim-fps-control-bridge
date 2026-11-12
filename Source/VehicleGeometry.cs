using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using IsoRim;
using UnityEngine;
using Verse;
namespace HSK.FpsBridge
{
    // Physical model placements and rendering use the same plan. Camera-only
    // hiding never removes a hull from physics; no shadow/preview/effect meshes.
    internal static class VehicleGeometry
    {
        internal readonly struct Part
        {
            internal readonly ModelHandle Mesh;internal readonly Matrix4x4 World;internal readonly string Role;
            internal Part(ModelHandle mesh,Vector3 at,Quaternion q,Matrix4x4 local,string role)
            {Mesh=mesh;World=Matrix4x4.TRS(at,q,Vector3.one)*local;Role=role;}
        }
        internal static readonly Dictionary<string,string> Missing=new();
        internal static AerialMotor Pose(Pawn v)
        {
            if(AerialFlight.TryGet(v,out var r))return r.Motor;
            if(VehicleDriving.ActiveFor(v))return VehicleDriving.PhysicalPose;
            if(VehicleContacts.RecoveredPose(v,out var pushed))return pushed;
            V3 at=FloorSpace.Spot(v);if(AerialFlight.Supports(v))at.Y-=AerialGeometry.LocalBounds(v).min.y;
            return new AerialMotor(at,VehicleApi.Yaw(v));
        }
        internal static bool Parts(Pawn v,AerialMotor pose,List<Part> into)
        {
            into.Clear();var profile=VehicleModels.Profile(v);bool air=AerialFlight.Supports(v);
            if(string.IsNullOrEmpty(profile.bodyModel)){Missing.Remove(v.def.defName);return false;}
            ModelHandle body=VehicleModels.Load(profile,profile.bodyModel);
            if(body==null)return Fail(v,"body:"+profile.bodyModel);
            Vector3 root=VehicleModels.U(pose.Position);Quaternion hull=air?AerialGeometry.Rotation(pose)*AerialGeometry.SourceRotation(v):Quaternion.Euler(0,pose.Yaw,0);
            Matrix4x4 scale=Matrix4x4.Scale(profile.modelScale);
            into.Add(new Part(body,root,hull,scale,"body"));
            void Imported(ModelHandle mesh,Matrix4x4 local,string role)=>into.Add(new Part(mesh,root,hull,local,role));
            try{VehicleGeometryBridge.Hull(v,profile,Imported);}
            catch(Exception e){return Fail(v,"imported hull parts:"+e.Message);}
            Quaternion Turret(object t)=>air?hull*Quaternion.Euler(0,Mathf.DeltaAngle(pose.Yaw,VehicleApi.Float(t,"TurretRotation")),0):Quaternion.Euler(0,VehicleAimAlignment.TurretYaw(v,t),0);
            Quaternion Aim(object t)=>AerialMounts.Fixed(v,t)?hull*Quaternion.FromToRotation(Vector3.forward,AerialDeparture.ModelForward(v)):Quaternion.Euler(VehicleWeapons.PitchOf(t),VehicleAimAlignment.TurretYaw(v,t),0);
            Vector3 Mount(object t,int depth=0)
            {
                var p=VehicleModels.Weapon(v,t);
                if(!string.IsNullOrEmpty(p.parentTurretKey)&&depth<8)
                {
                    var parent=VehicleApi.Turrets(v).FirstOrDefault(t2=>VehicleApi.TurretKey(t2)==p.parentTurretKey);
                    if(parent!=null&&parent!=t)
                    {
                        if(p.boreFollowsParentPitch)
                        {
                            Vector3 pivot=VehicleModels.Weapon(v,parent).barrelPivot;
                            return Mount(parent,depth+1)+Turret(parent)*pivot+Aim(parent)*(p.mount-pivot);
                        }
                        return Mount(parent,depth+1)+Turret(parent)*p.mount;
                    }
                }
                return root+hull*p.mount;
            }
            foreach(object t in VehicleApi.Turrets(v))
            {
                if(!VehicleApi.Bool(t,"ShouldDraw"))continue;
                if(air&&AerialArmament.Trial(t)&&AerialMounts.Spec(v,t) is {} s)
                {
                    var weapon=AerialMounts.Load(s,s.weaponModel);var pylon=AerialMounts.Load(s,s.pylonModel);
                    if(weapon==null||pylon==null)return Fail(v,"installed hardpoint:"+s.defName);
                    into.Add(new Part(pylon,root+hull*s.pylonPosition,hull*Quaternion.Euler(s.pylonAngles),Matrix4x4.Scale(s.pylonStretch*s.pylonScale),"pylon:"+s.defName));
                    into.Add(new Part(weapon,root+hull*s.mount,Aim(t),Matrix4x4.Scale(Vector3.one*s.weaponScale),"weapon:"+s.defName));continue;
                }
                bool imported;
                try{imported=VehicleGeometryBridge.TryTurret(v,profile,t,Imported);}
                catch(Exception e){return Fail(v,"imported turret:"+VehicleApi.TurretKey(t)+"; "+e.Message);}
                if(imported)continue;
                var p=AerialArmament.View(v,t)??VehicleModels.ExplicitWeapon(v,t);if(p==null)return Fail(v,"unmapped active turret:"+VehicleApi.TurretKey(t));if(p.modelInParent)continue;
                Vector3 mount=Mount(t);object fixedParent=p.boreFixedToParent?VehicleAimAlignment.FixedParent(v,t):null;
                object boreParent=VehicleAimAlignment.BoreParent(v,t);
                Quaternion q=boreParent!=null?Aim(boreParent):p.boreFixedToParent?(fixedParent==null?hull:Turret(fixedParent)):Turret(t);
                Quaternion barrelLocal=Quaternion.Euler(p.boreFixedToParent||boreParent!=null?0f:VehicleWeapons.PitchOf(t),0,0)*Quaternion.Euler(p.barrelRestAngles);
                bool Add(string path,Vector3 at,Quaternion rotation,Matrix4x4 transform,string role)
                {if(string.IsNullOrEmpty(path))return true;var mesh=VehicleModels.Load(profile,path);if(mesh==null)return false;into.Add(new Part(mesh,at,rotation,transform,role));return true;}
                if(!Add(p.turretModel,mount,q,scale,"turret")||!Add(p.mountRingModel,mount,q,scale,"bearing")
                    ||!Add(p.barrelModel,air?mount+q*p.barrelPivot:mount,air?Aim(t):q,
                        air?Matrix4x4.Rotate(Quaternion.Euler(p.barrelRestAngles))*scale:Matrix4x4.TRS(p.barrelPivot,barrelLocal,profile.modelScale),"barrel"))return Fail(v,"turret:"+VehicleApi.TurretKey(t));
            }
            if(profile.attachments.Count>0)
            {
                object renderer=VehicleApi.Get(VehicleApi.Get(v,"DrawTracker"),"overlayRenderer");
                foreach(object overlay in VehicleApi.List(renderer,"AllOverlaysListForReading"))
                {
                    object data=VehicleApi.Get(overlay,"data"),requirement=VehicleApi.Get(data,"component");
                    if(requirement!=null&&!VehicleApi.Bool(requirement,"MeetsRequirements"))continue;
                    string path=VehicleApi.Get(VehicleApi.Get(data,"graphicData"),"texPath") as string;
                    var part=profile.attachments.FirstOrDefault(p=>p.graphicPath==path);var mesh=part==null?null:VehicleModels.Load(profile,part.model);
                    if(part==null&&(VehicleAttachmentBridge.BodyContainsOverlay(v,profile,path)||VehicleGeometryBridge.CoversOverlay(v,profile,path)))continue;
                    if(mesh==null)return Fail(v,"active overlay:"+path);
                    float angle=VehicleApi.Float(data,"rotation");
                    float nativeAngle=VehicleApi.Float(VehicleApi.Get(overlay,"Transform"),"rotation");
                    AerialFlightRecord flight=null;
                    if(air)AerialFlight.TryGet(v,out flight);
                    Matrix4x4 local=AerialEffects.Attachment(v,profile,part,mesh,angle,nativeAngle,flight?.RotorAngle);
                    into.Add(new Part(mesh,root,hull,local,"overlay:"+path));
                }
            }
            if(profile.rotors.Count>0)
            {
                var overlays=VehicleApi.List(VehicleApi.Get(VehicleApi.Get(v,"DrawTracker"),"overlayRenderer"),"AllOverlaysListForReading").ToArray();
                AerialFlight.TryGet(v,out var flight);
                foreach(var rotor in profile.rotors)
                {
                    if(rotor.overlayOrdinal<0||rotor.overlayOrdinal>=overlays.Length)return Fail(v,"rotor overlay:"+rotor.overlayOrdinal);
                    object overlay=overlays[rotor.overlayOrdinal],data=VehicleApi.Get(overlay,"data"),requirement=VehicleApi.Get(data,"component");
                    if(requirement!=null&&!VehicleApi.Bool(requirement,"MeetsRequirements"))continue;
                    var mesh=VehicleModels.Load(profile,rotor.model);
                    if(mesh==null)return Fail(v,"rotor mesh:"+rotor.model);
                    float angle=VehicleApi.Float(data,"rotation")+(flight?.RotorAngle??VehicleApi.Float(VehicleApi.Get(overlay,"Transform"),"rotation"));
                    Vector3 mount=Vector3.Scale(rotor.mount,profile.modelScale);
                    into.Add(new Part(mesh,root+hull*mount,hull*Quaternion.Euler(0,angle,0),scale,"rotor:"+rotor.model));
                }
            }
            Missing.Remove(v.def.defName);return true;
        }
        static bool Fail(Pawn v,string reason)
        {if(!Missing.TryGetValue(v.def.defName,out string old)||old!=reason){Missing[v.def.defName]=reason;Log.Warning("[FpsBridge] VEHICLE_GEOMETRY_MISSING def="+v.def.defName+"; "+reason+"; no proxy box substituted");}return false;}
        internal static void Draw(Pawn v,List<Part> parts)
        {
            var visibility = VehicleFollowVisibility.BeginModels(v);
            try
            {
            foreach(var p in parts)
            {
                Vector3 at=p.World.MultiplyPoint3x4(Vector3.zero);Matrix4x4 local=p.World;local.m03=local.m13=local.m23=0;
                Vector3 raw=AerialFlight.Supports(v)?AerialFlight.Raw(v,at):at;
                Api.DrawModel(p.Mesh,raw,0,local,v.DrawColor);
                if(p.Role.StartsWith("rotor:",StringComparison.Ordinal)||p.Role.StartsWith("overlay:",StringComparison.Ordinal)&&AerialEffects.IsRotor(p.Role))AerialEffects.RotorSubmissions++;
            }
            }
            finally { VehicleFollowVisibility.AfterDraw(null, visibility); }
        }
    }
}
