using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    public sealed class RoofDamageRecord : IExposable
    {
        public IntVec3 Cell;
        public RoofDef Roof;
        public float Remaining;
        public void ExposeData()
        {
            Scribe_Values.Look(ref Cell, "cell");
            Scribe_Defs.Look(ref Roof, "roof");
            Scribe_Values.Look(ref Remaining, "remaining");
        }
    }

    public sealed class RoofHealthMap : MapComponent
    {
        private readonly Dictionary<IntVec3, RoofDamageRecord> damaged = new();
        private List<RoofDamageRecord> saved;
        public RoofHealthMap(Map map) : base(map) { }
        internal static float Maximum(RoofDef roof) => roof == null ? 0 : roof.isThickRoof ? 1500 : roof.isNatural ? 250 : 100;
        internal float Remaining(IntVec3 cell, RoofDef roof) => damaged.TryGetValue(cell, out var r) && r.Roof == roof ? r.Remaining : Maximum(roof);
        internal void Forget(IntVec3 cell) => damaged.Remove(cell);
        internal bool Damage(IntVec3 cell, RoofDef roof, float amount)
        {
            if (!cell.InBounds(map) || !RoofWear.Breakable(roof) || map.roofGrid.RoofAt(cell) != roof || !float.IsFinite(amount) || amount <= 0) return false;
            float health = Remaining(cell, roof) - amount;
            if (health > 0)
            {
                damaged[cell] = new RoofDamageRecord { Cell = cell, Roof = roof, Remaining = health };
                return false;
            }
            damaged.Remove(cell);
            Surface.Collapse(map, cell, roof, false);
            if (Surface.StandingOn(map, cell) && cell.Standable(map)) Surface.Drop("roof destroyed");
            return true;
        }
        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
                saved = damaged.Values.Where(r => r.Cell.InBounds(map) && map.roofGrid.RoofAt(r.Cell) == r.Roof).ToList();
            Scribe_Collections.Look(ref saved, "fpsRoofHealthR1", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                damaged.Clear();
                foreach (var r in saved ?? new List<RoofDamageRecord>())
                    if (r != null && r.Cell.InBounds(map) && r.Roof == map.roofGrid.RoofAt(r.Cell) && float.IsFinite(r.Remaining) && r.Remaining > 0)
                    {
                        r.Remaining = Math.Min(r.Remaining, Maximum(r.Roof));
                        damaged[r.Cell] = r;
                    }
                saved = null;
            }
        }
    }

    internal static class RoofHealth
    {
        internal static int BlastCellsDamaged, BlastCellsDestroyed;
        private static readonly ConditionalWeakTable<Explosion, HashSet<IntVec3>> affected = new();
        private static PropertyInfo ceDamageInfo;
        private static readonly FieldInfo gridMap = AccessTools.Field(typeof(RoofGrid), "map");
        public static void BeforeSet(RoofGrid __instance, IntVec3 __0, out RoofDef __state) => __state = __instance.RoofAt(__0);
        public static void AfterSet(RoofGrid __instance, IntVec3 __0, RoofDef __state)
        {
            if (__state != __instance.RoofAt(__0) && gridMap.GetValue(__instance) is Map map)
                map.GetComponent<RoofHealthMap>()?.Forget(__0);
        }
        public static void Blast(Explosion explosion, IntVec3 c)
        {
            Map map = explosion.Map;
            if (map == null || !c.InBounds(map) || explosion.damType?.harmsHealth != true) return;
            var visited = affected.GetValue(explosion, _ => new HashSet<IntVec3>());
            if (!visited.Add(c)) return; // Overrides can call their base worker for the same cell.
            RoofDef roof = map.roofGrid.RoofAt(c);
            if (!RoofWear.Breakable(roof)) return;
            FloorBand band = FloorSpace.Band(map, c);
            float height = band.Height + RoofShape.HeightAt(map, c.x + .5f, c.z + .5f);
            V3 point = new V3(c.x + .5f, height, c.z - band.Shift + .5f);
            if ((point - AerialBlast.Origin(explosion)).Length > explosion.radius + .01f) return;
            float amount = explosion.GetDamageAmountAt(c) * explosion.damType.buildingDamageFactor;
            if (amount <= 0) return;
            BlastCellsDamaged++;
            if (map.GetComponent<RoofHealthMap>().Damage(c, roof, amount)) BlastCellsDestroyed++;
        }
        internal sealed class Contact
        {
            internal Map Map;
            internal IntVec3 Cell;
            internal RoofDef Roof;
            internal float Amount;
        }
        public static void BeforeCeRoof(Thing __instance, IntVec3 __0, out Contact __state)
        {
            Map map = __instance.Map;
            __state = null;
            if (map == null || !__0.InBounds(map)) return;
            if (!RoofWear.Breakable(map.roofGrid.RoofAt(__0))) return;
            if (ceDamageInfo?.GetValue(__instance) is DamageInfo info && info.Def?.harmsHealth == true)
                __state = new Contact { Map = map, Cell = __0, Roof = map.roofGrid.RoofAt(__0), Amount = info.Amount };
        }
        public static void AfterCeRoof(bool __result, Contact __state)
        {
            if (__result && __state != null) RoofWear.Damage(__state.Map, __state.Cell, __state.Roof, __state.Amount);
        }
        internal static float ProjectileDamage(Thing projectile)
        {
            if (projectile.def.projectile?.damageDef?.harmsHealth != true) return 0;
            if (projectile is Projectile vanilla) return vanilla.DamageAmount;
            return ceDamageInfo?.GetValue(projectile) is DamageInfo info ? info.Amount : 0;
        }
        private static void OverheadDamage(IntVec3 cell, Map map, Thing projectile)
        {
            if (cell.InBounds(map)) RoofWear.Damage(map, cell, map.roofGrid.RoofAt(cell), ProjectileDamage(projectile));
        }
        public static void OverheadContact(IntVec3 cell, Map map, Thing projectile) => OverheadDamage(cell, map, projectile);
        public static void OverheadContactWithThings(IntVec3 cell, Map map, List<Thing> things, Thing projectile) => OverheadDamage(cell, map, projectile);
        // Replace only the native unconditional roof removal. Native shielding and impact ordering remain authoritative.
        public static IEnumerable<CodeInstruction> OverheadRoof(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.operand is MethodInfo method && method.DeclaringType == typeof(RoofCollapserImmediate) && method.Name == "DropRoofInCells")
                {
                    var self = new CodeInstruction(OpCodes.Ldarg_0);
                    self.labels.AddRange(instruction.labels);
                    instruction.labels.Clear();
                    self.blocks.AddRange(instruction.blocks);
                    instruction.blocks.Clear();
                    yield return self;
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(RoofHealth), method.GetParameters().Length == 2 ? nameof(OverheadContact) : nameof(OverheadContactWithThings));
                }
                yield return instruction;
            }
        }
        internal static void Install(Harmony harmony)
        {
            if (gridMap == null) throw new MissingFieldException("RoofGrid.map");
            harmony.Patch(AccessTools.Method(typeof(RoofGrid), "SetRoof"), prefix: new HarmonyMethod(typeof(RoofHealth), nameof(BeforeSet)), postfix: new HarmonyMethod(typeof(RoofHealth), nameof(AfterSet)));
            Type ce = AccessTools.TypeByName("CombatExtended.ProjectileCE");
            harmony.Patch(AccessTools.Method(typeof(Projectile), "ImpactSomething"), transpiler: new HarmonyMethod(typeof(RoofHealth), nameof(OverheadRoof)));
            if (ce != null)
            {
                ceDamageInfo = AccessTools.Property(ce, "DamageInfo");
                harmony.Patch(AccessTools.Method(ce, "ImpactSomething"), transpiler: new HarmonyMethod(typeof(RoofHealth), nameof(OverheadRoof)));
                harmony.Patch(AccessTools.Method(ce, "TryCollideWithRoof"), prefix: new HarmonyMethod(typeof(RoofHealth), nameof(BeforeCeRoof)), postfix: new HarmonyMethod(typeof(RoofHealth), nameof(AfterCeRoof)));
            }
            int count = 0;
            foreach (Type type in GenTypes.AllTypes.Where(t => typeof(DamageWorker).IsAssignableFrom(t)))
            {
                var method = AccessTools.DeclaredMethod(type, "ExplosionAffectCell");
                if (method == null || method.IsAbstract) continue;
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(RoofHealth), nameof(Blast)));
                count++;
            }
            Log.Message("[FpsBridge] ROOF_HEALTH READY; native blast workers=" + count + "; built=100, thin=250, thick=1500");
        }
    }
}
