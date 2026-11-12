using System.Linq;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialBombTrial
    {
        internal static void Prepare(Map map)
        {
            if(!AerialPlaytest.Enabled)return;
            foreach(Pawn v in map.mapPawns.AllPawnsSpawned.Where(p=>p.def.defName=="VVE_Warbird"))
            {
                AerialArmament.Ensure(v);
                ThingDef shell=DefDatabase<ThingDef>.GetNamedSilentFail("Shell_HighExplosive");
                if(shell==null||v.inventory==null||v.inventory.innerContainer.Any(t=>t.def==shell))continue;
                Thing stock=ThingMaker.MakeThing(shell);stock.stackCount=20;v.inventory.innerContainer.TryAdd(stock);
                foreach(object gun in VehicleApi.Turrets(v).Where(AerialArmament.Bomb))VehicleApi.Call(gun,"AutoReload");
            }
        }
    }
}
