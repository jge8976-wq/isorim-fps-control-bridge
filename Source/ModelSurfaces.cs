using System;
using System.Collections.Generic;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Ratkin use the same model placement as IsoRim's baker. No new ground collider or path-cost change.
    internal static class ModelSurfaces
    {
        private sealed class Geometry { public Model model; public Matrix4x4 matrix; public Vector3[] vertices; }
        private static readonly Dictionary<Thing,Geometry> cache = new Dictionary<Thing,Geometry>();
        private static readonly Vector2[] feet = {new Vector2(.055f,0),new Vector2(-.055f,0),new Vector2(0,.055f),new Vector2(0,-.055f)};
        private static float crouchAt = -10f;
        internal static bool Ratkin(Pawn p) => PawnPoseRules.Scale(p?.def?.defName) < 1f;
        internal static bool Candidate(Thing t) => t?.Spawned == true && !t.Destroyed && !(t is Pawn)
            && !(t is Building_Door) && !(t is Blueprint) && !(t is Frame) && !(t is Corpse)
            && (t.def.category == ThingCategory.Building || t.def.category == ThingCategory.Item
                || t is Plant && t.def.plant?.IsTree == true);
        internal static bool Valid(Thing t) => Candidate(t) && Api.ModelsOn && Api.ModelStandsIn(t);
        internal static void Reset() { cache.Clear(); crouchAt = -10f; }
        internal static bool DropKey(Pawn p, float now)
        {
            if (!Ratkin(p) || !Surface.For(p)) { crouchAt = -10f; return false; }
            bool twice = now - crouchAt <= .32f; crouchAt = twice ? -10f : now;
            if (twice) Surface.Drop("Ratkin double crouch");
            return twice;
        }
        internal static bool Top(Thing t, Vector3 at, out float top)
        {
            top = -1;
            if (!Valid(t) || !ModelLibrary.TryGet(t.def,out ModelEntry entry) || !entry.HasModel || entry.def.flat) return false;
            Model model = entry.Pick(t);
            if (model?.positions == null) return false;
            Vector3 origin = ModelBaker.PointOnMap(t,entry,model,Vector3.zero,out Vector3 fit,out float yaw);
            Matrix4x4 place = Matrix4x4.TRS(origin,Quaternion.AngleAxis(yaw,Vector3.up),fit);
            if (entry.def.layFlat) place *= Matrix4x4.Translate(new Vector3(0,model.halfDepth,0)) * Matrix4x4.Rotate(Quaternion.Euler(90,0,0));
            if (!cache.TryGetValue(t,out Geometry g) || g.model != model || g.matrix != place)
            {
                if (cache.Count > 32) cache.Clear();
                g = new Geometry { model=model,matrix=place,vertices=new Vector3[model.positions.Length] };
                for(int i=0;i<g.vertices.Length;i++) g.vertices[i]=place.MultiplyPoint3x4(model.positions[i]);
                cache[t]=g;
            }
            return Support(g.vertices,at,out top);
        }
        // A downward triangle query, plus four small foot-support samples. Empty gaps and vertical cards are not floors.
        internal static bool Support(Vector3[] vertices,Vector3 at,out float top)
        {
            top=-1;
            if (!Ray(vertices,at.x,at.z,out float centre)) return false;
            foreach(Vector2 d in feet)
                if(!Ray(vertices,at.x+d.x,at.z+d.y,out float foot) || Mathf.Abs(foot-centre)>.24f) return false;
            top=centre; return top>.08f;
        }
        private static bool Ray(Vector3[] v,float x,float z,out float y)
        {
            y=-1;
            for(int i=0;i+2<v.Length;i+=3)
            {
                Vector3 a=v[i],b=v[i+1],c=v[i+2];
                if(x<Mathf.Min(a.x,Mathf.Min(b.x,c.x)) || x>Mathf.Max(a.x,Mathf.Max(b.x,c.x))
                    || z<Mathf.Min(a.z,Mathf.Min(b.z,c.z)) || z>Mathf.Max(a.z,Mathf.Max(b.z,c.z)))continue;
                Vector3 normal=Vector3.Cross(b-a,c-a);
                if(normal.y<.000001f || normal.y < normal.magnitude*.45f) continue;
                float den=(b.z-c.z)*(a.x-c.x)+(c.x-b.x)*(a.z-c.z);
                if(Mathf.Abs(den)<1e-8f)continue;
                float u=((b.z-c.z)*(x-c.x)+(c.x-b.x)*(z-c.z))/den;
                float w=((c.z-a.z)*(x-c.x)+(a.x-c.x)*(z-c.z))/den;
                if(u<-.00001f || w<-.00001f || u+w>1.00001f)continue;
                y=Mathf.Max(y,u*a.y+w*b.y+(1-u-w)*c.y);
            }
            return y>=0;
        }
        internal static bool At(Map map,IntVec3 cell,Vector3 at,out Thing support,out float top)
        {
            support=null;top=-1;
            if(map==null || !cell.InBounds(map))return false;
            foreach(Thing t in cell.GetThingList(map))
                if(Top(t,at,out float h) && h>top){support=t;top=h;}
            return support!=null;
        }
        internal static bool Near(Pawn p,Vector3 at,Vector3 forward,out Thing support,out Vector3 landing,out float height)
        {
            support=null;landing=Vector3.zero;height=0;float best=float.MaxValue;
            var seen=new HashSet<Thing>();
            foreach(IntVec3 c in GenRadial.RadialCellsAround(p.Position,2f,true))
            {
                if(!c.InBounds(p.Map) || FloorSpace.Band(p.Map,c).Level!=FloorSpace.Band(p.Map,p.Position).Level)continue;
                foreach(Thing t in c.GetThingList(p.Map))
                {
                    if(!seen.Add(t) || !Candidate(t))continue;
                    CellRect rect=t.OccupiedRect();
                    Vector3 target=new Vector3(Mathf.Clamp(at.x+forward.x*.85f,rect.minX+.14f,rect.maxX+.86f),0,
                        Mathf.Clamp(at.z+forward.z*.85f,rect.minZ+.14f,rect.maxZ+.86f));
                    // A seat narrower than its cell may not cover the nearest edge. Probe inward too.
                    Vector3 centre=new Vector3((rect.minX+rect.maxX+1)*.5f,0,(rect.minZ+rect.maxZ+1)*.5f);
                    Vector3 nearest=target;bool found=false;float h=0;
                    for(int probe=0;probe<4;probe++)
                    {
                        target=Vector3.Lerp(nearest,centre,probe/3f);
                        Vector3 distance=target-at;distance.y=0;
                        if(distance.magnitude<=1.6f && Vector3.Dot(distance,forward)>.05f && Top(t,target,out h)){found=true;break;}
                    }
                    if(!found)continue;
                    Vector3 delta=target-at;delta.y=0;
                    bool blocked=false;
                    for(float u=.2f;u<delta.magnitude;u+=.2f)
                    {
                        IntVec3 between=(at+delta.normalized*u).ToIntVec3();
                        Building wall=between.InBounds(p.Map)?between.GetEdifice(p.Map):null;
                        if(between!=p.Position && wall!=null && wall!=t && wall.def.passability==Traversability.Impassable){blocked=true;break;}
                    }
                    if(blocked)continue;
                    float rise=h-Surface.HeightOf(p);
                    if(rise<=.08f)continue;
                    float score=delta.magnitude + .5f*(1-Vector3.Dot(delta.normalized,forward));
                    if(score<best){best=score;support=t;landing=target;height=h;}
                }
            }
            return support!=null;
        }
    }
}
