using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace HSK.FpsBridge
{
    // CE 1.6 re-scribes four inherited fields. Core 1.7 now owns those same fields and keys.
    // Keep Core's reference registration and post-load cleanup, rather than registering twice.
    internal static class CeVersionFix
    {
        private static FieldInfo fragmentMap;
        internal static int CancelledFragments;
        internal static void Install(Harmony harmony)
        {
            Type fragments = AccessTools.TypeByName("CombatExtended.CompFragments");
            Type iterator = fragments?.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public).FirstOrDefault(t => t.Name.StartsWith("<FragRoutine>"));
            fragmentMap = iterator?.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).FirstOrDefault(f => f.FieldType == typeof(Map));
            if (fragmentMap != null)
            {
                harmony.Patch(AccessTools.DeclaredMethod(iterator, "MoveNext"), prefix: new HarmonyMethod(typeof(CeVersionFix), nameof(FragmentMapAlive)));
                Log.Message("[FpsBridge] CE_FRAGMENT_MAP_GUARD READY; abandoned routines cannot touch disposed native map grids");
            }
            Type ce = AccessTools.TypeByName("CombatExtended.ExplosionCE");
            if (ce == null || !typeof(Explosion).IsAssignableFrom(ce)) return;
            MethodInfo method = AccessTools.DeclaredMethod(ce, "ExposeData");
            MethodInfo native = AccessTools.DeclaredMethod(typeof(Explosion), "ExposeData");
            if (method == null || native == null) return;
            var own = PatchProcessor.GetOriginalInstructions(method).ToArray();
            var inherited = PatchProcessor.GetOriginalInstructions(native).ToArray();
            string[] keys = { "startTick", "cellsToAffect", "damagedThings", "addedCellsAffectedOnlyByDamage" };
            string[] literals = own.Where(i => i.opcode == OpCodes.Ldstr).Select(i => (string)i.operand).ToArray();
            // A future CE version may serialize its own state: do not replace an unknown method.
            if (literals.Length != keys.Length || !keys.All(k => literals.Contains(k)) ||
                !keys.All(k => inherited.Any(i => i.opcode == OpCodes.Ldstr && Equals(i.operand, k))) ||
                own.Any(i => i.operand is FieldInfo field && field.DeclaringType == ce)) return;
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(CeVersionFix), nameof(SerializeOnce)));
            Log.Message("[FpsBridge] CE_17_EXPLOSION_SAVE_GUARD READY; inherited fields serialized once by Core");
        }
        private static bool FragmentMapAlive(object __instance, ref bool __result)
        {
            Map map = fragmentMap.GetValue(__instance) as Map;
            if (map != null && !map.Disposed && Current.ProgramState == ProgramState.Playing) return true;
            CancelledFragments++;
            __result = false;
            return false;
        }
        private static IEnumerable<CodeInstruction> SerializeOnce(IEnumerable<CodeInstruction> unused)
        {
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(Explosion), "ExposeData"));
            yield return new CodeInstruction(OpCodes.Ret);
        }
    }
}
