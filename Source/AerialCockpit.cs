using System;
using System.Collections.Generic;
using System.Linq;
using IsoRim;
using UnityEngine;
using Verse;
namespace HSK.FpsBridge
{
    public sealed class AerialCockpitDef:Def
    {
        public List<string> vehicleDefs=new();
        public string model;
        public Vector3 eye=Vector3.zero;
    }
    internal static class AerialCockpit
    {
        static readonly Dictionary<AerialCockpitDef,ModelHandle> models=new();
        internal static int Submissions;
        internal static AerialCockpitDef Profile(Pawn v)=>v==null?null:DefDatabase<AerialCockpitDef>.AllDefsListForReading.FirstOrDefault(d=>d.vehicleDefs.Contains(v.def.defName));
        internal static bool Draw(Pawn v)
        {
            if(!VehicleControl.DriverCamera||VehicleControl.Vehicle!=v)return false;
            var p=Profile(v);if(p==null)return false;
            if(!models.TryGetValue(p,out var mesh))models[p]=mesh=Api.LoadModel(p.modContentPack,p.model);
            if(mesh==null)return false;
            Quaternion q=AerialMounts.Rotation(v);
            // Eye=(0,0,0) is authored by the art worker. The cabin follows the aircraft,
            // not mouse freelook; it is drawn before all screen HUDs and never paints over them.
            Vector3 at=VehicleControl.Eye()-q*p.eye;
            Api.DrawModel(mesh,AerialFlight.Raw(v,at),0,Matrix4x4.Rotate(q),Color.white);Submissions++;
            return true;
        }
    }
}
