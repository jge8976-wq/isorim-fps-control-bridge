using System.Collections.Generic;
using UnityEngine;

namespace HSK.FpsBridge
{
    // One mesh per layer and frame. All particles share positions, indices and vertex colors in that mesh.
    internal sealed class BlastMesh
    {
        private const float ShortAxis = .5257311f;
        private const float LongAxis = .8506508f;
        private static readonly Vector3[] UnitBall =
        {
            new(-ShortAxis,LongAxis,0), new(ShortAxis,LongAxis,0), new(-ShortAxis,-LongAxis,0), new(ShortAxis,-LongAxis,0),
            new(0,-ShortAxis,LongAxis), new(0,ShortAxis,LongAxis), new(0,-ShortAxis,-LongAxis), new(0,ShortAxis,-LongAxis),
            new(LongAxis,0,-ShortAxis), new(LongAxis,0,ShortAxis), new(-LongAxis,0,-ShortAxis), new(-LongAxis,0,ShortAxis),
        };
        private static readonly int[] BallTriangles =
        {
            0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11,
            1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
            3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9,
            4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1,
        };
        private static readonly Vector3[] Ring12 = MakeRing(12), Ring18 = MakeRing(18), Ring24 = MakeRing(24);

        private static Vector3[] MakeRing(int segments)
        {
            var ring = new Vector3[segments];
            for (int i = 0; i < segments; i++)
            {
                float angle = Mathf.PI * 2f * i / segments;
                ring[i] = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
            }
            return ring;
        }

        private readonly string name;
        private readonly bool luminous;
        private readonly List<Vector3> positions = new List<Vector3>(3000);
        private readonly List<Color32> colors = new List<Color32>(3000);
        private readonly List<int> triangles = new List<int>(12000);
        private Mesh mesh;

        internal BlastMesh(string name, bool luminous = false) { this.name = name; this.luminous = luminous; }
        internal int VertexCount => positions.Count;
        internal static Vector3 Direction(int seed) => UnitBall[(int)((uint)seed % UnitBall.Length)];

        internal void Clear()
        {
            positions.Clear();
            colors.Clear();
            triangles.Clear();
        }

        internal void Ball(Vector3 center, Vector3 size, Quaternion rotation, Color tint)
        {
            int start = positions.Count;
            for (int i = 0; i < UnitBall.Length; i++)
            {
                var unit = UnitBall[i];
                positions.Add(center + rotation * Vector3.Scale(unit, size));
                float light = .65f + .29f * unit.y - .13f * unit.x;
                colors.Add(new Color(tint.r * light, tint.g * light, tint.b * light, tint.a));
            }
            foreach (int index in BallTriangles) triangles.Add(start + index);
        }

        internal void Ring(Vector3 center, float radius, Quaternion rotation, Color tint, int segments)
        {
            var ring = segments == 12 ? Ring12 : segments == 18 ? Ring18 : Ring24;
            int start = positions.Count;
            Color edge = tint;
            edge.a = 0;
            for (int i = 0; i < segments; i++)
            {
                Vector3 edgeVector = rotation * ring[i] * radius;
                positions.Add(center + edgeVector * .78f);
                positions.Add(center + edgeVector * .92f);
                positions.Add(center + edgeVector);
                colors.Add(edge);
                colors.Add(tint);
                colors.Add(edge);
                int here = start + i * 3;
                int next = start + ((i + 1) % segments) * 3;
                triangles.Add(here); triangles.Add(here + 1); triangles.Add(next);
                triangles.Add(here + 1); triangles.Add(next + 1); triangles.Add(next);
                triangles.Add(here + 1); triangles.Add(here + 2); triangles.Add(next + 1);
                triangles.Add(here + 2); triangles.Add(next + 2); triangles.Add(next + 1);
            }
        }

        internal bool Draw()
        {
            if (positions.Count == 0) return false;
            if (mesh == null)
            {
                mesh = new Mesh { name = name };
                mesh.MarkDynamic();
            }
            mesh.Clear();
            mesh.SetVertices(positions);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            return Visuals.DrawExplosionMesh(mesh, luminous);
        }
    }
}
