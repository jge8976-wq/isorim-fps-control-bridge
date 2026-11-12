using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace HSK.FpsBridge
{
    // 0.9.0 hit feedback and first-person experience. Every hit the possessed pawn lands (rounds, penetration, blades)
    // flashes a cross on the crosshair: red with a ring of metal for the head, white for the rest; a pawn hit lately that
    // dies shows a skull. Rewards for doing it by hand: a headshot +150 Shooting; a kill within 3 cells +200 Shooting or
    // Melee (by the weapon that landed the last hit).
    internal static class HitMarker
    {
        private const float CrossTime = .25f, SkullTime = .8f, KillWindow = 1.5f, CloseKill = 3f;
        private const float HeadshotXp = 150f, CloseKillXp = 200f;
        private sealed class Recent { public Pawn victim; public float time; public bool melee; public float distance; }
        private static readonly List<Recent> recent = new List<Recent>();
        private static float crossAt = -10f, skullAt = -10f;
        private static bool crossHead;
        private static SoundDef headSound;
        private static bool soundLooked;

        public static void Reset() { recent.Clear(); crossAt = skullAt = -10f; }

        public static void Hit(Pawn victim, HitRegion region, bool melee)
        {
            Pawn self = Bridge.ShootingPawn;
            if (self == null || victim == null || victim == self) return;
            bool head = region == HitRegion.Head;
            crossAt = Time.unscaledTime; crossHead = head;
            if (head)
            {
                if (!soundLooked) { soundLooked = true; headSound = DefDatabase<SoundDef>.GetNamedSilentFail("BulletImpact_Metal"); }
                FpsSound.Local(headSound, self);
                if (!melee) self.skills?.Learn(SkillDefOf.Shooting, HeadshotXp);
            }
            float distance = (victim.Position - self.Position).LengthHorizontal;
            foreach (Recent r in recent) if (r.victim == victim) { r.time = Time.unscaledTime; r.melee = melee; r.distance = distance; return; }
            recent.Add(new Recent { victim = victim, time = Time.unscaledTime, melee = melee, distance = distance });
        }

        // Once per GUI repaint while possessed: kills among the pawns hit lately, then the marks.
        public static void Draw()
        {
            Pawn self = Bridge.ShootingPawn;
            float now = Time.unscaledTime;
            for (int i = recent.Count - 1; i >= 0; i--)
            {
                Recent r = recent[i];
                if (r.victim.Dead)
                {
                    skullAt = now;
                    if (self != null && r.distance <= CloseKill) self.skills?.Learn(r.melee ? SkillDefOf.Melee : SkillDefOf.Shooting, CloseKillXp);
                    recent.RemoveAt(i);
                }
                else if (now - r.time > KillWindow || r.victim.Destroyed) recent.RemoveAt(i);
            }
            float x = UI.screenWidth * .5f, y = UI.screenHeight * .5f;
            float tc = now - crossAt;
            if (tc >= 0f && tc < CrossTime)
            {
                float a = 1f - tc / CrossTime;
                Color c = crossHead ? new Color(1f, .2f, .15f, a) : new Color(1f, 1f, 1f, a);
                float inner = 6f, outer = crossHead ? 15f : 12f, w = crossHead ? 2.5f : 2f;
                Widgets.DrawLine(new Vector2(x - outer, y - outer), new Vector2(x - inner, y - inner), c, w);
                Widgets.DrawLine(new Vector2(x + outer, y - outer), new Vector2(x + inner, y - inner), c, w);
                Widgets.DrawLine(new Vector2(x - outer, y + outer), new Vector2(x - inner, y + inner), c, w);
                Widgets.DrawLine(new Vector2(x + outer, y + outer), new Vector2(x + inner, y + inner), c, w);
            }
            float ts = now - skullAt;
            if (ts >= 0f && ts < SkullTime) Skull(x, y - 34f, 1f - ts / SkullTime);
        }

        // A small skull from blocks: cranium, two eye holes, a nose, a jaw with teeth.
        private static void Skull(float cx, float cy, float a)
        {
            Color bone = new Color(1f, .95f, .85f, a), dark = new Color(0f, 0f, 0f, a * .9f);
            Widgets.DrawBoxSolid(new Rect(cx - 9f, cy - 10f, 18f, 13f), bone);
            Widgets.DrawBoxSolid(new Rect(cx - 7f, cy - 12f, 14f, 3f), bone);
            Widgets.DrawBoxSolid(new Rect(cx - 6f, cy + 3f, 12f, 5f), bone);
            Widgets.DrawBoxSolid(new Rect(cx - 6f, cy - 5f, 4f, 4f), dark);
            Widgets.DrawBoxSolid(new Rect(cx + 2f, cy - 5f, 4f, 4f), dark);
            Widgets.DrawBoxSolid(new Rect(cx - 1f, cy + 0f, 2f, 2f), dark);
            for (int i = 0; i < 3; i++) Widgets.DrawBoxSolid(new Rect(cx - 4.5f + i * 3.5f, cy + 4f, 1f, 4f), dark);
        }
    }
}
