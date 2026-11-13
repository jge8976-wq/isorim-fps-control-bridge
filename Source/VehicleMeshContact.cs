using System;
using System.Collections.Generic;
using HarmonyLib;
using IsoRim;
using UnityEngine;
using Verse;
using N=System.Numerics;
namespace HSK.FpsBridge
{
    internal static class VehicleMeshContact
    {
        static readonly Dictionary<ModelHandle,MeshContactMath.Tree> meshes=new();
        static readonly System.Reflection.FieldInfo model=AccessTools.Field(typeof(ModelHandle),"model");
        internal static int MeshBuilds,SweepIterations;internal static readonly Dictionary<string,int> OpenMeshes=new();
        static readonly Dictionary<MeshContactMath.Tree,N.Vector3[]> edges=new();
        internal static N.Vector3[] Edges(MeshContactMath.Tree mesh)
        {
            if(edges.TryGetValue(mesh,out var cached))return cached;
            var unique=new Dictionary<(N.Vector3,N.Vector3),(N.Vector3 normal,int count,bool crease)>();
            static bool Before(N.Vector3 a,N.Vector3 b)=>a.X<b.X||a.X==b.X&&(a.Y<b.Y||a.Y==b.Y&&a.Z<b.Z);
            for(int i=0;i<mesh.Vertices.Length;i+=3)
            {
                var a=mesh.Vertices[i];var b=mesh.Vertices[i+1];var c=mesh.Vertices[i+2];var n=N.Vector3.Cross(b-a,c-a);if(n.LengthSquared()<1e-14f)continue;n=N.Vector3.Normalize(n);
                void Add(N.Vector3 x,N.Vector3 y){var key=Before(x,y)?(x,y):(y,x);if(unique.TryGetValue(key,out var old))unique[key]=(old.normal,old.count+1,old.crease||Math.Abs(N.Vector3.Dot(n,old.normal))<.95f);else unique[key]=(n,1,false);}
                Add(a,b);Add(b,c);Add(c,a);
            }
            var list=new List<N.Vector3>();foreach(var pair in unique)if(pair.Value.count==1||pair.Value.crease){list.Add(pair.Key.Item1);list.Add(pair.Key.Item2);}return edges[mesh]=list.ToArray();
        }
        internal static N.Vector3 N3(Vector3 p)=>new(p.x,p.y,p.z);
        internal static Vector3 U3(N.Vector3 p)=>new(p.X,p.Y,p.Z);
        internal static N.Matrix4x4 Matrix(Matrix4x4 m)=>new(m.m00,m.m10,m.m20,m.m30,m.m01,m.m11,m.m21,m.m31,m.m02,m.m12,m.m22,m.m32,m.m03,m.m13,m.m23,m.m33);
        internal static MeshContactMath.Tree Mesh(ModelHandle handle)
        {
            if(meshes.TryGetValue(handle,out var tree))return tree;
            var source=(Model)model.GetValue(handle);var v=new N.Vector3[source.positions.Length];
            for(int i=0;i<v.Length;i++)v[i]=N3(source.positions[i]);tree=new MeshContactMath.Tree(v);meshes[handle]=tree;MeshBuilds++;return tree;
        }
        internal static bool Instances(Pawn v,AerialMotor pose,List<MeshContactMath.Instance> output)
        {
            if (!AerialFlight.Supports(v)) return ModelInstances(v, pose, output);
            output.Clear();
            output.Add(AircraftFootprint.Instance(v, pose));
            return true;
        }
        internal static bool ModelInstances(Pawn v,AerialMotor pose,List<MeshContactMath.Instance> output)
        {
            var plan = new List<VehicleGeometry.Part>();
            if (!VehicleGeometry.Parts(v, pose, plan)) { output.Clear(); return false; }
            MakeModelInstances(v, plan, output);
            return output.Count > 0;
        }
        private static void MakeModelInstances(Pawn vehicle, List<VehicleGeometry.Part> plan, List<MeshContactMath.Instance> output)
        {
            output.Clear();
            foreach (var part in plan)
            {
                var mesh = Mesh(part.Mesh);
                if (!mesh.Closed) OpenMeshes[vehicle.def.defName + ":" + part.Role] = mesh.Vertices.Length / 3;
                output.Add(new MeshContactMath.Instance(mesh, Matrix(part.World)));
            }
        }
        internal static bool Contact(Pawn a,AerialMotor ap,Pawn b,AerialMotor bp,out MeshContactMath.Contact contact)
        {
            contact=default;
            if(AerialFlight.Supports(a)&&AerialFlight.Supports(b))
            {
                var bounds=AircraftFootprint.BoundsAt(a,ap);
                bounds.Expand(.04f);
                if(!bounds.Intersects(AircraftFootprint.BoundsAt(b,bp)))return false;
            }
            var aa=new List<MeshContactMath.Instance>();var bb=new List<MeshContactMath.Instance>();
            if(!Instances(a,ap,aa)||!Instances(b,bp,bb))return false;
            foreach(var x in aa)foreach(var y in bb)if(MeshContactMath.Query(x,y,N.Vector3.Zero,out contact))return true;return false;
        }
        static AerialMotor Between(AerialMotor a,AerialMotor b,float t)
        {var p=a;p.Position=a.Position+(b.Position-a.Position)*t;p.Yaw=a.Yaw+Mathf.DeltaAngle(a.Yaw,b.Yaw)*t;p.Pitch=a.Pitch+Mathf.DeltaAngle(a.Pitch,b.Pitch)*t;p.Roll=a.Roll+Mathf.DeltaAngle(a.Roll,b.Roll)*t;return p;}
        internal static bool Sweep(Pawn a,AerialMotor before,AerialMotor next,Pawn b,AerialMotor other,out MeshContactMath.Contact contact)
        {
            contact = default;
            if (AerialFlight.Supports(a) && AerialFlight.Supports(b))
            {
                var swept = AircraftFootprint.BoundsAt(a, before);
                swept.Encapsulate(AircraftFootprint.BoundsAt(a, next));
                // The footprint snaps at cardinal headings. Its largest XY projection fits within this horizontal radius.
                float width = Math.Max(a.def.size.x, a.def.size.z);
                var start = VehicleModels.U(before.Position);
                var end = VehicleModels.U(next.Position);
                var min = swept.min; var max = swept.max;
                min.x = Math.Min(start.x, end.x) - width * .5f - .02f;
                min.z = Math.Min(start.z, end.z) - width * .5f - .02f;
                max.x = Math.Max(start.x, end.x) + width * .5f + .02f;
                max.z = Math.Max(start.z, end.z) + width * .5f + .02f;
                min.y -= .02f; max.y += .02f;
                swept.SetMinMax(min, max);
                if (!swept.Intersects(AircraftFootprint.BoundsAt(b, other))) return false;
            }
            var target = new List<MeshContactMath.Instance>();
            if (!Instances(b, other, target)) { contact = default; return false; }
            return SweepAgainst(a, before, next, target, false, out contact);
        }
        internal static bool SweepAgainst(Pawn a, AerialMotor before, AerialMotor next,
            List<MeshContactMath.Instance> bb, bool exactModel, out MeshContactMath.Contact contact)
        {
            contact = default;
            if (bb.Count == 0) return false;
            var aa = new List<MeshContactMath.Instance>();
            bool Build(AerialMotor pose) => exactModel ? ModelInstances(a, pose, aa) : Instances(a, pose, aa);
            float angle = (Math.Abs(Mathf.DeltaAngle(before.Yaw,next.Yaw)) + Math.Abs(Mathf.DeltaAngle(before.Pitch,next.Pitch))
                + Math.Abs(Mathf.DeltaAngle(before.Roll,next.Roll))) * Mathf.Deg2Rad;
            var delta = N3(VehicleModels.U(next.Position - before.Position));
            if (exactModel)
            {
                var plan = new List<VehicleGeometry.Part>();
                if (!VehicleGeometry.Parts(a, before, plan)) return false;
                // Model handles already provide local bounds. Reject distant contacts before building the triangle hierarchy.
                var pivot = N3(VehicleModels.U(before.Position));
                bool mayContact = false;
                foreach (var part in plan)
                {
                    var box = new MeshContactMath.Box(N3(part.Mesh.Min), N3(part.Mesh.Max)).Transform(Matrix(part.World));
                    float angularReach = (N.Vector3.Distance(pivot, box.Center) + (box.Max - box.Min).Length() * .5f) * angle;
                    box = box.Swept(delta);
                    box.Min -= new N.Vector3(angularReach);
                    box.Max += new N.Vector3(angularReach);
                    foreach (var target in bb)
                    {
                        if (!box.Intersects(target.Bounds)) continue;
                        mayContact = true;
                        break;
                    }
                    if (mayContact) break;
                }
                if (!mayContact) return false;
                MakeModelInstances(a, plan, aa);
                if (aa.Count == 0) return false;
            }
            else if (!Build(before)) return false;
            Vector3 root=VehicleModels.U(before.Position);float radius=0;
            foreach(var p in aa){var box=p.Bounds;radius=Math.Max(radius,N.Vector3.Distance(VehicleMeshContact.N3(root),box.Center)+(box.Max-box.Min).Length()*.5f);}
            float bound=(next.Position-before.Position).Length+radius*angle;
            var union=aa[0].Bounds;foreach(var p in aa){union.Add(p.Bounds.Min);union.Add(p.Bounds.Max);}
            union=union.Swept(N3(VehicleModels.U(next.Position-before.Position)));union.Min-=new N.Vector3(radius*angle);union.Max+=new N.Vector3(radius*angle);
            bool candidate=false;foreach(var p in bb)if(union.Intersects(p.Bounds)){candidate=true;break;}if(!candidate)return false;
            if(angle<.000001f)
            {
                float first=float.PositiveInfinity;
                foreach(var x in aa)foreach(var y in bb)if(MeshContactMath.Query(x,y,delta,out var hit)&&hit.Time<first){first=hit.Time;contact=hit;}
                return float.IsFinite(first);
            }
            // Conservative advancement uses actual mesh distance and a rigid
            // point-speed upper bound. Rotation cannot tunnel a thin wing even
            // with zero translation; no arbitrary angular sample interval.
            float t=0;
            for(int i=0;i<96;i++)
            {
                SweepIterations++;var pose=Between(before,next,t);if(!Build(pose))return false;
                float nearest=float.PositiveInfinity;MeshContactMath.Contact closest=default;
                float reachable=bound*(1-t)+MeshContactMath.Skin+.00002f;
                foreach(var x in aa)foreach(var y in bb)
                {
                    if(x.Bounds.DistanceSquared(y.Bounds)>reachable*reachable)continue;
                    if(MeshContactMath.Query(x,y,N.Vector3.Zero,out var hit)){contact=hit;contact.Time=t;return true;}
                    float distance=MeshContactMath.DistanceWithin(x,y,Math.Min(reachable,nearest),out var point);if(distance<nearest){nearest=distance;closest=point;}
                }
                if(nearest<=MeshContactMath.Skin+.00001f){contact=closest;contact.Time=t;return true;}
                if(bound<1e-7f||!float.IsFinite(nearest))return false;
                float step=(nearest-MeshContactMath.Skin)*.8f/bound;
                if(t+step>1)return false;
                t+=Math.Max(.000001f,step);
            }
            // A convergence failure is explicit; never claim a box is a hit.
            Log.WarningOnce("[FpsBridge] VEHICLE_SWEEP_BUDGET " + a.ThingID, 19104377);
            if (!Build(next)) return false;
            foreach (var x in aa) foreach (var y in bb)
                if (MeshContactMath.Query(x, y, N.Vector3.Zero, out contact)) return true;
            return false;
        }
    }
}
