using IsoRim;
using RimWorld;
using Verse;

namespace HSK.FpsBridge
{
    // Test aid: Alt+5 while possessed stops drawing the fog of war (IsoRim's fog shading and fog lid, the game's own
    // fog layer) without revealing anything: the fog grid, area triggers and sleeping threats are left as they are.
    // Alt+5 again, or the game closing, draws it again with the player's own settings.
    internal static class FogPeek
    {
        private static bool hidden, savedLid, savedEnabled, savedDraw;

        public static void Toggle()
        {
            if (!hidden)
            {
                savedLid = IsoRimMod.Settings.fogLid; savedEnabled = RoofClip.FogEnabled; savedDraw = DebugViewSettings.drawFog;
                IsoRimMod.Settings.fogLid = false; RoofClip.FogEnabled = false; DebugViewSettings.drawFog = false;
                hidden = true;
            }
            else Restore();
            Redraw();
            Messages.Message(hidden ? L.T("迷雾显示已关闭（测试用，Alt+5 恢复）", "Fog of war hidden (test aid, Alt+5 to restore)") : L.T("迷雾显示已恢复", "Fog of war shown again"), MessageTypeDefOf.SilentInput, false);
            Log.Message("[FpsBridge] FOG_PEEK " + (hidden ? "HIDDEN" : "SHOWN"));
        }

        public static void Reset() { if (hidden) Restore(); }

        private static void Restore()
        {
            IsoRimMod.Settings.fogLid = savedLid; RoofClip.FogEnabled = savedEnabled; DebugViewSettings.drawFog = savedDraw;
            hidden = false;
        }

        private static void Redraw()
        {
            try { Find.CurrentMap?.mapDrawer.RegenerateEverythingNow(); }
            catch (System.Exception e) { Log.Warning("[FpsBridge] Fog redraw failed: " + e.Message); }
        }
    }
}
