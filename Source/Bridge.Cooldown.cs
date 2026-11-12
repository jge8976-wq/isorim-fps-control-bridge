using Verse;

namespace HSK.FpsBridge
{
    internal static partial class Bridge
    {
        // Patch the duration at construction, NOT AdjustedCooldownTicks: our heat cache and custom single-shot
        // scheduler need the unscaled base value. Native melee and our grenade/heavy-slash stances pass here once.
        public static void BeforeCooldown(ref int __0, Verb __2)
        {
            if (CeCompat.Owns(__2)) { CeCompat.AdjustCooldown(__2, ref __0); return; }
            if (SpecialWeapons.AdjustCooldown(__2, ref __0)) return;
            Pawn controlled = ShootingPawn;
            // Vanilla also uses this stance BETWEEN burst bullets; leave those intervals alone.
            if (controlled == null || __2 == null || __2.Bursting) return;
            __0 = CooldownRules.ForActor(__0, FpsMod.Settings.cooldownMultiplier, controlled, __2.CasterPawn,
                __2.EquipmentSource != null || __2.verbProps?.IsMeleeAttack == true);
            // Sandevistan / Death Eye: the possessed pawn recovers at its own pace (grenades, game-run casts).
            if (__2.CasterPawn == controlled) __0 = System.Math.Max(1, UnityEngine.Mathf.RoundToInt(__0 * CyberSkills.FireFactor(controlled)));
        }
    }
}
