using System;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class MountedJumpRules
    {
        public const float MaxHold = 1.2f;
        public static float Charge(float seconds) => Math.Clamp(seconds / MaxHold, 0f, 1f);
        public static float Distance(float speed, float charge) => Math.Clamp(Math.Max(2f, speed) * (.6f + .35f * Math.Clamp(charge, 0, 1)) * (1f + .6f * Math.Clamp(charge, 0, 1)), 1.5f, 10f);
        public static float Radius(float charge) => 3.4f + Math.Clamp(charge, 0, 1) * .8f;
    }
    internal static class MountedTraversal
    {
        internal sealed class Plan { public Vector3 start, end, direction; public float duration, peak, ratio, charge; }
        private static Plan active;
        private static Pawn owner, mount;
        private static object avatar;
        private static Map flightMap;
        private static Vector3 lastSafe;
        private static float elapsed, hold = -1f, cooldown;
        private static bool heldLatch;
        public static bool Flying => active != null;
        public static float Lift => active == null ? 0f : Mathf.Sin(Mathf.Clamp01(elapsed / active.duration) * Mathf.PI) * active.peak;
        public static void Reset()
        {
            if (active != null && owner?.Spawned == true && owner.Map == flightMap && Current.Game != null && Current.ProgramState == ProgramState.Playing)
                Place(owner, avatar, lastSafe);
            active = null; owner = mount = null; avatar = null; flightMap = null; elapsed = 0; hold = -1; heldLatch = false; cooldown = 0;
        }
        private static void Place(Pawn p, object av, Vector3 point)
        {
            p.pather?.StopDead(); IntVec3 cell = point.ToIntVec3();
            if (p.Position != cell) { p.Position = cell; p.Drawer?.tweener.ResetTweenedPosToRoot(); }
            point.y = p.def.Altitude;
            if (av != null) Bindings.PhysicsPosition.SetValue(av, (Vector3?)point);
        }
        private static bool LowCover(Thing t)
        {
            if (t is Building_Door || t.def.building?.isNaturalRock == true) return false;
            if (t.def.category == ThingCategory.Item) return Ballistics.IsChunk(t.def);
            return t is Building && t.def.fillPercent >= .1f && t.def.fillPercent < 1f && Bindings.PickHeight(t) <= 1.05f;
        }
        private static bool Cell(Pawn p, IntVec3 c, bool landing, out bool cover)
        {
            cover = false;
            Map map = p.Map;
            if (!c.InBounds(map) || c.Fogged(map) || FloorSpace.Band(map, c).Level != FloorSpace.Band(map, p.Position).Level
                || FloorSpace.ProjectZReady && FloorSpace.Band(map, c).Level > 0 && !FloorSpace.HasFloorAt(map, c)) return false;
            foreach (Thing t in c.GetThingList(map))
            {
                if (t == p || t == mount || t is Pawn) continue;
                if (LowCover(t)) { cover = true; if (landing) return false; continue; }
                if (t.def.passability == Traversability.Impassable || t is Plant && t.def.plant?.IsTree == true || t is Building_Door door && !door.Open) return false;
            }
            return !landing || c.Standable(map);
        }
        internal static bool TryPlan(Pawn p, Vector3 start, Vector3 direction, float speed, float baseSpeed, float charge, bool automatic, out Plan plan)
        {
            plan = null; direction.y = 0;
            if (p?.Map == null || direction.sqrMagnitude < .01f) return false;
            direction.Normalize();
            float distance = MountedJumpRules.Distance(speed, charge);
            bool crossed = false; float lastObstacle = 0;
            for (float d = .15f; d <= distance + .05f; d += .15f)
            {
                if (!Cell(p, (start + direction * d).ToIntVec3(), false, out bool cover)) return false;
                if (cover) { crossed = true; lastObstacle = d; }
                if (automatic && d <= 1.1f && crossed) distance = Mathf.Max(1.5f, lastObstacle + 1.15f);
            }
            if (automatic && (!crossed || lastObstacle > 2.6f)) return false;
            Vector3 end = start + direction * distance;
            if (!Cell(p, end.ToIntVec3(), true, out _)) return false;
            float duration = distance / Mathf.Max(2f, speed * (1f + .6f * charge));
            plan = new Plan { start = start, end = end, direction = direction, duration = Mathf.Max(.18f, duration), peak = 1.15f + .45f * charge,
                ratio = Mathf.Clamp(speed / Mathf.Max(1f, baseSpeed), .7f, 2.4f), charge = charge };
            return true;
        }
        public static void Frame(Pawn p, Pawn animal, object av, float dt, bool input, float speed, float baseSpeed)
        {
            mount = animal;
            cooldown = Mathf.Max(0, cooldown - dt);
            if (active != null)
            {
                if (owner != p || !p.Spawned || !animal.Spawned || p.Map != animal.Map || p.Map != flightMap) { Reset(); return; }
                if (dt <= 0 || !input) return;
                elapsed = Mathf.Min(active.duration, elapsed + dt);
                Vector3 next = Vector3.Lerp(active.start, active.end, elapsed / active.duration);
                if (!Cell(p, next.ToIntVec3(), elapsed >= active.duration, out bool cover)) { Reset(); return; }
                if (!cover && Cell(p, next.ToIntVec3(), true, out _)) lastSafe = next;
                Place(p, av, next);
                if (elapsed >= active.duration)
                {
                    Plan landed = active; active = null; cooldown = .35f;
                    MountedControl.Landed(p, landed.ratio, landed.charge, landed.direction);
                }
                return;
            }
            if (!input) { hold = -1; heldLatch = false; return; }
            bool space = ControlKeys.Held(ControlAction.Jump);
            if (!space) heldLatch = false;
            if (ControlKeys.Down(ControlAction.Jump) && !heldLatch && cooldown <= 0) { hold = 0; heldLatch = true; }
            if (hold >= 0 && space) hold = Mathf.Min(MountedJumpRules.MaxHold, hold + dt);
            bool release = hold >= 0 && !space;
            bool automatic = hold < 0 && ControlKeys.Held(ControlAction.Sprint)
                && (ControlKeys.Held(ControlAction.Forward) || ControlKeys.Held(ControlAction.Backward) || ControlKeys.Held(ControlAction.Left) || ControlKeys.Held(ControlAction.Right));
            if (cooldown > 0 || !release && !automatic) return;
            float charge = release ? MountedJumpRules.Charge(hold) : 0;
            Vector3 start = (Vector3?)Bindings.PhysicsPosition.GetValue(av) ?? p.DrawPos;
            Vector3 dir = automatic ? (Vector3)Bindings.MoveField.GetValue(av) : Quaternion.Euler(0, ControlCamera.AttackYaw(p), 0) * Vector3.forward;
            if (release) hold = -1;
            if (!TryPlan(p, start, dir, Mathf.Max(speed, baseSpeed), baseSpeed, charge, automatic, out Plan plan)) return;
            active = plan; owner = p; avatar = av; flightMap = p.Map; lastSafe = plan.start; elapsed = 0;
        }
        public static void Preview(Pawn p, object av, float speed, float baseSpeed, bool melee)
        {
            if (!ControlKeys.Held(ControlAction.Sprint) && hold < 0 && active == null) return;
            Plan plan = active;
            bool valid = plan != null || TryPlan(p, (Vector3?)Bindings.PhysicsPosition.GetValue(av) ?? p.DrawPos,
                Quaternion.Euler(0, ControlCamera.AttackYaw(p), 0) * Vector3.forward, Mathf.Max(speed, baseSpeed), baseSpeed,
                MountedJumpRules.Charge(Mathf.Max(0, hold)), false, out plan);
            if (!valid) return;
            Vector3 at = plan.end + FloorSpace.Offset(p); at.y = FloorSpace.Offset(p).y + .09f;
            Color color = new Color(.4f, .85f, 1f, .8f);
            Visuals.GroundRing(at, .28f, color);
            if (!melee) return;
            float radius = MountedJumpRules.Radius(plan.charge);
            Vector3 right = Vector3.Cross(Vector3.up, plan.direction);
            Vector3 prior = at + (plan.direction * .5f - right * .8660254f) * radius;
            Visuals.Line(at, prior, color);
            for (int i = 1; i <= 24; i++)
            {
                float a = Mathf.Lerp(-60f, 60f, i / 24f) * Mathf.Deg2Rad;
                Vector3 next = at + (plan.direction * Mathf.Cos(a) + right * Mathf.Sin(a)) * radius;
                Visuals.Line(prior, next, color); prior = next;
            }
            Visuals.Line(prior, at, color);
        }
    }
}
