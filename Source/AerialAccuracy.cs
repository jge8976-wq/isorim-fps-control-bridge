using System.Collections.Generic;
using System.Linq;
using Verse;

namespace HSK.FpsBridge
{
    public sealed class AerialAccuracyDef:Def
    {
        public List<string> turretDefs=new();
        public float sway,spread;
    }
    internal static class AerialAccuracy
    {
        private static readonly HashSet<Def> checkedDefs=new();
        internal static void Apply(object gun)
        {
            var def=VehicleApi.Get(gun,"def") as Def;
            if(def==null||!checkedDefs.Add(def)||AerialArmament.Data(gun)==null)return;
            var policy=DefDatabase<AerialAccuracyDef>.AllDefsListForReading.FirstOrDefault(p=>p.turretDefs.Contains(def.defName));
            var ce=def.modExtensions?.FirstOrDefault(e=>e.GetType().FullName=="Vehicles.CETurretDataDefModExtension");
            // Strike Framework replaces the old FPS definitions. Repair only the
            // explicitly listed legacy zero/zero configurations after all XML patches.
            // Authored nonzero configurations, guided missiles and bombs remain authoritative.
            if(policy==null||ce==null||VehicleApi.Float(ce,"sway")!=0||VehicleApi.Float(ce,"spread")!=0)return;
            VehicleApi.Set(ce,"sway",policy.sway);VehicleApi.Set(ce,"spread",policy.spread);
            Log.Message("[FpsBridge] AIRCRAFT_ACCURACY legacy zero dispersion repaired: "+def.defName+" sway="+policy.sway+" spread="+policy.spread);
        }
    }
}
