using System.Collections.Generic;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static partial class CeCompat
    {
        private static HashSet<ThingDef> fragmentDefs;
        internal static int FragmentDraws;

        internal static bool IsFragment(ThingDef projectile)
        {
            if (fragmentDefs == null)
            {
                fragmentDefs = new HashSet<ThingDef>();
                var properties = Named("CompProperties_Fragments");
                foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    if (def.comps == null) continue;
                    foreach (CompProperties comp in def.comps)
                    {
                        if (properties == null || !properties.IsInstanceOfType(comp)) continue;
                        foreach (object value in VehicleApi.List(comp, "fragments"))
                            if (value is ThingDefCountClass fragment && fragment.thingDef != null)
                                fragmentDefs.Add(fragment.thingDef);
                    }
                }
            }
            return projectile != null && fragmentDefs.Contains(projectile);
        }

        private static bool DrawFragment(Thing projectile)
        {
            if (!View3D.Active || projectile.Map == null || !IsFragment(projectile.def)) return false;
            Vector3 native = vector3.Read3(exactPos.GetValue(projectile));
            Vector3 last = vector3.Read3(exactLastPos.GetValue(projectile));
            V3 position = NativeWorld(projectile.Map, native);
            V3 delta = position - NativeWorld(projectile.Map, last);
            if (delta.Length < .00001f && VehicleApi.Get(projectile, "velocity") is object velocity)
                delta = NativeVelocityToWorld(VehicleModels.V(vector3.Read3(velocity)));
            if (!Visuals.Fragment(position, delta, projectile.def.graphicData?.drawSize.x ?? 1f, projectile.thingIDNumber))
                return false;
            if (projectile is ThingWithComps withComps)
                foreach (ThingComp comp in withComps.AllComps) comp.PostDraw();
            FragmentDraws++;
            return true;
        }
    }
}
