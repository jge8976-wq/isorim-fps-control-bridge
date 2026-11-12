using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace HSK.FpsBridge
{
    // 0.9.0 marking a target: the middle button on a hostile under the crosshair marks it (again on it, or on nothing:
    // unmarked). Own turrets that can hit it shoot it first; drafted colonists and mechs within 40 cells are ordered to
    // attack it, each once. A red diamond floats over it; the mark goes when it dies or leaves.
    internal static class TargetMark
    {
        private const float SquadRange = 40f;
        private static Pawn marked;
        private static readonly HashSet<int> ordered = new HashSet<int>();
        private static int nextOrders;

        public static Pawn Target => marked != null && marked.Spawned && !marked.Dead ? marked : null;

        public static void Reset() { marked = null; ordered.Clear(); }

        public static void Toggle(Pawn self)
        {
            V3 eye = Ballistics.Eye(self), dir = ControlCamera.ShotDirection(self);
            Thing hit = Ballistics.PickTarget(self, eye, eye + dir * 80f).Thing;
            if (!(hit is Pawn p) || p.Dead || !p.HostileTo(Faction.OfPlayer) || p == marked)
            {
                if (marked != null) Messages.Message(L.T("已取消标记", "Mark cleared"), MessageTypeDefOf.SilentInput, false);
                Reset();
                return;
            }
            marked = p; ordered.Clear(); nextOrders = 0;
            Messages.Message(L.T("标记目标：", "Target marked: ") + p.LabelShortCap, MessageTypeDefOf.SilentInput, false);
            Log.Message("[FpsBridge] MARK target=" + p.ThingID);
        }

        // Once per input frame: drop a gone target; order the drafted squad (a few times a second).
        public static void Frame(Pawn self)
        {
            if (marked != null && Target == null) { Reset(); return; }
            Pawn t = Target;
            if (t == null || self?.Map == null || t.Map != self.Map || Time.frameCount < nextOrders) return;
            nextOrders = Time.frameCount + 20;
            foreach (Pawn p in self.Map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer))
            {
                if (p == self || !p.Drafted || p.Downed || p.InMentalState || ordered.Contains(p.thingIDNumber)) continue;
                if ((p.Position - self.Position).LengthHorizontal > SquadRange) continue;
                if (p.CurJob != null && p.CurJob.targetA.Thing == t) { ordered.Add(p.thingIDNumber); continue; }
                Verb verb = p.equipment?.PrimaryEq?.PrimaryVerb;
                bool ranged = verb != null && !verb.verbProps.IsMeleeAttack && !p.WorkTagIsDisabled(WorkTags.Shooting);
                if (p.WorkTagIsDisabled(WorkTags.Violent)) { ordered.Add(p.thingIDNumber); continue; }
                Job job = JobMaker.MakeJob(ranged ? JobDefOf.AttackStatic : JobDefOf.AttackMelee, t);
                job.playerForced = true;
                p.jobs.TryTakeOrderedJob(job);
                ordered.Add(p.thingIDNumber);
            }
        }

        // Turrets: the mark first when the turret can hit it from where it stands.
        public static bool ForTurret(Building_TurretGun turret, out LocalTargetInfo target)
        {
            target = LocalTargetInfo.Invalid;
            Pawn t = Target;
            if (t == null || turret.Faction != Faction.OfPlayer || t.Map != turret.Map) return false;
            Verb verb = turret.AttackVerb;
            if (verb == null || !verb.CanHitTarget(t)) return false;
            float d = (t.Position - turret.Position).LengthHorizontal;
            if (d > verb.EffectiveRange || d < verb.verbProps.minRange) return false;
            target = t;
            return true;
        }

        public static void Draw()
        {
            Pawn t = Target;
            if (t == null) return;
            V3 spot = FloorSpace.Spot(t);
            if (!IffMarkers.Project(new Vector3(spot.X, spot.Y + Bindings.PickHeight(t) + .62f, spot.Z), out Vector2 s, out _)) return;
            float r = 9f + 2f * Mathf.Sin(Time.unscaledTime * 6f);
            Color c = new Color(1f, .15f, .1f, .95f);
            Widgets.DrawLine(new Vector2(s.x, s.y - r), new Vector2(s.x + r, s.y), c, 2.5f);
            Widgets.DrawLine(new Vector2(s.x + r, s.y), new Vector2(s.x, s.y + r), c, 2.5f);
            Widgets.DrawLine(new Vector2(s.x, s.y + r), new Vector2(s.x - r, s.y), c, 2.5f);
            Widgets.DrawLine(new Vector2(s.x - r, s.y), new Vector2(s.x, s.y - r), c, 2.5f);
        }
    }
}
