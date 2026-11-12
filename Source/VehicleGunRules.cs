using System;

namespace HSK.FpsBridge
{
    // Quick fire addresses a physical machine gun, independently of the selected cannon/optic.
    internal static class VehicleGunRules
    {
        public static int RefilledCount(int previous, int refilled, int capacity, bool sameAmmo)
            => Math.Clamp(refilled + (sameAmmo ? Math.Max(0, previous) : 0), 0, Math.Max(0, capacity));
        public static bool MachineGun(string name)
        {
            name=(name??"").ToLowerInvariant();
            if(name.Contains("rocket")||name.Contains("missile")||name.Contains("swarm")||name.Contains("mortar")
                ||name.Contains("howitzer")||name.Contains("flame")||name.Contains("smoke"))return false;
            return name.Contains("machine")||name.Contains("mountedturret")||name.Contains("militaryturret")
                ||name.Contains("miniturret")||name.Contains("minigun")||name.Contains("gatling")||name.Contains("coax")
                ||name.Contains("autocannon")||name.Contains("机枪")||name.Contains("机炮")||name.Contains("機槍")||name.Contains("機砲");
        }
    }

    internal sealed class VehicleGunState
    {
        internal readonly VehicleWeaponClock Clock=new VehicleWeaponClock();
        internal readonly VehicleFireCycle Cycle;
        internal string Failure;
        internal V3? AimPoint;
        private int nextReload;
        public VehicleGunState(bool automatic){Cycle=new VehicleFireCycle(automatic);}
        public bool RetryReload(int tick,bool empty,bool reloading)
        {
            if(!empty||reloading||tick<nextReload)return false;
            nextReload=tick+60;return true;
        }
        public void ReloadStarted(int tick){nextReload=tick+60;Cycle.Interrupt();}
    }
}
