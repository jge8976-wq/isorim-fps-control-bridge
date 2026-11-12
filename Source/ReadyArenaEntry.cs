using System;
using System.IO;
using HarmonyLib;
using LudeonTK;
using Verse;

namespace HSK.FpsBridge
{
    [StaticConstructorOnStartup]
    internal static class ReadyArenaEntry
    {
        static ReadyArenaEntry()
        {
            string marker = Path.Combine(GenFilePaths.SaveDataFolderPath, "all-vehicles-ready.marker");
            if (!GenCommandLine.CommandLineArgPassed("fps-ready-arena") || !File.Exists(marker)
                || File.ReadAllText(marker).Trim() != "ISORIM_ALL_VEHICLES_READY_20261108") return;
            AccessTools.Field(typeof(EditWindow_Log), "canAutoOpen").SetValue(null, false);
            EditWindow_Log.wantsToOpen = false;
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                EditWindow_Log.wantsToOpen = false;
                Find.WindowStack?.TryRemove(typeof(EditWindow_Log), false);
            });
            Log.Message("[FpsBridge] READY_ARENA_ENTRY active; error log opens manually in this dedicated session");
        }
    }
}
