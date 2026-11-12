using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Linq;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    public sealed class AerialDepartureModelDef:Def
    {
        public string vehicleDef,modelPath,sha256;
        public Vector3 localForward=Vector3.forward;
    }
    // Render a held vehicle without respawning it or moving its map cell. The VF
    // skyfaller remains the sole owner until its native LeaveMap transfers it.
    public static class AerialDeparture
    {
        public const int DurationTicks=120;
        public static int BodySubmissions,PartSubmissions;
        static readonly Dictionary<VehicleViewDef,Vector3> forwards=new Dictionary<VehicleViewDef,Vector3>();
        public static Vector3 ModelForward(Pawn v)
        {
            var p=VehicleModels.Profile(v);if(forwards.TryGetValue(p,out var result))return result;
            result=Vector3.forward;
            var binding=DefDatabase<AerialDepartureModelDef>.AllDefsListForReading.FirstOrDefault(d=>d.vehicleDef==v.def.defName&&d.modelPath==p.bodyModel);
            if(binding!=null&&p.modContentPack!=null)
            {
                string path=Path.Combine(p.modContentPack.RootDir,p.bodyModel);
                if(File.Exists(path)&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(binding.sha256,StringComparison.OrdinalIgnoreCase))
                    // Bindings describe the frozen raw OBJ; IsoRim's loader reflects its Z.
                    result=new Vector3(binding.localForward.x,binding.localForward.y,-binding.localForward.z).normalized;
            }
            forwards[p]=result;return result;
        }
        public static bool CanRender(Pawn v)
        {
            if(v==null||!Api.ModelsOn||VehicleApi.Get(v.def,"type")?.ToString()!="Air")return false;
            var p=VehicleModels.Profile(v);
            if(string.IsNullOrEmpty(p.bodyModel)||VehicleModels.Load(p,p.bodyModel)==null)return false;
            foreach(var rotor in p.rotors)if(VehicleModels.Load(p,rotor.model)==null)return false;
            foreach(var t in VehicleApi.Turrets(v))
            {
                var w=VehicleModels.Weapon(v,t);
                foreach(string path in new[]{w.turretModel,w.mountRingModel,w.barrelModel})
                    if(!string.IsNullOrEmpty(path)&&VehicleModels.Load(p,path)==null)return false;
            }
            object overlays=VehicleApi.Get(VehicleApi.Get(v,"DrawTracker"),"overlayRenderer");
            var nativeRotors=VehicleApi.List(overlays,"AllOverlaysListForReading").ToArray();
            foreach(var rotor in p.rotors)if(rotor.overlayOrdinal<0||rotor.overlayOrdinal>=nativeRotors.Length)return false;
            foreach(object o in VehicleApi.List(overlays,"AllOverlaysListForReading"))
            {
                object data=VehicleApi.Get(o,"data"),requirement=VehicleApi.Get(data,"component");
                if(requirement!=null&&!VehicleApi.Bool(requirement,"MeetsRequirements"))continue;
                string path=VehicleApi.Get(VehicleApi.Get(data,"graphicData"),"texPath") as string;
                var a=p.attachments.FirstOrDefault(x=>x.graphicPath==path);
                if(a==null&&VehicleAttachmentBridge.BodyContainsOverlay(v,p,path))continue;
                if(a==null||VehicleModels.Load(p,a.model)==null)return false;
            }
            return true;
        }
        public static void Plan(Pawn v,Map map,IntVec3 cell,out Vector3 start,out Vector3 end,out float yaw)
        {
            var band=FloorSpace.Band(map,cell);Bounds b=AerialGeometry.LocalBounds(v);
            Vector3 c=(Vector3)VehicleApi.Call(v,"TrueCenter",cell,0f);
            start=new Vector3(c.x,band.Height-b.min.y,c.z-band.Shift);
            yaw=VehicleApi.NativeYaw(v);
            Vector3 forward=Quaternion.Euler(0,yaw,0)*Vector3.forward;
            float margin=Math.Max(b.size.x,b.size.z)+3f;
            // Map coordinates are raw; convert the endpoint back into the same floor band.
            float dx=forward.x>1e-4f?(map.Size.x+margin-c.x)/forward.x:forward.x< -1e-4f?(-margin-c.x)/forward.x:float.PositiveInfinity;
            float dz=forward.z>1e-4f?(map.Size.z+margin-c.z)/forward.z:forward.z< -1e-4f?(-margin-c.z)/forward.z:float.PositiveInfinity;
            float distance=Math.Max(1,Math.Min(dx,dz));
            end=start+forward*distance;end.y=start.y+12f;
        }
        public static Vector3 Sample(Vector3 start,Vector3 end,int elapsed)
        {
            float t=Mathf.Clamp01(elapsed/(float)DurationTicks);
            // A short lift then accelerating outbound flight; always reaches the edge.
            float travel=t*t;
            Vector3 p=Vector3.Lerp(start,end,travel);
            p.y=Mathf.Lerp(start.y,end.y,Mathf.SmoothStep(0,1,Mathf.Clamp01(t*3)));
            return p;
        }
        public static void Draw(Pawn v,Map map,IntVec3 cell,Vector3 root,float yaw,int elapsed)
        {
            if(!Api.ViewReady||!Api.ModelsOn)return;
            var p=VehicleModels.Profile(v);var band=FloorSpace.Band(map,cell);
            Vector3 raw=new Vector3(root.x,root.y-band.Height,root.z+band.Shift);
            Quaternion hull=Quaternion.Euler(0,yaw,0)*AerialGeometry.SourceRotation(v);
            void Submit(ModelHandle mesh,Vector3 at,Quaternion q,Matrix4x4 local)
            {if(mesh!=null){Api.DrawModel(mesh,at,0,q==hull?Matrix4x4.Rotate(hull)*local:Matrix4x4.Rotate(q)*local,v.DrawColor);PartSubmissions++;}}
            Submit(VehicleModels.Load(p,p.bodyModel),raw,hull,Matrix4x4.Scale(p.modelScale));BodySubmissions++;
            Vector3 Mount(object t,int depth=0)
            {
                var w=VehicleModels.Weapon(v,t);
                object parent=string.IsNullOrEmpty(w.parentTurretKey)?null:VehicleApi.Turrets(v).FirstOrDefault(x=>VehicleApi.TurretKey(x)==w.parentTurretKey);
                return parent!=null&&parent!=t&&depth<8?Mount(parent,depth+1)+Turret(parent)*w.mount:raw+hull*w.mount;
            }
            Quaternion Turret(object t)=>AerialMounts.Fixed(v,t)?hull*Quaternion.FromToRotation(Vector3.forward,ModelForward(v)):hull*Quaternion.Euler(0,Mathf.DeltaAngle(VehicleApi.NativeYaw(v),VehicleApi.Float(t,"TurretRotation")),0);
            foreach(object t in VehicleApi.Turrets(v))
            {
                if(!VehicleApi.Bool(t,"ShouldDraw"))continue;
                var w=VehicleModels.Weapon(v,t);if(w.modelInParent)continue;
                Vector3 mount=Mount(t);Quaternion q=Turret(t);
                Submit(VehicleModels.Load(p,w.turretModel),mount,q,Matrix4x4.Scale(p.modelScale));
                Submit(VehicleModels.Load(p,w.mountRingModel),mount,q,Matrix4x4.Scale(p.modelScale));
                Submit(VehicleModels.Load(p,w.barrelModel),mount+q*w.barrelPivot,q*Quaternion.Euler(VehicleWeapons.PitchOf(t),0,0),Matrix4x4.Scale(p.modelScale));
                if(AerialArmament.Trial(t)&&!AerialMounts.Draw(v,t,raw,hull))Visuals.AerialWeapon(mount+q*w.muzzle,q,VehicleApi.TurretKey(t).Contains("Rocket"),AerialArmament.Data(t));
            }
            object overlays=VehicleApi.Get(VehicleApi.Get(v,"DrawTracker"),"overlayRenderer");
            var nativeRotors=VehicleApi.List(overlays,"AllOverlaysListForReading").ToArray();
            foreach(var rotor in p.rotors)
            {
                if(rotor.overlayOrdinal<0||rotor.overlayOrdinal>=nativeRotors.Length)continue;
                object overlay=nativeRotors[rotor.overlayOrdinal],data=VehicleApi.Get(overlay,"data"),requirement=VehicleApi.Get(data,"component");
                if(requirement!=null&&!VehicleApi.Bool(requirement,"MeetsRequirements"))continue;
                float angle=VehicleApi.Float(VehicleApi.Get(overlay,"Transform"),"rotation")+VehicleApi.Float(data,"rotation");
                Submit(VehicleModels.Load(p,rotor.model),raw+hull*Vector3.Scale(rotor.mount,p.modelScale),hull*Quaternion.Euler(0,angle,0),Matrix4x4.Scale(p.modelScale));
            }
            foreach(object o in VehicleApi.List(overlays,"AllOverlaysListForReading"))
            {
                object data=VehicleApi.Get(o,"data"),requirement=VehicleApi.Get(data,"component");
                if(requirement!=null&&!VehicleApi.Bool(requirement,"MeetsRequirements"))continue;
                string path=VehicleApi.Get(VehicleApi.Get(data,"graphicData"),"texPath") as string;
                var a=p.attachments.FirstOrDefault(x=>x.graphicPath==path);if(a==null)continue;
                var mesh=VehicleModels.Load(p,a.model);if(mesh==null)continue;
                float angle=VehicleApi.Float(data,"rotation");
                float nativeAngle=VehicleApi.Float(VehicleApi.Get(o,"Transform"),"rotation");
                Submit(mesh,raw,hull,AerialEffects.Attachment(v,p,a,mesh,angle,nativeAngle,elapsed*24f));
            }
        }
    }
}
