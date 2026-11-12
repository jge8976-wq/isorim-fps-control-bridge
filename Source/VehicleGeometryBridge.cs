using System;
using System.Linq;
using HarmonyLib;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class VehicleGeometryBridge
    {
        private delegate bool TurretParts(Pawn vehicle,string bodyModel,object turret,Action<ModelHandle,Matrix4x4,string> add);
        private delegate bool HullParts(Pawn vehicle,string bodyModel,Action<ModelHandle,Matrix4x4,string> add);
        private sealed class Provider
        {
            internal TurretParts Turret;
            internal HullParts Hull;
            internal Func<Pawn,string,string,bool> Overlay;
        }
        private static readonly Provider[] Providers=new[]{"HSK.VehicleModels.VehicleGeometryApi","HSK.DmsVehicleModels.VehicleGeometryApi"}
            .Select(AccessTools.TypeByName).Where(t=>t!=null).Select(t=>new Provider
            {
                Turret=AccessTools.Method(t,"TryTurretParts").CreateDelegate<TurretParts>(),
                Hull=AccessTools.Method(t,"TryHullParts").CreateDelegate<HullParts>(),
                Overlay=AccessTools.Method(t,"CoversOverlay").CreateDelegate<Func<Pawn,string,string,bool>>()
            }).ToArray();

        internal static bool TryTurret(Pawn vehicle,VehicleViewDef profile,object turret,Action<ModelHandle,Matrix4x4,string> add)
            =>Providers.Any(p=>p.Turret(vehicle,profile.bodyModel,turret,add));
        internal static void Hull(Pawn vehicle,VehicleViewDef profile,Action<ModelHandle,Matrix4x4,string> add)
        {foreach(var provider in Providers)if(provider.Hull(vehicle,profile.bodyModel,add))return;}
        internal static bool CoversOverlay(Pawn vehicle,VehicleViewDef profile,string path)
            =>Providers.Any(p=>p.Overlay(vehicle,profile.bodyModel,path));
    }
}
