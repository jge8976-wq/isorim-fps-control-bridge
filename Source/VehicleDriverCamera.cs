using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using HarmonyLib;
using IsoRim;
using UnityEngine;
using Verse;
using N = System.Numerics;

namespace HSK.FpsBridge
{
    public sealed class VehicleDriverCameraDef : Def
    {
        public string vehicleDef, bodyModel;
        public Vector3 eye;
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (string.IsNullOrEmpty(vehicleDef) || string.IsNullOrEmpty(bodyModel) || !VehicleViewDef.Finite(eye))
                yield return "Driver camera requires an explicit vehicle/body model and a finite local eye";
        }
    }

    internal static class VehicleDriverCamera
    {
        private const float Clearance = .1f;
        private static readonly FieldInfo Model = AccessTools.Field(typeof(ModelHandle), "model");
        private static readonly Dictionary<(ModelHandle, bool), MeshContactMath.Tree> opaqueBodies = new();

        internal static Vector3 Resolve(Pawn vehicle, Vector3 authoredEye)
        {
            if (!Api.ViewReady || !Api.ModelsOn) return authoredEye;
            VehicleViewDef profile = VehicleModels.Profile(vehicle);
            var calibration = DefDatabase<VehicleDriverCameraDef>.AllDefsListForReading.FirstOrDefault(def => def.vehicleDef == vehicle.def.defName
                && string.Equals(def.bodyModel.Replace('\\', '/'), profile.bodyModel?.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (calibration != null) authoredEye = VehicleModels.World(vehicle, calibration.eye);
            AerialMotor pose = VehicleGeometry.Pose(vehicle);
            var parts = new List<VehicleGeometry.Part>();
            if (!VehicleGeometry.Parts(vehicle, pose, parts)) return authoredEye;
            VehicleGeometry.Part body = parts[0];
            MeshContactMath.Tree mesh = OpaqueBody(body.Mesh);
            if (mesh == null) return authoredEye;
            var shape = new MeshContactMath.Instance(mesh, VehicleMeshContact.Matrix(body.World));
            Quaternion heading = AerialFlight.Supports(vehicle) ? AerialGeometry.Rotation(pose) : Quaternion.Euler(0, pose.Yaw, 0);
            N.Vector3 forward = VehicleMeshContact.N3(heading * Vector3.forward);
            N.Vector3 start = VehicleMeshContact.N3(authoredEye);
            MeshContactMath.Box bounds = shape.Bounds;
            // Trace back from beyond the opaque driving shell to its frontmost surface.
            // The fixed hull heading keeps free look and moving weapon parts out of this anchor.
            float Advance(N.Vector3 offset)
            {
                N.Vector3 origin = start + offset;
                float reach = N.Vector3.Dot(bounds.Center - origin, forward) + (bounds.Max - bounds.Min).Length() * .5f + Clearance;
                if (reach <= Clearance) return 0;
                float back = MeshContactMath.Raycast(shape, origin + forward * reach, -forward, 0, reach);
                return back < reach - .0001f ? reach - back + Clearance : 0;
            }
            float advance = Advance(N.Vector3.Zero);
            if (advance <= 0) return authoredEye;
            N.Vector3 right = VehicleMeshContact.N3(heading * Vector3.right) * Clearance;
            N.Vector3 up = VehicleMeshContact.N3(heading * Vector3.up) * Clearance;
            foreach (N.Vector3 corner in new[] { right + up, right - up, -right + up, -right - up })
                advance = Math.Max(advance, Advance(corner));
            return authoredEye + VehicleMeshContact.U3(forward) * advance;
        }

        private static MeshContactMath.Tree OpaqueBody(ModelHandle handle)
        {
            bool outline = IsoRimMod.Settings.outlines;
            if (opaqueBodies.TryGetValue((handle, outline), out var cached)) return cached;
            var model = (IsoRim.Model)Model.GetValue(handle);
            var vertices = new List<N.Vector3>();
            for (int triangle = 0; triangle < model.positions.Length; triangle += 3)
            {
                bool opaque = true;
                for (int corner = triangle; corner < triangle + 3; corner++)
                {
                    if (model.colors != null && model.colors[corner].a < .999f) opaque = false;
                    int material = model.material == null ? -1 : model.material[corner];
                    if (material >= 0 && material < model.materials.Length && model.materials[material].color.a < .999f) opaque = false;
                }
                if (!opaque) continue;
                for (int corner = triangle; corner < triangle + 3; corner++) vertices.Add(VehicleMeshContact.N3(model.positions[corner]));
                if (outline && model.hull != null && (model.noOutline == null || !model.noOutline[triangle]))
                    for (int corner = triangle; corner < triangle + 3; corner++) vertices.Add(VehicleMeshContact.N3(model.positions[corner] + model.hull[corner]));
            }
            return opaqueBodies[(handle, outline)] = vertices.Count == 0 ? null : new MeshContactMath.Tree(vertices.ToArray());
        }
    }
}
