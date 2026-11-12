using System.Collections.Generic;
using Verse;

namespace HSK.FpsBridge
{
    [StaticConstructorOnStartup]
    internal static class RatkinIntegrationAudit
    {
        static RatkinIntegrationAudit()
        {
            if(DefDatabase<ThingDef>.GetNamedSilentFail("Ratkin")==null)return;
            var labels=new List<string>();int translated=0;
            foreach(string id in new[]{"Ratkin","RK_Building_Ballista","RK_Building_Cannon","RK_Ammo_CannonBall"})
            {
                string label=DefDatabase<ThingDef>.GetNamedSilentFail(id)?.label??"MISSING";
                foreach(char c in label)if(c>='\u3400' && c<='\u9fff'){translated++;break;}
                labels.Add(id+"="+label);
            }
            Log.Message("[FpsBridge] RATKIN_INTEGRATION READY; eye-scale=0.50; furniture-seat="+FurnitureSurfaces.SeatHeight.ToString("0.00")
                +"; custom-projectiles="+(RatkinTurretShots.Enabled?"ON":"OFF")+"; Chinese-labels="+translated+"/4; "+string.Join("; ",labels));
        }
    }
}
