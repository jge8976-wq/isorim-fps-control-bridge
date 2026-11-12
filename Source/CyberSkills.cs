using System;
using System.Collections.Generic;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace HSK.FpsBridge
{
    // Two skills of the possessed pawn, on a fixed HUD row with independent native key bindings (2026-10-23).
    //  - Sandevistan (Intellectual and Melee both 10+): the world runs at 0.1 times its speed while the pawn moves,
    //    climbs, swings and fires at its own normal pace. Projectiles (its own rounds too) fly and fall at the world's
    //    pace, so they are seen crawling to their targets. It burns stamina as running does; it ends when stamina runs
    //    out or the key is pressed again; then 3 minutes (game time, so faster game speeds shorten it) to recover.
    //  - Death Eye (Shooting 10+): for 20 seconds every round goes for the head of the hostile in sight nearest the
    //    crosshair (a neutral one, e.g. a wild animal, when no hostile is in sight), steered onto it in flight; single-shot
    //    guns fire while LMB is held; shots come at half the interval; holding RMB also pulls the view onto that head.
    //    Then 3 minutes (game time) to recover.
    // The world's slowing is the game's own tick rate (TickManager.TickRateMultiplier); everything that paces the pawn
    // by game time multiplies back by PlayerTime.
    internal static class CyberSkills
    {
        public const int MinSkill = 10;
        public const float Slow = .1f, EyeSeconds = 20f, EyeFireFactor = .5f;
        public const int SandevistanCooldown = 3 * 60 * 60, EyeCooldown = 3 * 60 * 60;   // ticks (3 min at 1x)
        public enum Kind { Sandevistan, DeathEye }

        private static Pawn slowedBy, eyeBy;
        private static float eyeLeft, lastFrame;
        private static readonly Dictionary<int, int> sandevistanReady = new Dictionary<int, int>(), eyeReady = new Dictionary<int, int>();
        private static Pawn locked;

        public static bool Slowed => slowedBy != null;
        internal static bool AnyActive => slowedBy != null || eyeBy != null;
        // How much faster than the (slowed) world the possessed pawn runs.
        public static float PlayerTime => Slowed ? 1f / Slow : 1f;
        public static bool Eye(Pawn p) => eyeBy != null && eyeBy == p;
        // Multiplier on the possessed pawn's shot and recovery intervals (in game ticks).
        public static float FireFactor(Pawn p) => (Slowed && p == slowedBy ? Slow : 1f) * (Eye(p) ? EyeFireFactor : 1f);
        // Multiplier on its melee recovery (ticks): the slowed world only.
        public static float RecoverFactor(Pawn p) => Slowed && p == slowedBy ? Slow : 1f;

        private static int Level(Pawn p, SkillDef s) => p?.skills?.GetSkill(s) is SkillRecord r && !r.TotallyDisabled ? r.Level : -1;
        public static bool Has(Pawn p, Kind k) => k == Kind.Sandevistan
            ? Level(p, SkillDefOf.Intellectual) >= MinSkill && Level(p, SkillDefOf.Melee) >= MinSkill
            : Level(p, SkillDefOf.Shooting) >= MinSkill;

        public static string Label(Kind k) => k == Kind.Sandevistan ? L.T("斯安威斯坦", "Sandevistan") : L.T("死神之眼", "Death Eye");
        public static bool Active(Pawn p, Kind k) => k == Kind.Sandevistan ? Slowed && slowedBy == p : Eye(p);

        public static int CooldownLeft(Pawn p, Kind k)
        {
            var table = k == Kind.Sandevistan ? sandevistanReady : eyeReady;
            return p != null && table.TryGetValue(p.thingIDNumber, out int at) ? Math.Max(0, at - Find.TickManager.TicksGame) : 0;
        }

        public static void Install(Harmony harmony)
        {
            var rate = AccessTools.PropertyGetter(typeof(TickManager), nameof(TickManager.TickRateMultiplier));
            if (rate == null) throw new MissingMethodException("TickManager.TickRateMultiplier");
            harmony.Patch(rate, postfix: new HarmonyMethod(typeof(CyberSkills), nameof(AfterTickRate)));
        }

        public static void AfterTickRate(ref float __result) { if (slowedBy != null) __result *= Slow; }

        // ---- use (from the ability bar)

        public static void Use(Pawn pawn, Kind k)
        {
            if (!Has(pawn, k)) return;
            if (k == Kind.Sandevistan)
            {
                if (Active(pawn, k)) { EndSlow(L.T("斯安威斯坦 · 已关闭", "Sandevistan · off")); return; }
                if (Refuse(pawn, k)) return;
                if (Stances.For(pawn) && Stances.State.Exhausted) { Messages.Message(L.T("太累了，用不了斯安威斯坦。", "Too tired for the Sandevistan."), MessageTypeDefOf.RejectInput, false); return; }
                slowedBy = pawn;
                CombatMusicManager.AbilityActivated();
                Play("Psycast_Skip_Pulse", pawn);
                Messages.Message(L.T("斯安威斯坦 · 时间放慢", "Sandevistan · time slows"), MessageTypeDefOf.SilentInput, false);
                Log.Message("[FpsBridge] SANDEVISTAN on pawn=" + pawn.thingIDNumber);
                return;
            }
            if (Active(pawn, k)) return;
            if (Refuse(pawn, k)) return;
            eyeBy = pawn; eyeLeft = EyeSeconds;
            CombatMusicManager.AbilityActivated();
            eyeReady[pawn.thingIDNumber] = Find.TickManager.TicksGame + EyeCooldown;
            Play("Psycast_Skip_Entry", pawn);
            Messages.Message(L.T("死神之眼 · 子弹追踪头部，按住左键连射", "Death Eye · rounds seek heads; hold LMB to keep firing"), MessageTypeDefOf.SilentInput, false);
            Log.Message("[FpsBridge] DEATH_EYE on pawn=" + pawn.thingIDNumber);
        }

        private static void Play(string sound, Pawn pawn) => FpsSound.Local(DefDatabase<SoundDef>.GetNamedSilentFail(sound), pawn);

        private static bool Refuse(Pawn pawn, Kind k)
        {
            int left = CooldownLeft(pawn, k);
            if (left <= 0) return false;
            Messages.Message(Label(k) + L.T(" 冷却中 ", " recharging ") + left.ToStringSecondsFromTicks(), MessageTypeDefOf.RejectInput, false);
            return true;
        }

        private static void EndSlow(string why)
        {
            if (slowedBy == null) return;
            sandevistanReady[slowedBy.thingIDNumber] = Find.TickManager.TicksGame + SandevistanCooldown;
            Log.Message("[FpsBridge] SANDEVISTAN off pawn=" + slowedBy.thingIDNumber);
            slowedBy = null;
            CombatMusicManager.SkillsEnded();
            if (why != null) Messages.Message(why, MessageTypeDefOf.SilentInput, false);
        }

        // Session over (left, saved, game closing): the world back to its own speed.
        public static void End() { EndSlow(null); eyeBy = null; locked = null; CombatMusicManager.SkillsEnded(); }

        // ---- once per frame while possessing

        public static void Frame(Pawn pawn, bool paused)
        {
            float now = Time.unscaledTime, real = Mathf.Clamp(now - lastFrame, 0f, .1f);
            lastFrame = now;
            if (slowedBy != null && slowedBy != pawn) EndSlow(null);
            if (eyeBy != null && eyeBy != pawn) { eyeBy = null; locked = null; }
            if (paused || Find.TickManager == null) return;
            // The pawn's own time this frame: real time at the game's speed, before the slowing.
            float own = real * Find.TickManager.TickRateMultiplier * PlayerTime;
            if (slowedBy == pawn && Stances.For(pawn) && !Stances.State.Drain(StanceState.SprintCost, own))
                EndSlow(L.T("体力耗尽，斯安威斯坦关闭。", "Out of stamina: Sandevistan off."));
            if (eyeBy == pawn) { eyeLeft -= own; if (eyeLeft <= 0f) { eyeBy = null; locked = null; CombatMusicManager.SkillsEnded(); } }
        }

        // ---- Death Eye: the view onto a head while RMB is held (after the mouse has turned it this frame)

        public static void AimAssist(Pawn pawn, bool paused)
        {
            Verb verb=pawn.equipment?.PrimaryEq?.PrimaryVerb;
            if (!Eye(pawn) || paused || verb==null || !(Ballistics.IsRound(verb) || CeCompat.ProjectileFor(verb)!=null && !CeCompat.IsExplosive(verb)))
            { locked = null; return; }
            locked = Target(pawn, verb, out Vector3 head);   // shown with a mark; rounds go for it whether or not RMB is held
            if (locked == null || !ControlKeys.Held(ControlAction.Aim)) return;
            Vector3 d = head - ControlCamera.RenderEye();
            float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, pitch = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
            float k = 1f - Mathf.Exp(-22f * Mathf.Min(Time.deltaTime, .05f));   // a fast pull, not a teleport
            FirstPerson.Look(Mathf.DeltaAngle(FirstPerson.Yaw, yaw) * k, Mathf.DeltaAngle(FirstPerson.Pitch, pitch) * k);
            Bridge.SyncView();
        }

        // The Death Eye's target: the hostile in sight nearest the crosshair, else the nearest neutral in sight, within
        // the gun's reach; rechecked a few times a second (sight traces cost frame time on a crowded map).
        private static Pawn target;
        private static Pawn targetOwner;
        private static Verb targetVerb;
        private static int targetFrame = -100;
        public static Pawn Target(Pawn pawn, Verb verb, out Vector3 head)
        {
            if (pawn==targetOwner&&verb==targetVerb&&Time.frameCount - targetFrame < 6 && target != null && target.Spawned && !target.Dead && target.Map==pawn.Map) { head = HeadOf(target); return target; }
            targetOwner=pawn;targetVerb=verb;
            targetFrame = Time.frameCount;
            target = null; head = Vector3.zero;
            if (pawn?.Map == null) return null;
            V3 eye = Ballistics.Eye(pawn);
            Vector3 e = new Vector3(eye.X, eye.Y, eye.Z);
            Vector3 view = Quaternion.Euler(FirstPerson.Pitch, FirstPerson.Yaw, 0f) * Vector3.forward;
            float range = 2f * Mathf.Max(10f, verb?.EffectiveRange ?? 40f);   // twice the weapon's range
            float bestScore = float.MaxValue; bool bestHostile = false;
            foreach (Pawn p in pawn.Map.mapPawns.AllPawnsSpawned)
            {
                if (p == pawn || p.Dead || p.Position.Fogged(p.Map)) continue;
                bool hostile = p.HostileTo(pawn);
                bool neutral = !hostile && p.Faction == null && !p.IsPrisonerOfColony && p.HostFaction == null;
                if (!hostile && !neutral) continue;
                if (bestHostile && !hostile) continue;
                Vector3 h = HeadOf(p);
                float dist = (h - e).magnitude;
                if (dist > range || !IffMarkers.Project(h, out _, out _)) continue;   // behind the view or out of reach
                float score = hostile ? Vector3.Angle(view, (h - e).normalized) : dist;   // hostiles: nearest the crosshair; neutrals: nearest
                if (!((hostile && !bestHostile) || score < bestScore)) continue;
                if (Ballistics.SightContact(pawn, eye, new V3(h.x, h.y, h.z), out V3 hit, false) && (new Vector3(hit.X, hit.Y, hit.Z) - h).magnitude > .5f) continue;
                target = p; head = h; bestScore = score; bestHostile = hostile;
            }
            return target;
        }

        // The head: the centre of the head volume (BodyRegions), else near the top of the body.
        private static readonly List<HitVolume> volumes = new List<HitVolume>();
        public static Vector3 HeadOf(Pawn p)
        {
            volumes.Clear();
            BodyRegions.AddVolumes(p, volumes);
            foreach (HitVolume v in volumes)
                if (v.Region == HitRegion.Head)
                {
                    V3 c = v.Kind == VolumeKind.Capsule ? V3.Lerp(v.A, v.B, .5f) : v.A;
                    return new Vector3(c.X, c.Y, c.Z);
                }
            V3 s = FloorSpace.Spot(p);
            return new Vector3(s.X, s.Y + Mathf.Max(.3f, Bindings.PickHeight(p) - .12f), s.Z);
        }

        internal static BodyPartRecord HeadPart(Pawn pawn)
        {
            BodyPartRecord fallback=null;
            foreach(BodyPartRecord part in pawn.health.hediffSet.GetNotMissingParts())
            {
                if(part.def==BodyPartDefOf.Head)return part;
                if(part.def.defName.EndsWith("Head",StringComparison.OrdinalIgnoreCase))fallback=part;
            }
            return fallback;
        }

        // ---- HUD: a cyan edge while the world is slowed, a red one with a lock mark for the Death Eye.

        public static void DrawHud(Pawn pawn)
        {
            float w = UI.screenWidth, h = UI.screenHeight;
            if (Slowed && slowedBy == pawn) Edge(w, h, new Color(.2f, .95f, 1f), L.T("斯安威斯坦 · 再按一次关闭", "Sandevistan · press again to stop"), 0f);
            if (Eye(pawn))
            {
                Edge(w, h, new Color(1f, .15f, .12f), L.T("死神之眼 ", "Death Eye ") + Mathf.Max(0f, eyeLeft).ToString("0.0") + "s", 22f);
                if (locked != null && IffMarkers.Project(HeadOf(locked), out Vector2 s, out _))
                {
                    Color c0 = GUI.color;
                    GUI.color = new Color(1f, .2f, .15f, .95f);
                    for (int i = 0; i < 2; i++) Widgets.DrawBox(new Rect(s.x - 9f - 3f * i, s.y - 9f - 3f * i, 18f + 6f * i, 18f + 6f * i));
                    GUI.color = c0;
                }
            }
        }

        private static void Edge(float w, float h, Color c, string label, float offset)
        {
            float pulse = .55f + .25f * Mathf.Sin(Time.unscaledTime * 4f);
            for (int i = 0; i < 6; i++)
            {
                float t = 6f + i * 7f, a = c.a * .16f * pulse * (1f - i / 6f);
                Color band = new Color(c.r, c.g, c.b, a);
                Widgets.DrawBoxSolid(new Rect(0, i * t * .6f, w, t), band);
                Widgets.DrawBoxSolid(new Rect(0, h - t - i * t * .6f, w, t), band);
            }
            GameFont f0 = Text.Font; TextAnchor a0 = Text.Anchor; Color c0 = GUI.color;
            Text.Font = GameFont.Small; Text.Anchor = TextAnchor.UpperCenter; GUI.color = new Color(c.r, c.g, c.b, .95f);
            Widgets.Label(new Rect(w * .5f - 200f, 64f + offset, 400f, 22f), label);
            Text.Font = f0; Text.Anchor = a0; GUI.color = c0;
        }
    }
}
