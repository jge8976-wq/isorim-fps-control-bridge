using System;
using System.Collections.Generic;
using IsoRim;
using UnityEngine;

namespace HSK.FpsBridge
{
    // View-only black-iron exposure. Source colours, geometry and world materials stay intact.
    internal static class HeldWeaponAppearance
    {
        private static readonly Dictionary<(Model,bool,bool),Model> cache=new();
        internal static bool NearBlack(Color c)
        {
            float hi=Mathf.Max(c.r,Mathf.Max(c.g,c.b)),lo=Mathf.Min(c.r,Mathf.Min(c.g,c.b));
            return hi<=.24f&&hi-lo<=.09f&&c.a>.05f;
        }
        internal static bool Paintable(ModelMaterial m)
        {
            if(m==null||m.glow||m.fire||m.beam||m.texture!=null)return false;
            string name=(m.name??"").ToLowerInvariant();
            foreach(string keep in new[]{"wood","leather","brass","copper","bullet","cartridge","glass","lens","optic_glass","emissive","smoked"})
                if(name.Contains(keep))return false;
            return true;
        }
        internal static Color Readable(Color source,ModelMaterial material)
        {
            if(!Paintable(material)||!NearBlack(source))return source;
            float hi=Mathf.Max(source.r,Mathf.Max(source.g,source.b));
            string name=(material.name??"").ToLowerInvariant();
            float level;
            if(name.Contains("sight"))level=.08f+.55f*hi;
            else if(name.Contains("rubber"))level=.065f+.80f*hi;
            else if(name.Contains("polymer")||name.Contains("grip"))level=.085f+.85f*hi;
            else if(name.Contains("steel")||name.Contains("metal")||name.Contains("rail")||name.Contains("bolt"))level=.11f+.90f*hi;
            else level=.10f+.80f*hi;
            return new Color(level*.92f,level*.97f,level,source.a);
        }
        internal static Model For(Model source,bool outlines,bool readable)
        {
            if(outlines&&!readable)return source;
            var key=(source,outlines,readable);
            if(cache.TryGetValue(key,out Model copy))return copy;
            // Allocate a unique upstream id so DynamicModels never reuses a world batch.
            copy=ObjLoader.WithoutSmallOutlines(source,float.MaxValue);
            if(outlines)copy.noOutline=source.noOutline;
            copy.beamFlicker=source.beamFlicker;copy.beamPulse=source.beamPulse;
            if(readable&&source.colors!=null)
            {
                copy.colors=(Color[])source.colors.Clone();
                if(source.tinted!=null)copy.tinted=(bool[])source.tinted.Clone();
                if(source.materials!=null)copy.materials=(ModelMaterial[])source.materials.Clone();
                for(int i=0;i<copy.colors.Length;i++)
                {
                    int slot=source.material!=null&&i<source.material.Length?source.material[i]:-1;
                    ModelMaterial material=slot>=0&&source.materials!=null&&slot<source.materials.Length?source.materials[slot]:null;
                    Color colour=Readable(source.colors[i],material);
                    if(colour.Equals(source.colors[i]))continue;
                    copy.colors[i]=colour;
                    if(copy.tinted!=null&&i<copy.tinted.Length)copy.tinted[i]=false;
                    if(copy.materials!=null&&ReferenceEquals(copy.materials[slot],material))
                        copy.materials[slot]=new ModelMaterial{name=material.name,texture=material.texture,color=Readable(material.color,material),
                            tinted=false,sheet=material.sheet,leaf=material.leaf,twig=material.twig,glow=material.glow,fire=material.fire,beam=material.beam,
                            runtimeTexture=material.runtimeTexture,runtimeName=material.runtimeName};
                }
            }
            cache[key]=copy;return copy;
        }
    }
}
