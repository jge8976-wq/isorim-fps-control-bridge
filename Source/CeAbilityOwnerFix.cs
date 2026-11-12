using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HSK.FpsBridge
{
    internal static class CeAbilityOwnerFix
    {
        static PropertyInfo ability;
        static FieldInfo owner;
        internal static int Repaired, Orphaned;
        internal static void Install(Harmony h)
        {
            Type t=AccessTools.TypeByName("CombatExtended.Verb_AbilityShootCE");
            ability=t==null?null:AccessTools.Property(t,"Ability");
            owner=AccessTools.Field(typeof(VerbTracker),"directOwner");
            if(ability?.PropertyType!=typeof(Ability)||owner==null)return;
            h.Patch(AccessTools.DeclaredMethod(t,"TryCastShot"),prefix:new HarmonyMethod(typeof(CeAbilityOwnerFix),nameof(BeforeShot)));
            Log.Message("[FpsBridge] CE_ABILITY_OWNER_GUARD READY");
        }
        internal static bool BeforeShot(Verb __instance,ref bool __result)
        {
            if(ability.GetValue(__instance) is Ability existing && existing.def!=null)return true;
            Ability parent=__instance.verbTracker==null?null:owner.GetValue(__instance.verbTracker) as Ability;
            if(parent?.def!=null && parent.pawn==__instance.CasterPawn)
            {ability.SetValue(__instance,parent);Repaired++;return true;}
            // A genuinely detached ability cannot cast. End this invalid burst rather than
            // emitting projectiles every tick before CE dereferences its missing owner.
            __instance.Reset();__result=false;Orphaned++;
            Log.WarningOnce("[FpsBridge] Detached CE ability burst cancelled: "+__instance.GetType().Name,19271972);
            return false;
        }
    }
}
