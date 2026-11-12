using System;
using IsoRim;
using UnityEngine;
using Verse;
using N = System.Numerics;

namespace HSK.FpsBridge
{
    internal static class AircraftFootprint
    {
        private static readonly MeshContactMath.Tree cube = Cube();

        internal static Bounds BoundsAt(Pawn vehicle, AerialMotor pose)
        {
            Rot4 rotation = Rot4.FromAngleFlat(pose.Yaw);
            bool sideways = rotation == Rot4.East || rotation == Rot4.West;
            float width = sideways ? vehicle.def.size.z : vehicle.def.size.x;
            float length = sideways ? vehicle.def.size.x : vehicle.def.size.z;
            // Keep the existing model origin at touchdown, with a one-cell-high
            // collision volume and the exact native construction footprint.
            float bottom = pose.Position.Y + AerialGeometry.LocalBounds(vehicle).min.y;
            return new Bounds(new Vector3(pose.Position.X, bottom + .5f, pose.Position.Z),
                new Vector3(width, 1f, length));
        }

        internal static MeshContactMath.Instance Instance(Pawn vehicle, AerialMotor pose)
            => Instance(BoundsAt(vehicle, pose));

        internal static MeshContactMath.Instance Instance(Bounds bounds)
        {
            Matrix4x4 world = Matrix4x4.TRS(bounds.center, Quaternion.identity, bounds.size);
            return new MeshContactMath.Instance(cube, VehicleMeshContact.Matrix(world));
        }

        private static MeshContactMath.Tree Cube()
        {
            var points = new[]
            {
                new N.Vector3(-.5f,-.5f,-.5f), new N.Vector3(.5f,-.5f,-.5f),
                new N.Vector3(.5f,.5f,-.5f), new N.Vector3(-.5f,.5f,-.5f),
                new N.Vector3(-.5f,-.5f,.5f), new N.Vector3(.5f,-.5f,.5f),
                new N.Vector3(.5f,.5f,.5f), new N.Vector3(-.5f,.5f,.5f),
            };
            int[] faces = { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4,
                3,7,6, 3,6,2, 0,4,7, 0,7,3, 1,2,6, 1,6,5 };
            var vertices = new N.Vector3[faces.Length];
            for (int i = 0; i < faces.Length; i++) vertices[i] = points[faces[i]];
            return new MeshContactMath.Tree(vertices);
        }
    }
}
