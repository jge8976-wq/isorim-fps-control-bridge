using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    [StaticConstructorOnStartup]
    internal static class AerialPlaytest
    {
        internal static bool Enabled=>GenCommandLine.CommandLineArgPassed("fps-air-playtest")
            && Path.GetFullPath(GenFilePaths.SaveDataFolderPath).Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)
                .Contains("Aerial-Cockpit-20261104-R14",StringComparer.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(GenFilePaths.SaveDataFolderPath,"air-playtest.marker.json"));
        static AerialPlaytest()
        {
            if(!Enabled||GenCommandLine.CommandLineArgPassed("vtol-native-check"))return;
            LongEventHandler.ExecuteWhenFinished(()=>{
                try
                {
                    using(var marker=JsonDocument.Parse(File.ReadAllText(Path.Combine(GenFilePaths.SaveDataFolderPath,"air-playtest.marker.json"))))
                    {
                        string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(AerialPlaytest).Assembly.Location)));
                        if(hash!=marker.RootElement.GetProperty("sha256").GetString())throw new InvalidOperationException("Private air test candidate changed");
                    }
                    string save=File.Exists(Path.Combine(GenFilePaths.SaveDataFolderPath,"Saves","Flight-Ready-R4.rws"))?"Flight-Ready-R4":"Flight-Isolated-R4";
                    LongEventHandler.QueueLongEvent(()=>GameDataSaveLoader.LoadGame(save),"LoadingLongEvent",false,null);
                }
                catch(Exception e){Log.Error("[FpsBridge] Automatic air test startup: "+e);}
            });
        }
        internal static void ResetSession()
        {
            if(Bridge.Active)Bridge.ReleaseForVehicle();
            FirstPerson.Leave(null);Bindings.ClearAvatar();
            AccessTools.Field(Bindings.StateType,"CameraLockPosition")?.SetValue(null,null);
            ClosePrompts();
        }
        internal static void ClosePrompts()
        {
            foreach(Window w in Find.WindowStack.Windows.ToArray())
                if(w is LudeonTK.EditWindow_Log||w.GetType().FullName=="VEF.Factions.Dialog_NewFactionSpawning")Find.WindowStack.TryRemove(w,false);
        }
    }
    public sealed class AerialPlaytestScene:GameComponent
    {
        private bool prepared;
        private int frames;
        private bool done;
        private int promptFrames;
        public AerialPlaytestScene(Game game){}
        public override void ExposeData()=>Scribe_Values.Look(ref prepared,"fpsAirPlaytestPreparedR4");
        public override void GameComponentUpdate()
        {
            if(AerialPlaytest.Enabled&&Current.ProgramState==ProgramState.Playing&&Find.CurrentMap!=null&&promptFrames++<180)AerialPlaytest.ClosePrompts();
            if(done||!AerialPlaytest.Enabled||Current.ProgramState!=ProgramState.Playing||Find.CurrentMap==null||!VehicleApi.Ready||!AerialFlight.Enabled)return;
            if(++frames<30)return;
            done=true;
            try
            {
                Map map=Find.CurrentMap;AerialPlaytest.ResetSession();View3D.Active=true;Find.TickManager.CurTimeSpeed=TimeSpeed.Paused;
                if(!prepared)
                {
                    // Only this marker-guarded, isolated range is changed. The original template/save is never opened for writing.
                    foreach(IntVec3 c in CellRect.CenteredOn(map.Center,220,220).Cells)
                    {
                        if(!c.InBounds(map))continue;
                        map.roofGrid.SetRoof(c,null);map.fogGrid.Unfog(c);
                        foreach(Thing t in c.GetThingList(map).ToArray())if(t.def.destroyable&&(t is Building||t is Plant))t.Destroy(DestroyMode.Vanish);
                        map.terrainGrid.SetTerrain(c,TerrainDefOf.Soil);
                    }
                    // The template predates runway flight: move its sample actors out of the takeoff/landing lanes.
                    int sample=0;
                    foreach(Pawn p in map.mapPawns.AllPawnsSpawned.Where(p=>!VehicleApi.IsVehicle(p)).ToArray())
                    {
                        IntVec3 safe=new IntVec3(18+sample%16,0,18+sample/16);sample++;
                        if(!safe.InBounds(map)||!safe.Standable(map))continue;
                        p.jobs?.StopAll();p.Position=safe;p.Notify_Teleported();
                    }
                    AerialDebugActions.CreateRange();prepared=true;
                    if(!GenCommandLine.CommandLineArgPassed("vtol-native-check"))
                        LongEventHandler.QueueLongEvent(()=>GameDataSaveLoader.SaveGame("Flight-Ready-R4"),"SavingLongEvent",false,null);
                }
                AerialBombTrial.Prepare(map);
                if(!GenCommandLine.CommandLineArgPassed("vtol-native-check"))AerialMissileTrial.PrepareTargets(map);
                Find.Selector.ClearSelection();Find.CameraDriver.JumpToCurrentMapLoc(map.Center.ToVector3Shifted());
                Directory.CreateDirectory(Path.Combine(GenFilePaths.SaveDataFolderPath,"Logs"));
                File.WriteAllText(Path.Combine(GenFilePaths.SaveDataFolderPath,"Logs","Aerial-scene-ready.json"),JsonSerializer.Serialize(new{status="AIR_SCENE_READY",automatic=true,aircraft=map.mapPawns.AllPawnsSpawned.Count(p=>AerialFlight.Supports(p))}));
            }
            catch(Exception e){Log.Error("[FpsBridge] Automatic air range: "+e);}
        }
    }
}
