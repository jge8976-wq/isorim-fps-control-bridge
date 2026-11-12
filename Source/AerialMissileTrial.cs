using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
namespace HSK.FpsBridge
{
    // Explicit, per-aircraft opt-in, only in the marker-guarded private range.
    // Uses existing 80mm rounds to test the seeker; it is not a new real-world ammo claim.
    internal static class AerialMissileTrial
    {
        internal const string TestDef="FPS_Air_SeekerTrial80";
        internal static object Gun(Pawn v)=>VehicleApi.Turrets(v).FirstOrDefault(t=>VehicleApi.TurretKey(t)=="FPS_AirLoadout_3_Seeker80");
        internal static bool Active(Pawn v)=>(VehicleApi.Get(Gun(v),"def") as Def)?.defName==TestDef;
        // R14 test aircraft have a separate seeker mount by default. Preserve the old
        // helper entry for regression callers, without swapping away the rocket pod.
        internal static void Set(Pawn v,bool enabled){if(enabled&&v!=null)AerialArmament.Ensure(v);}
        internal static IEnumerable<Gizmo> Commands(Pawn v){yield break;}
        internal static void PrepareTargets(Map map)
        {
            if(!AerialPlaytest.Enabled||map.mapPawns.AllPawnsSpawned.Any(p=>p.Name?.ToStringFull=="Seeker test target"))return;
            Pawn v=map.mapPawns.AllPawnsSpawned.FirstOrDefault(p=>p.def.defName=="VVE_Warbird");if(v==null)return;
            Pawn target=PawnGenerator.GeneratePawn(PawnKindDefOf.Mech_Scyther,Faction.OfMechanoids);
            target.Name=new NameSingle("Seeker test target");
            GenSpawn.Spawn(target,new IntVec3(v.Position.x+8,0,v.Position.z+55).ClampInsideMap(map),map);
            var wait=JobMaker.MakeJob(JobDefOf.Wait);wait.expiryInterval=60000;
            target.jobs.StartJob(wait,Verse.AI.JobCondition.InterruptForced);
        }
    }
}
