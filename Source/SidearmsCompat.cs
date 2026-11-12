using System;
using HarmonyLib;
using Verse;

namespace HSK.FpsBridge
{
    internal static class SidearmsCompat
    {
        private static bool checkedTypes, unavailable;
        private static SidearmsPreferenceBinding binding;

        public static void RememberSelection(Pawn pawn, ThingWithComps equipped)
        {
            try
            {
                if (!checkedTypes)
                {
                    checkedTypes = true;
                    if (ModLister.GetActiveModWithIdentifier("petetimessix.simplesidearms", ignorePostfix: true) == null) return;
                    Type memory = AccessTools.TypeByName("SimpleSidearms.rimworld.CompSidearmMemory")
                        ?? throw new TypeLoadException("Simple Sidearms CompSidearmMemory is unavailable");
                    binding = new SidearmsPreferenceBinding(memory, typeof(Pawn), typeof(ThingDef));
                    Log.Message("[FpsBridge] SIDEARMS_COMPAT READY; assembly=" + memory.Assembly.FullName + "; path=" + memory.Assembly.Location);
                }
                if (unavailable || binding == null || pawn?.equipment?.Primary != equipped) return;
                if (binding.Apply(pawn, equipped.def, equipped.Stuff, equipped.def.IsMeleeWeapon, pawn.Drafted))
                    Log.Message("[FpsBridge] SIDEARMS_PREFERENCE pawn=" + pawn.thingIDNumber + "; weapon=" + equipped.ThingID + "; drafted=" + pawn.Drafted);
                else
                    Log.WarningOnce("[FpsBridge] Simple Sidearms has no memory for the selected pawn; weapon equipped but preference was not synchronized.", 19271708);
            }
            catch (Exception e)
            {
                // An optional integration failure must not undo a successful inventory transfer or end control.
                unavailable = true;
                Log.WarningOnce("[FpsBridge] Simple Sidearms preference sync unavailable; Tab still equips but auto-switch may override it. " + e.GetBaseException().Message, 19271707);
            }
        }
    }
}
