using System.Collections.Generic;
using IsoRim;
using UnityEngine;
using Verse;
namespace HSK.FpsBridge
{
    // Reuse the established CWM first-person material path with original OBJ data.
    internal static class HeldWeaponLighting
    {
        private sealed class Batch { internal Mesh mesh;internal Material material; }
        private static readonly Dictionary<(Model,Color),List<Batch>> cache=new();
        internal static int StableNormalDraws;
        internal static bool Draw(Model model,Color tint,Matrix4x4 render)
        {
            if(FpsMod.Settings.firstPersonWeaponOutlines||model.materials==null||model.normals==null||model.normals.Length!=model.positions.Length||model.beam!=null)return false;
            foreach(var mat in model.materials)if(mat.texture!=null)return false;
            var key=(model,tint);
            if(!cache.TryGetValue(key,out var batches))
            {
                batches=new();
                foreach(bool glow in new[]{false,true})
                {
                    var vertices=new List<Vector3>();var normals=new List<Vector3>();var colours=new List<Color32>();var uv=new List<Vector2>();var indices=new List<int>();
                    for(int i=0;i<model.positions.Length;i++)
                    {
                        int slot=model.material!=null?model.material[i]:0;if(model.materials[slot].glow!=glow)continue;
                        vertices.Add(model.positions[i]);normals.Add(model.normals[i]);uv.Add(Vector2.zero);indices.Add(indices.Count);
                        Vector3 n=model.normals[i].normalized;
                        Vector3 keyLight=new Vector3(-.35f,.8f,-.45f).normalized;
                        float shade=glow?1f:Mathf.Clamp(.78f+.30f*Vector3.Dot(n,keyLight)+.10f*Mathf.Abs(n.y),.52f,1.12f);
                        Color c=model.colors[i]*(model.tinted!=null&&model.tinted[i]?tint:Color.white);
                        colours.Add((Color32)new Color(c.r*shade,c.g*shade,c.b*shade,c.a));
                    }
                    if(vertices.Count==0)continue;
                    var mesh=new Mesh();if(vertices.Count>65535)mesh.indexFormat=UnityEngine.Rendering.IndexFormat.UInt32;
                    mesh.SetVertices(vertices);mesh.SetNormals(normals);mesh.SetColors(colours);mesh.SetUVs(0,uv);mesh.SetTriangles(indices,0);mesh.RecalculateBounds();
                    var material=R3DMaterials.Make(R3DMaterials.SolidTexDef,"HSKFps_Held_"+model.id+"_"+glow,-1,0);
                    if(material==null)return false;
                    material.mainTexture=BaseContent.WhiteTex;material.color=Color.white;
                    if(!glow)IsoGlow.Use(material,IsoGlow.OwnCell);
                    // World AO captures depth at queue 2450. View models are drawn
                    // after world effects, using their own cell lighting instead.
                    material.renderQueue=3102;
                    batches.Add(new(){mesh=mesh,material=material});
                }
                cache[key]=batches;
            }
            foreach(var batch in batches)Graphics.DrawMesh(batch.mesh,render,batch.material,0);
            StableNormalDraws++;return true;
        }
    }
}
