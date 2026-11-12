using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static partial class CeCompat
    {
        // The shared obstacle settings are stored as fractions. In CE mode the
        // same numbers represent mm RHA: .10 is 10 mm, .35 is 35 mm.
        internal const float CeArmorScale = 100f;
        internal static int FpsStructureContacts;
        private static Type structureBulletType;
        private static PropertyInfo structureDamageInfo, structureExtraDamage;
        private static FieldInfo structureSecondaryDamage, structureTraitExplosion;
        private static float nextStructureLog;

        private sealed class StructureFlight
        {
            internal readonly HashSet<Thing> Things = new();
            internal readonly HashSet<IntVec3> Roofs = new();
            internal readonly ObstacleArmor Armor = FpsMod.Settings.ObstacleArmor;
            internal int Credits, Passed;
            internal float Retain = 1f;

            internal StructureFlight(PenetrationGroup group) => Credits = PenetrationRules.Credits(group);

            internal bool Passes(ObstacleClass kind, float sharp, float armor) =>
                PenetrationRules.Affordable(Credits, PenetrationRules.Cost(kind), Passed)
                && PenetrationRules.Passes(sharp, armor);

            internal void Pass(ObstacleClass kind)
            {
                Credits = Math.Max(0, Credits - PenetrationRules.Cost(kind));
                Passed++;
                Retain *= PenetrationRules.ExitRetain;
            }
        }

        private static void InstallStructure()
        {
            const string id = "hsk.fpsbridge.ce-structure";
            var harmony = new Harmony(id);
            try
            {
                structureBulletType = Named("BulletCE") ?? throw new TypeLoadException("CombatExtended.BulletCE");
                structureDamageInfo = Property(projectileType, "DamageInfo", typeof(DamageInfo));
                structureExtraDamage = Property(projectileType, "ExtraDamages");
                structureSecondaryDamage = AccessTools.Field(Named("ProjectilePropertiesCE"), "secondaryDamage");
                structureTraitExplosion = AccessTools.Field(projectileType, "traitExplosion");
                harmony.Patch(Property(projectileType, "PenetrationAmount", typeof(float)).GetGetMethod(),
                    postfix: new HarmonyMethod(typeof(CeCompat), nameof(AfterStructurePenetration)));
                Log.Message("[FpsBridge] CE_STRUCTURE READY; sharp mm RHA, native damage and shared obstacle budgets");
            }
            catch (Exception e)
            {
                harmony.UnpatchAll(id);
                structureBulletType = null;
                Log.Warning("[FpsBridge] CE structural penetration unavailable: " + e.Message);
            }
        }

        private static StructureFlight StructureFor(Thing projectile, string weapon, int burst)
        {
            if (structureBulletType == null || projectile.GetType() != structureBulletType || IsExplosiveDef(projectile.def)
                || projectile.def.projectile.flyOverhead || projectile.def.projectile.damageDef?.armorCategory != DamageArmorCategoryDefOf.Sharp)
                return null;
            // These projectiles need their complete native terminal impact sequence.
            if (structureSecondaryDamage?.GetValue(projectile.def.projectile) is IList secondary && secondary.Count > 0
                || structureTraitExplosion?.GetValue(projectile) != null
                || structureExtraDamage.GetValue(projectile) is IEnumerable extra && extra.Cast<object>().Any()) return null;
            return new StructureFlight(PenetrationRules.Classify(weapon, burst, true));
        }

        public static void AfterStructurePenetration(Thing __instance, ref float __result)
        {
            if (traces.TryGetValue(__instance, out Trace flight) && flight.structure != null)
                __result *= flight.structure.Retain;
        }

        private static bool ContinueStructure(Thing projectile, Trace flight, Thing target, V3 at, CeStructureRoof.Contact? roof)
        {
            StructureFlight structure = flight.structure;
            if (structure == null || target is Pawn || (target == null && roof?.Breakable != true)) return false;
            if (target != null && (!target.def.useHitPoints || !target.def.destroyable)) return false;
            ObstacleClass kind = target == null ? (roof?.Roof?.isNatural==true?ObstacleClass.NaturalRock:ObstacleClass.Wood) : Ballistics.ObstacleOf(target);
            bool reinforced = target?.def.defName is "AncientFortifiedWall" or "OrbitalAncientFortifiedWall" or "AncientBlastDoor";
            if (reinforced) kind = ObstacleClass.NaturalRock;
            else if (kind == ObstacleClass.Barrier && target != null && (target.def.IsWall || target.def.IsDoor))
                kind = ObstacleClass.StoneWall; // Fixed-material walls can have no Stuff.
            if (kind == ObstacleClass.Barrier) return false;
            var damage = (DamageInfo)structureDamageInfo.GetValue(projectile);
            if (damage.Def.armorCategory != DamageArmorCategoryDefOf.Sharp) return false;
            float sharp = damage.ArmorPenetrationInt;
            float armor = structure.Armor.Get(kind) * CeArmorScale * (reinforced ? 2f : 1f);
            bool pass = structure.Passes(kind, sharp, armor);
            if (FpsMod.Settings.penetrationDiagnostics && Time.unscaledTime >= nextStructureLog)
            {
                nextStructureLog = Time.unscaledTime + .25f;
                Log.Message($"[FpsBridge] CE_STRUCTURE projectile={projectile.def.defName}, obstacle={target?.ThingID ?? roof?.Cell.ToString()}, class={kind}, sharp={sharp:F2}, armor={armor:F2}, pass={pass}");
            }
            if (!pass) return false;
            if (target == null)
            {
                CeStructureRoof.Hit(flight.map, roof.Value, damage.Amount);
                structure.Roofs.Add(roof.Value.Cell);
            }
            else
            {
                // CE supplies the real projectile DamageInfo. Apply the shared
                // structural wear once and let the surviving projectile keep flying.
                damage.SetAmount(damage.Amount * CoverWear.Factor(kind));
                target.TakeDamage(damage);
                structure.Things.Add(target);
            }
            Visuals.ImpactLight(flight.map, at, target);
            structure.Pass(kind);
            FpsStructureContacts++;
            return true;
        }
    }
}
