using System.Collections.Generic;
using Verse;

namespace HSK.FpsBridge
{
    internal static class VehicleAuxiliary
    {
        private static readonly Dictionary<object,bool> original=new Dictionary<object,bool>();
        internal static void Frame()
        {
            if(!VehicleControl.On)return;
            bool capable=VehicleControl.OperatorCapable(true);
            foreach(var gun in VehicleApi.Turrets(VehicleControl.Vehicle))
            {
                if(!original.ContainsKey(gun))original.Add(gun,VehicleApi.Bool(gun,"AutoTarget"));
                // Keep manual launchers, bombs and seeker pods on their original manual path.
                bool automatic=capable&&!VehicleWeapons.ManualGun(gun)&&!AerialArmament.Trial(gun)&&VehicleApi.Bool(gun,"CanAutoTarget");
                if(VehicleApi.Bool(gun,"AutoTarget")!=automatic)VehicleApi.Set(gun,"AutoTarget",automatic);
                if(automatic)VehicleApi.Call(gun,"StartTicking");
            }
        }
        internal static void Restore(Pawn vehicle)
        {
            if(vehicle?.Spawned==true&&!vehicle.Destroyed)
                foreach(var item in original)if(VehicleApi.Turrets(vehicle).Contains(item.Key))VehicleApi.Set(item.Key,"AutoTarget",item.Value);
            original.Clear();
        }
    }
}
