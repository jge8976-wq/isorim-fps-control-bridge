using System;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // The shooter's hands, not the weapon: low Shooting skill sways the view more and kicks harder per shot; high skill
    // holds steadier and recovers. Rounds always leave exactly along the view (Ballistics), so this is the only
    // inaccuracy. Applied as an additive view offset: the part of the offset added last frame is replaced each frame,
    // so the player's own mouse aim is never lost; 30% of each kick stays (the player pulls it down), 70% recovers.
    internal static class AimMotion
    {
        private static float appliedYaw, appliedPitch;     // offset currently added to the view
        private static float recoilYaw, recoilPitch;        // recovering part of the kicks (pitch negative = up)
        private static float phase;
        private static int level = -1, levelTick = -1;
        // Iron sights steady the hands (set each frame by the bridge from the weapon's aim blend).
        public static float SwayScale = 1f;

        public static void Reset()
        {
            appliedYaw = appliedPitch = recoilYaw = recoilPitch = 0f;
            SwayScale = 1f;
            level = -1; levelTick = -1;
            phase = UnityEngine.Random.Range(0f, 100f);
        }

        private static int Level(Pawn pawn)
        {
            int now = Find.TickManager?.TicksGame ?? 0;
            if (level >= 0 && now - levelTick < 250) return level;
            levelTick = now;
            SkillRecord skill = pawn.skills?.GetSkill(SkillDefOf.Shooting);
            level = skill == null || skill.TotallyDisabled ? 0 : skill.Level;
            return level;
        }

        // Once per frame while the view is captured (also while paused: breathing goes on, recoil recovery waits).
        public static void Frame(Pawn pawn, bool moving, bool paused)
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            if (!paused)
            {
                float k = Mathf.Exp(-6f * dt);
                recoilYaw *= k; recoilPitch *= k;
            }
            float t = Time.unscaledTime;
            // Full sway with the weapon raised (drafted); only a light breathing sway when just walking about.
            float sway = Mathf.Lerp(AimSkill.SwayDegrees(10, moving), AimSkill.SwayDegrees(Level(pawn), moving), FpsMod.Settings.shootingSkillInfluence) * SwayScale;
            float wantYaw = AimSkill.Wander(t, phase) * sway + recoilYaw;
            float wantPitch = AimSkill.Wander(t * 0.83f, phase + 11f) * sway * 0.7f + recoilPitch;
            float before = FirstPerson.Pitch;
            FirstPerson.Look(wantYaw - appliedYaw, wantPitch - appliedPitch);
            appliedYaw = wantYaw;
            // Pitch is clamped by IsoRim at the limits; only what actually moved counts as applied.
            appliedPitch += FirstPerson.Pitch - before;
        }

        public static void Kick(Pawn pawn, float damage)
        {
            float kick = Mathf.Lerp(AimSkill.KickDegrees(10, damage), AimSkill.KickDegrees(Level(pawn), damage), FpsMod.Settings.shootingSkillInfluence);
            if (WeaponView.SteadySingleAim) kick *= .75f;
            float side = UnityEngine.Random.Range(-0.35f, 0.35f) * kick;
            // 30% stays in the view (as if the muzzle climbed), 70% recovers on its own.
            FirstPerson.Look(side * 0.3f, -kick * 0.3f);
            recoilPitch += -kick * 0.7f;
            recoilYaw += side * 0.7f;
            Bridge.SyncView();
        }

        // Leaving: take the temporary offset back out so the next session starts from the true view.
        public static void Release()
        {
            if (FirstPerson.On && (appliedYaw != 0f || appliedPitch != 0f)) FirstPerson.Look(-appliedYaw, -appliedPitch);
            Reset();
        }
    }
}
