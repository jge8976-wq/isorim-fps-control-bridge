using HarmonyLib;
using IsoRim;
using RimWorld;
using Verse;

namespace HSK.FpsBridge
{
    internal static class TurretView
    {
        private static Thing hidden;

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.DynamicDrawPhaseAt)),
                prefix: Prefix(nameof(DrawThing)));
            harmony.Patch(AccessTools.Method(typeof(TurretTop), nameof(TurretTop.DrawTurret)),
                prefix: Prefix(nameof(DrawTop)));
            var print = AccessTools.Method(MapSections.ThingsLayer, "TakePrintFrom");
            if (print != null) harmony.Patch(print, prefix: Prefix(nameof(PrintThing)));
            harmony.Patch(AccessTools.Method(typeof(SectionLayer_IsoRimBodies), nameof(SectionLayer_IsoRimBodies.Handles)),
                prefix: Prefix(nameof(BakeBody)));
        }

        private static HarmonyMethod Prefix(string name) => new HarmonyMethod(typeof(TurretView), name) { priority = Priority.First };

        internal static void Set(Thing turret)
        {
            if (hidden == turret) return;
            Thing previous = hidden;
            hidden = turret;
            RebuildBase(previous);
            RebuildBase(turret);
        }

        private static void RebuildBase(Thing turret)
        {
            if (turret?.Spawned != true) return;
            // IsoRim can bake the base into a section mesh independently of the rotating top.
            MapSections.QueueAround(turret.Map, turret.Position, 0, MapSections.ThingsLayer, includeOwn: true);
            MapSections.QueueAround(turret.Map, turret.Position, 0, typeof(SectionLayer_IsoRimBodies), includeOwn: true);
        }

        internal static bool Hides(Thing thing) => hidden != null && hidden == thing && View3D.Active;

        private static bool DrawThing(Thing __instance, DrawPhase __0) => __0 != DrawPhase.Draw || !Hides(__instance);
        private static bool DrawTop(Building_Turret ___parentTurret) => !Hides(___parentTurret);
        private static bool PrintThing(Thing __0) => !Hides(__0);

        private static bool BakeBody(Thing __0, ref bool __result)
        {
            if (!Hides(__0)) return true;
            __result = false;
            return false;
        }
    }
}
