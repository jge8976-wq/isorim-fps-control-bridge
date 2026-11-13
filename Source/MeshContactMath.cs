using System;
using System.Collections.Generic;
using System.Numerics;
namespace HSK.FpsBridge
{
    // Engine-free triangle hierarchy. AABB is only broad phase; leaves retain
    // the original triangle soup, including holes between hulls and wings.
    internal static class MeshContactMath
    {
        internal const float Skin=.003f;
        internal struct Box
        {
            internal Vector3 Min,Max;internal Vector3 Center=>(Min+Max)*.5f;
            internal Box(Vector3 a,Vector3 b){Min=Vector3.Min(a,b);Max=Vector3.Max(a,b);}
            internal void Add(Vector3 p){Min=Vector3.Min(Min,p);Max=Vector3.Max(Max,p);}
            internal bool Intersects(Box b)=>Min.X<=b.Max.X+Skin&&Max.X+Skin>=b.Min.X&&Min.Y<=b.Max.Y+Skin&&Max.Y+Skin>=b.Min.Y&&Min.Z<=b.Max.Z+Skin&&Max.Z+Skin>=b.Min.Z;
            internal float DistanceSquared(Box b)
            {Vector3 d=Vector3.Max(Vector3.Zero,Vector3.Max(Min-b.Max,b.Min-Max));return d.LengthSquared();}
            internal Box Swept(Vector3 d)=>new(Vector3.Min(Min,Min+d),Vector3.Max(Max,Max+d));
            internal Box Transform(Matrix4x4 m)
            {
                Vector3 center = Vector3.Transform(Center, m);
                Vector3 extent = (Max - Min) * .5f;
                var half = new Vector3(
                    MathF.Abs(m.M11) * extent.X + MathF.Abs(m.M21) * extent.Y + MathF.Abs(m.M31) * extent.Z,
                    MathF.Abs(m.M12) * extent.X + MathF.Abs(m.M22) * extent.Y + MathF.Abs(m.M32) * extent.Z,
                    MathF.Abs(m.M13) * extent.X + MathF.Abs(m.M23) * extent.Y + MathF.Abs(m.M33) * extent.Z);
                return new Box(center - half, center + half);
            }
        }
        internal sealed class Tree
        {
            internal readonly Vector3[] Vertices;internal readonly int[] Triangles;internal readonly List<Node> Nodes=new();
            internal readonly bool Closed;internal Vector3[] Seeds;
            internal Tree(Vector3[] vertices)
            {
                if(vertices==null||vertices.Length<3||vertices.Length%3!=0)throw new ArgumentException("Triangle soup required");
                Vertices=vertices;
                Triangles=new int[vertices.Length/3];
                var centers = new Vector3[Triangles.Length];
                for(int i=0;i<Triangles.Length;i++)
                {
                    Triangles[i]=i;
                    centers[i]=(vertices[i*3]+vertices[i*3+1]+vertices[i*3+2])/3f;
                }
                Build(0,Triangles.Length,centers);Closed=IsClosed(vertices);
            }
            int Build(int start,int count, Vector3[] centers)
            {
                var b=new Box(Vertices[Triangles[start]*3],Vertices[Triangles[start]*3]);
                for(int i=start;i<start+count;i++)for(int j=0;j<3;j++)b.Add(Vertices[Triangles[i]*3+j]);
                int n=Nodes.Count;Nodes.Add(new Node{Bounds=b,Start=start,Count=count,Left=-1,Right=-1});
                if(count<=6)return n;
                Vector3 size=b.Max-b.Min;int axis=size.X>size.Y&&size.X>size.Z?0:size.Y>size.Z?1:2;
                int half = count / 2;
                Partition(start, start + count - 1, start + half, axis, centers);
                int l=Build(start,half,centers),r=Build(start+half,count-half,centers);
                Nodes[n]=new Node{Bounds=b,Left=l,Right=r};return n;
            }
            private void Partition(int low, int high, int middle, int axis, Vector3[] centers)
            {
                float Key(int triangle)
                {
                    Vector3 value = centers[triangle];
                    return axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;
                }
                while (low < high)
                {
                    float pivot = Key(Triangles[low + (high - low) / 2]);
                    int left = low, right = high;
                    while (left <= right)
                    {
                        while (Key(Triangles[left]) < pivot) left++;
                        while (Key(Triangles[right]) > pivot) right--;
                        if (left > right) break;
                        (Triangles[left], Triangles[right]) = (Triangles[right], Triangles[left]);
                        left++;
                        right--;
                    }
                    if (middle <= right) high = right;
                    else if (middle >= left) low = left;
                    else return;
                }
            }
            static bool IsClosed(Vector3[] v)
            {
                var ids=new Dictionary<(int,int,int),int>();var edges=new Dictionary<(int,int),int>();
                int Id(Vector3 p){var k=((int)MathF.Round(p.X*100000),(int)MathF.Round(p.Y*100000),(int)MathF.Round(p.Z*100000));if(!ids.TryGetValue(k,out int n))ids[k]=n=ids.Count;return n;}
                for(int i=0;i<v.Length;i+=3)for(int j=0;j<3;j++){int a=Id(v[i+j]),b=Id(v[i+(j+1)%3]);if(a==b)continue;var k=a<b?(a,b):(b,a);edges[k]=edges.GetValueOrDefault(k)+1;}
                foreach(int n in edges.Values)if(n!=2)return false;return true;
            }
        }
        internal struct Node {internal Box Bounds;internal int Start,Count,Left,Right;}
        internal readonly struct Instance
        {
            internal readonly Tree Mesh;internal readonly Matrix4x4 World;
            internal Instance(Tree mesh,Matrix4x4 world){Mesh=mesh;World=world;}
            internal Box Bounds=>Mesh.Nodes[0].Bounds.Transform(World);
            internal Vector3 Vertex(int triangle,int corner)=>Vector3.Transform(Mesh.Vertices[triangle*3+corner],World);
        }
        internal struct Contact {internal float Time;internal Vector3 Point,Normal;internal bool Contained;}
        internal static int TriangleTests;
        internal static bool Query(Instance a,Instance b,Vector3 delta,out Contact hit)
        {
            hit=default;if(!a.Bounds.Swept(delta).Intersects(b.Bounds))return false;
            float best=float.PositiveInfinity;Contact found=default;
            void Visit(int ai,int bi)
            {
                if(best==0)return;
                Node an=a.Mesh.Nodes[ai],bn=b.Mesh.Nodes[bi];
                if(!an.Bounds.Transform(a.World).Swept(delta).Intersects(bn.Bounds.Transform(b.World)))return;
                if(an.Left<0&&bn.Left<0)
                {
                    for(int i=an.Start;i<an.Start+an.Count;i++)for(int j=bn.Start;j<bn.Start+bn.Count;j++)
                    {
                        if(best==0)return;
                        int ia=a.Mesh.Triangles[i],ib=b.Mesh.Triangles[j];TriangleTests++;
                        Vector3 a0=a.Vertex(ia,0),a1=a.Vertex(ia,1),a2=a.Vertex(ia,2),b0=b.Vertex(ib,0),b1=b.Vertex(ib,1),b2=b.Vertex(ib,2);
                        if(!SweepTriangle(a0,a1,a2,b0,b1,b2,delta,out float t,out Vector3 normal)||t>=best)continue;
                        best=t;Vector3 d=delta*t;
                        found=new Contact{Time=t,Normal=normal,Point=ClosestTriangles(a0+d,a1+d,a2+d,b0,b1,b2)};
                    }
                    return;
                }
                if(an.Left>=0&&(bn.Left<0||(an.Bounds.Max-an.Bounds.Min).LengthSquared()>(bn.Bounds.Max-bn.Bounds.Min).LengthSquared())){Visit(an.Left,bi);Visit(an.Right,bi);}
                else{Visit(ai,bn.Left);Visit(ai,bn.Right);}
            }
            Visit(0,0);
            if(float.IsFinite(best)){hit=found;return true;}
            // Surface tests alone miss a small solid completely enclosed by a
            // larger solid. Only watertight geometry gets parity containment.
            bool enclosed=false;
            if(b.Mesh.Closed)foreach(var seed in ComponentSeeds(a.Mesh))if(Inside(Vector3.Transform(seed,a.World),b)){enclosed=true;break;}
            if(!enclosed&&a.Mesh.Closed)foreach(var seed in ComponentSeeds(b.Mesh))if(Inside(Vector3.Transform(seed,b.World),a)){enclosed=true;break;}
            if(enclosed)
            {
                Vector3 n=a.Bounds.Center-b.Bounds.Center;if(n.LengthSquared()<1e-8f)n=Vector3.UnitX;else n=Vector3.Normalize(n);
                hit=new Contact{Time=0,Point=(a.Bounds.Center+b.Bounds.Center)*.5f,Normal=n,Contained=true};return true;
            }
            return false;
        }
        // Connected islands share welded vertex positions, not OBJ material or
        // index identity. Containment must examine every disconnected solid.
        static Vector3[] ComponentSeeds(Tree mesh)
        {
            if(mesh.Seeds!=null)return mesh.Seeds;
            int count=mesh.Vertices.Length/3;int[] parent=new int[count];for(int i=0;i<count;i++)parent[i]=i;
            int Root(int i){while(parent[i]!=i){parent[i]=parent[parent[i]];i=parent[i];}return i;}
            var seen=new Dictionary<(int,int,int),int>();
            for(int t=0;t<count;t++)for(int k=0;k<3;k++)
            {var p=mesh.Vertices[t*3+k];var key=((int)MathF.Round(p.X*100000),(int)MathF.Round(p.Y*100000),(int)MathF.Round(p.Z*100000));if(seen.TryGetValue(key,out int other))parent[Root(t)]=Root(other);else seen[key]=t;}
            var roots=new HashSet<int>();var seeds=new List<Vector3>();for(int i=0;i<count;i++)if(roots.Add(Root(i)))seeds.Add(mesh.Vertices[i*3]);return mesh.Seeds=seeds.ToArray();
        }
        internal static float Distance(Instance a,Instance b,out Contact contact)
            => DistanceWithin(a,b,float.PositiveInfinity,out contact);
        // The sweep only needs distances reachable during its remaining motion.
        // A rejected bound is not a collision: leaves still measure real triangles.
        internal static float DistanceWithin(Instance a,Instance b,float limit,out Contact contact)
        {
            float best=limit*limit;Vector3 pa=default,pb=default;bool found=false;
            float Lower(int ai,int bi)=>a.Mesh.Nodes[ai].Bounds.Transform(a.World).DistanceSquared(b.Mesh.Nodes[bi].Bounds.Transform(b.World));
            void Visit(int ai,int bi,float lower)
            {
                if(lower>=best)return;Node an=a.Mesh.Nodes[ai],bn=b.Mesh.Nodes[bi];
                if(an.Left<0&&bn.Left<0)
                {
                    for(int i=an.Start;i<an.Start+an.Count;i++)
                    {
                        int at=a.Mesh.Triangles[i];var a0=a.Vertex(at,0);var a1=a.Vertex(at,1);var a2=a.Vertex(at,2);
                        var ab=new Box(a0,a1);ab.Add(a2);
                        for(int j=bn.Start;j<bn.Start+bn.Count;j++)
                        {
                            int bt=b.Mesh.Triangles[j];var b0=b.Vertex(bt,0);var b1=b.Vertex(bt,1);var b2=b.Vertex(bt,2);
                            var bb=new Box(b0,b1);bb.Add(b2);if(ab.DistanceSquared(bb)>=best)continue;
                            ClosestTriangles(a0,a1,a2,b0,b1,b2,out float d,out var p,out var q);
                            if(d<best){best=d;pa=p;pb=q;found=true;}
                        }
                    }
                    return;
                }
                int al=ai,ar=ai,bl=bi,br=bi;
                if(an.Left>=0&&(bn.Left<0||(an.Bounds.Max-an.Bounds.Min).LengthSquared()>(bn.Bounds.Max-bn.Bounds.Min).LengthSquared())){al=an.Left;ar=an.Right;}else{bl=bn.Left;br=bn.Right;}
                float left=Lower(al,bl),right=Lower(ar,br);
                // A near leaf supplies a tight upper bound before the far subtree
                // is visited. Fixed left-first order can scan most of both meshes.
                if(left<=right){Visit(al,bl,left);Visit(ar,br,right);}else{Visit(ar,br,right);Visit(al,bl,left);}
            }
            Visit(0,0,Lower(0,0));Vector3 normal=pa-pb;contact=new Contact{Point=(pa+pb)*.5f,Normal=normal.LengthSquared()>1e-12f?Vector3.Normalize(normal):Vector3.Zero};return found?MathF.Sqrt(best):float.PositiveInfinity;
        }
        internal static bool SweepTriangle(Vector3 a0,Vector3 a1,Vector3 a2,Vector3 b0,Vector3 b1,Vector3 b2,Vector3 delta,out float time,out Vector3 normal)
        {
            Span<Vector3> ae=stackalloc Vector3[]{a1-a0,a2-a1,a0-a2};Span<Vector3> be=stackalloc Vector3[]{b1-b0,b2-b1,b0-b2};
            Vector3 na=Vector3.Cross(ae[0],ae[1]),nb=Vector3.Cross(be[0],be[1]);Span<Vector3> axes=stackalloc Vector3[17];int count=0;
            axes[count++]=na;axes[count++]=nb;for(int i=0;i<3;i++)for(int j=0;j<3;j++)axes[count++]=Vector3.Cross(ae[i],be[j]);
            for(int i=0;i<3;i++){axes[count++]=Vector3.Cross(na,ae[i]);axes[count++]=Vector3.Cross(nb,be[i]);}
            float enter=0,exit=1;Vector3 n=Vector3.Zero;
            foreach(Vector3 axis in axes)
            {
                float length=axis.Length();if(length<1e-8f)continue;Vector3 u=axis/length;
                float ap0=Vector3.Dot(a0,u),ap1=Vector3.Dot(a1,u),ap2=Vector3.Dot(a2,u),bp0=Vector3.Dot(b0,u),bp1=Vector3.Dot(b1,u),bp2=Vector3.Dot(b2,u);
                float lo=Math.Min(ap0,Math.Min(ap1,ap2)),hi=Math.Max(ap0,Math.Max(ap1,ap2)),blo=Math.Min(bp0,Math.Min(bp1,bp2))-Skin,bhi=Math.Max(bp0,Math.Max(bp1,bp2))+Skin,speed=Vector3.Dot(delta,u);
                if(Math.Abs(speed)<1e-10f){if(hi<blo||lo>bhi){time=0;normal=default;return false;}continue;}
                float t0=(blo-hi)/speed,t1=(bhi-lo)/speed;if(t0>t1)(t0,t1)=(t1,t0);
                if(t0>enter){enter=t0;n=speed>0?-u:u;}exit=Math.Min(exit,t1);
                if(enter>exit||exit<0||enter>1){time=0;normal=default;return false;}
            }
            if(n.LengthSquared()<1e-8f){n=(a0+a1+a2-b0-b1-b2)/3;if(n.LengthSquared()<1e-8f)n=delta.LengthSquared()>1e-8f?-delta:na;if(n.LengthSquared()<1e-8f)n=Vector3.UnitX;n=Vector3.Normalize(n);}
            time=Math.Clamp(enter,0,1);normal=n;return true;
        }
        static bool Inside(Vector3 point,Instance shape)
        {
            Matrix4x4.Invert(shape.World,out var inv);Vector3 p=Vector3.Transform(point,inv);var root=shape.Mesh.Nodes[0].Bounds;
            if(p.X<root.Min.X||p.Y<root.Min.Y||p.Z<root.Min.Z||p.X>root.Max.X||p.Y>root.Max.Y||p.Z>root.Max.Z)return false;
            Vector3 dir=Vector3.Normalize(new Vector3(1,.37139f,.52913f));var distances=new List<float>();
            void Walk(int index)
            {
                Node node=shape.Mesh.Nodes[index];if(!RayBox(p,dir,node.Bounds))return;
                if(node.Left>=0){Walk(node.Left);Walk(node.Right);return;}
                for(int i=node.Start;i<node.Start+node.Count;i++){int t=shape.Mesh.Triangles[i]*3;if(RayTriangle(p,dir,shape.Mesh.Vertices[t],shape.Mesh.Vertices[t+1],shape.Mesh.Vertices[t+2],out float distance)&&distance>1e-5f)distances.Add(distance);}
            }
            Walk(0);distances.Sort();int count=0;float last=-1;foreach(float d in distances)if(d-last>1e-4f){count++;last=d;}return count%2==1;
        }
        internal static float Raycast(Instance shape, Vector3 origin, Vector3 direction, float near, float limit)
        {
            if (!Matrix4x4.Invert(shape.World, out var inverse)) return limit;
            Vector3 p = Vector3.Transform(origin, inverse);
            Vector3 d = Vector3.TransformNormal(direction, inverse);
            float best = limit;
            Visit(0);
            return best;

            void Visit(int index)
            {
                Node node = shape.Mesh.Nodes[index];
                if (!RayBox(p, d, node.Bounds, near, best)) return;
                if (node.Left >= 0) { Visit(node.Left); Visit(node.Right); return; }
                for (int i = node.Start; i < node.Start + node.Count; i++)
                {
                    int t = shape.Mesh.Triangles[i] * 3;
                    var v = shape.Mesh.Vertices;
                    if (RayTriangle(p, d, v[t], v[t + 1], v[t + 2], out float distance) && distance >= near && distance < best)
                        best = distance;
                }
            }
        }
        internal static bool RayBox(Vector3 p, Vector3 d, Box box, float lo = 0f, float hi = float.PositiveInfinity)
        {
            for (int i = 0; i < 3; i++)
            {
                float start = i == 0 ? p.X : i == 1 ? p.Y : p.Z;
                float step = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
                float min = i == 0 ? box.Min.X : i == 1 ? box.Min.Y : box.Min.Z;
                float max = i == 0 ? box.Max.X : i == 1 ? box.Max.Y : box.Max.Z;
                if (step == 0f)
                {
                    if (start < min || start > max) return false;
                    continue;
                }
                float a = (min - start) / step, b = (max - start) / step;
                if (a > b) (a, b) = (b, a);
                lo = Math.Max(lo, a); hi = Math.Min(hi, b);
                if (lo > hi) return false;
            }
            return true;
        }
        static bool RayTriangle(Vector3 p,Vector3 d,Vector3 a,Vector3 b,Vector3 c,out float distance)
        {var e=b-a;var f=c-a;var h=Vector3.Cross(d,f);float det=Vector3.Dot(e,h);distance=0;if(Math.Abs(det)<1e-8f)return false;float inv=1/det;var s=p-a;float u=inv*Vector3.Dot(s,h);if(u<0||u>1)return false;var q=Vector3.Cross(s,e);float v=inv*Vector3.Dot(d,q);if(v<0||u+v>1)return false;distance=inv*Vector3.Dot(f,q);return distance>=0;}
        static Vector3 ClosestTriangles(Vector3 a,Vector3 b,Vector3 c,Vector3 d,Vector3 e,Vector3 f)
        {ClosestTriangles(a,b,c,d,e,f,out _,out var pa,out var pb);return (pa+pb)*.5f;}
        static void ClosestTriangles(Vector3 a,Vector3 b,Vector3 c,Vector3 d,Vector3 e,Vector3 f,out float squared,out Vector3 pointA,out Vector3 pointB)
        {
            float best=float.PositiveInfinity;Vector3 pa=default,pb=default;
            void Add(Vector3 p,Vector3 q){float s=Vector3.DistanceSquared(p,q);if(s<best){best=s;pa=p;pb=q;}}
            Add(a,Closest(a,d,e,f));Add(b,Closest(b,d,e,f));Add(c,Closest(c,d,e,f));Add(Closest(d,a,b,c),d);Add(Closest(e,a,b,c),e);Add(Closest(f,a,b,c),f);
            Span<Vector3> av=stackalloc Vector3[]{a,b,c},bv=stackalloc Vector3[]{d,e,f};for(int i=0;i<3;i++)for(int j=0;j<3;j++){ClosestEdges(av[i],av[(i+1)%3],bv[j],bv[(j+1)%3],out var p,out var q);Add(p,q);}squared=best;pointA=pa;pointB=pb;
        }
        static Vector3 Closest(Vector3 p,Vector3 a,Vector3 b,Vector3 c)
        {
            Vector3 ab=b-a,ac=c-a,ap=p-a;float d1=Vector3.Dot(ab,ap),d2=Vector3.Dot(ac,ap);if(d1<=0&&d2<=0)return a;
            var bp=p-b;float d3=Vector3.Dot(ab,bp),d4=Vector3.Dot(ac,bp);if(d3>=0&&d4<=d3)return b;float vc=d1*d4-d3*d2;if(vc<=0&&d1>=0&&d3<=0)return a+ab*(d1/(d1-d3));
            var cp=p-c;float d5=Vector3.Dot(ab,cp),d6=Vector3.Dot(ac,cp);if(d6>=0&&d5<=d6)return c;float vb=d5*d2-d1*d6;if(vb<=0&&d2>=0&&d6<=0)return a+ac*(d2/(d2-d6));float va=d3*d6-d5*d4;if(va<=0&&d4-d3>=0&&d5-d6>=0)return b+(c-b)*((d4-d3)/(d4-d3+d5-d6));float den=1/(va+vb+vc);return a+ab*(vb*den)+ac*(vc*den);
        }
        static void ClosestEdges(Vector3 p1,Vector3 q1,Vector3 p2,Vector3 q2,out Vector3 a,out Vector3 b)
        {var d1=q1-p1;var d2=q2-p2;var r=p1-p2;float x=Vector3.Dot(d1,d1),e=Vector3.Dot(d2,d2),f=Vector3.Dot(d2,r),s,t;if(x<=1e-10f){s=0;t=e<=1e-10f?0:Math.Clamp(f/e,0,1);}else{float c=Vector3.Dot(d1,r);if(e<=1e-10f){t=0;s=Math.Clamp(-c/x,0,1);}else{float k=Vector3.Dot(d1,d2),den=x*e-k*k;s=den!=0?Math.Clamp((k*f-c*e)/den,0,1):0;t=(k*s+f)/e;if(t<0){t=0;s=Math.Clamp(-c/x,0,1);}else if(t>1){t=1;s=Math.Clamp((k-c)/x,0,1);}}}a=p1+d1*s;b=p2+d2*t;}
    }
}
