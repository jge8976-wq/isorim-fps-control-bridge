using System.Collections.Generic;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
using N = System.Numerics;

namespace HSK.FpsBridge
{
    internal static class AircraftModelContact
    {
        private static readonly Dictionary<Model, MeshContactMath.Tree> models = new();

        internal static bool Target(Thing thing, List<MeshContactMath.Instance> output)
        {
            output.Clear();
            if (thing is Pawn pawn)
                return VehicleApi.IsVehicle(pawn)
                    ? VehicleMeshContact.ModelInstances(pawn, VehicleGeometry.Pose(pawn), output)
                    : FigureMeshes.ContactMeshes(pawn, output);
            ThingForm form = Forms.For(thing.def);
            if (form.form == Form3D.Block && form.height > 0)
            {
                CellRect area = thing.OccupiedRect();
                FloorBand floor = FloorSpace.Band(thing.Map, thing.Position);
                float bottom = floor.Height + Forms.BaseUnder(thing, storageOnly: false);
                var block = new Bounds(new Vector3(area.minX + area.Width * .5f, bottom + form.height * .5f,
                    area.minZ - floor.Shift + area.Height * .5f), new Vector3(area.Width, form.height, area.Height));
                output.Add(AircraftFootprint.Instance(block));
                return true;
            }
            if (!Api.ModelsOn || !thing.Spawned || form.form != Form3D.Model
                || !ModelLibrary.TryGet(thing.def, out ModelEntry entry) || !entry.HasModel || entry.def.flat) return false;
            Model model = StateModels.Choose(thing, entry, entry.Pick(thing));
            if (model?.positions == null || model.positions.Length < 3) return false;
            if (!models.TryGetValue(model, out MeshContactMath.Tree mesh))
            {
                var vertices = new N.Vector3[model.positions.Length];
                for (int i = 0; i < vertices.Length; i++) vertices[i] = VehicleMeshContact.N3(model.positions[i]);
                models[model] = mesh = new MeshContactMath.Tree(vertices);
            }
            Vector3 origin = ModelBaker.PointOnMap(thing, entry, model, Vector3.zero, out Vector3 fit, out float yaw);
            // The baker returns native map rows. Section rendering applies the
            // floor transform separately; contacts use unsquashed world space.
            FloorBand modelFloor = FloorSpace.Band(thing.Map, thing.Position);
            origin.y += modelFloor.Height + (thing is Plant ? Forms.BaseUnder(thing, storageOnly: false) : 0);
            origin.z -= modelFloor.Shift;
            Matrix4x4 place = Matrix4x4.TRS(origin, Quaternion.AngleAxis(yaw, Vector3.up), fit);
            if (entry.def.layFlat)
                place *= Matrix4x4.Translate(new Vector3(0, model.halfDepth, 0)) * Matrix4x4.Rotate(Quaternion.Euler(90, 0, 0));
            output.Add(new MeshContactMath.Instance(mesh, VehicleMeshContact.Matrix(place)));
            return true;
        }

        internal static bool Touches(Pawn aircraft, AerialMotor before, AerialMotor next, Thing target)
        {
            var targetParts = new List<MeshContactMath.Instance>();
            return Target(target, targetParts)
                && VehicleMeshContact.SweepAgainst(aircraft, before, next, targetParts, true, out _);
        }
    }
}
