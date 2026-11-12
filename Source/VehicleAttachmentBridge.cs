using System;
using System.Collections;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class VehicleAttachmentBridge
    {
        private delegate bool Transform(Pawn vehicle, string bodyModel, string graphicPath, string model, float angle, out Matrix4x4 local);
        private static readonly Type api = AccessTools.TypeByName("HSK.VehicleModels.VehicleAttachmentApi");
        private static readonly Transform transform = api == null ? null : AccessTools.DeclaredMethod(api, "TryLocalTransform",
            new[] { typeof(Pawn), typeof(string), typeof(string), typeof(string), typeof(float), typeof(Matrix4x4).MakeByRefType() })
            ?.CreateDelegate<Transform>();
        private static readonly Func<Pawn, string, string, bool> coverage = api == null ? null : AccessTools.DeclaredMethod(api, "BodyContainsOverlay",
            new[] { typeof(Pawn), typeof(string), typeof(string) })?.CreateDelegate<Func<Pawn, string, string, bool>>();

        internal static bool TryLocalTransform(Pawn vehicle, VehicleViewDef profile, VehicleAttachmentView part, float angle, out Matrix4x4 local)
        {
            local = default;
            return transform != null && transform(vehicle, profile.bodyModel, part.graphicPath, part.model, angle, out local);
        }

        internal static bool BodyContainsOverlay(Pawn vehicle, VehicleViewDef profile, string graphicPath)
        {
            if(coverage?.Invoke(vehicle,profile.bodyModel,graphicPath)==true)return true;
            var type=AccessTools.TypeByName("HSK.VehicleModels.VehicleOverlayCoverageDef");
            if(type==null)return false;
            var database=typeof(DefDatabase<>).MakeGenericType(type);
            var definitions=AccessTools.Property(database,"AllDefsListForReading")?.GetValue(null) as IEnumerable;
            if(definitions==null)return false;
            foreach(var entry in definitions)
                if((string)AccessTools.Field(type,"vehicleDef").GetValue(entry)==vehicle.def.defName
                    &&(string)AccessTools.Field(type,"graphicPath").GetValue(entry)==graphicPath
                    &&(string)AccessTools.Field(type,"requiredBodyModel").GetValue(entry)==profile.bodyModel)return true;
            return false;
        }
    }
}
