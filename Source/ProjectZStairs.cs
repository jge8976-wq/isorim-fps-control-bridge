using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace HSK.FpsBridge
{
    // Reuse the registered stair graph and its traversal rules. No synthetic stairs, pathfinding, or unrestricted teleport.
    internal static class ProjectZStairs
    {
        private static bool looked;
        private static MethodInfo forMap, passable;
        private static FieldInfo links, bottom, top, stairs, kind;
        private static int lastTick = -10;
        private static Pawn lastPawn;
        private static bool Ready()
        {
            if (looked) return forMap != null;
            looked = true;
            Type grid = AccessTools.TypeByName("ProjectZ.StairGrid");
            if (grid == null) return false;
            try
            {
                // ProjectZ up to 16965d2: StairLink with a 'stairs' field. Later: the VLink struct for every vertical link
                // (stairs, ladders, lifts, elevators) with 'thing' and 'kind'; only stairs are stepped here.
                Type link = AccessTools.TypeByName("ProjectZ.StairLink") ?? AccessTools.TypeByName("ProjectZ.VLink");
                links = AccessTools.Field(grid, "links");
                bottom = AccessTools.Field(link, "bottom"); top = AccessTools.Field(link, "top");
                stairs = AccessTools.Field(link, "stairs") ?? AccessTools.Field(link, "thing");
                kind = AccessTools.Field(link, "kind");
                Log.Message("[FpsBridge] PROJECTZ_STAIRS link=" + link?.FullName + "; kinds=" + (kind != null));
                passable = AccessTools.Method(grid, "Passable", new[] { link, typeof(TraverseParms) });
                MethodInfo factory = AccessTools.Method(grid, "For", new[] { typeof(Map) });
                if (links == null || bottom == null || top == null || stairs == null || passable == null || factory == null)
                    throw new MissingMemberException("ProjectZ.StairGrid/StairLink");
                forMap = factory;
            }
            catch (Exception e) { Log.Error("[FpsBridge] Stair API mismatch: " + e); }
            return forMap != null;
        }

        // ---- jumping down (0.7.0): Space next to an upper floor's open edge drops the pawn to the floor below, where
        // ProjectZ's own fall geometry says it lands (no landing on roofs or impassable cells). ProjectZ staggers the
        // landing; a drop of more than one floor also hurts both legs (blunt, more per floor).
        public const float LegDamagePerExtraFloor = 7f;
        private static bool jumpLooked;
        private static MethodInfo landingFrom, jumpGridFor, landed;

        private static bool JumpReady()
        {
            if (jumpLooked) return landingFrom != null;
            jumpLooked = true;
            Type grid = AccessTools.TypeByName("ProjectZ.JumpGrid");
            if (grid == null) return false;
            landingFrom = AccessTools.Method(grid, "LandingFrom", new[] { typeof(IntVec3), typeof(IntVec3), typeof(Map), typeof(IntVec3).MakeByRefType(), typeof(int).MakeByRefType() });
            jumpGridFor = AccessTools.Method(grid, "For", new[] { typeof(Map) });
            landed = AccessTools.Method(grid, "Landed", new[] { typeof(Pawn), typeof(int) });
            if (landingFrom == null || jumpGridFor == null || landed == null) { landingFrom = null; Log.Warning("[FpsBridge] ProjectZ JumpGrid API changed: jumping down is off."); }
            return landingFrom != null;
        }

        // direction: where the player is heading (movement input, else the view), world x/z.
        public static bool TryJumpDown(Pawn pawn, object avatar, Vector3 direction, out float drop)
        {
            drop = 0f;
            if (!FloorSpace.ProjectZReady || !JumpReady() || pawn?.Map == null || direction.sqrMagnitude < 1e-4f) return false;
            Map map = pawn.Map;
            FloorBand own = FloorSpace.Band(map, pawn.Position);
            if (own.Level < 1) return false;
            float ang = Mathf.Atan2(direction.x, direction.z);
            int dx = Mathf.RoundToInt(Mathf.Sin(ang)), dz = Mathf.RoundToInt(Mathf.Cos(ang));
            foreach (IntVec3 dir in new[] { new IntVec3(dx, 0, dz), new IntVec3(dx, 0, 0), new IntVec3(0, 0, dz) })
            {
                if (dir.x == 0 && dir.z == 0) continue;
                object[] args = { pawn.Position, dir, map, IntVec3.Invalid, 0 };
                if (!(bool)landingFrom.Invoke(null, args)) continue;
                IntVec3 land = (IntVec3)args[3];
                int fallen = (int)args[4];
                if (!land.IsValid || !land.InBounds(map) || fallen < 1) continue;
                FloorBand below = FloorSpace.Band(map, land);
                drop = Math.Max(0f, own.Height - below.Height);
                pawn.pather?.StopDead();
                pawn.Position = land;
                pawn.Notify_Teleported(endCurrentJob: false, resetTweenedPos: true);
                if (pawn.pather != null) pawn.pather.nextCell = land;
                Vector3 logical = land.ToVector3Shifted(); logical.y = pawn.def.Altitude;
                Bindings.PhysicsPosition.SetValue(avatar, (Vector3?)logical);
                Api.Floors?.ViewFloor(map, below.Level);
                AimPicker.Reset();
                object grid = jumpGridFor.Invoke(null, new object[] { map });
                if (grid != null) landed.Invoke(grid, new object[] { pawn, 1 });   // ProjectZ's landing: dust and stagger, never its own hurt
                if (fallen >= 2) HurtLegs(pawn, fallen);
                lastPawn = pawn; lastTick = Find.TickManager.TicksGame;
                Log.Message("[FpsBridge] JUMP_DOWN pawn=" + pawn.thingIDNumber + "; level=" + own.Level + "->" + below.Level + "; floors=" + fallen);
                return true;
            }
            return false;
        }

        internal static void HurtLegs(Pawn pawn, int fallen)
        {
            float amount = LegDamagePerExtraFloor * (fallen - 1);
            var legs = new System.Collections.Generic.List<BodyPartRecord>();
            foreach (BodyPartRecord part in pawn.health.hediffSet.GetNotMissingParts())
            {
                if (part.def.tags == null || !part.def.tags.Contains(RimWorld.BodyPartTagDefOf.MovingLimbCore)) continue;
                bool nested = false;
                for (BodyPartRecord p = part.parent; p != null; p = p.parent) if (p.def.tags != null && p.def.tags.Contains(RimWorld.BodyPartTagDefOf.MovingLimbCore)) nested = true;
                if (!nested) legs.Add(part);
            }
            foreach (BodyPartRecord leg in legs)
            {
                if (pawn.Dead || pawn.health.hediffSet.PartIsMissing(leg)) continue;
                var dinfo = new DamageInfo(RimWorld.DamageDefOf.Blunt, amount, 0f);
                dinfo.SetHitPart(leg);
                pawn.TakeDamage(dinfo);
            }
        }

        // ---- ladders, lifts and elevators (ProjectZ after 16965d2): E at one end orders the pawn to the other end and
        // ProjectZ's own link step does the rest (climb time, one climber per ladder, power, falls). Looking down picks
        // the way down where both are possible (an elevator's middle floor).
        private static string blocked;   // a link beside the pawn that ProjectZ says it cannot use now
        private static IntVec3 rideTo = IntVec3.Invalid;
        private static int rideLevel;
        private static Job rideJob;

        public static bool Riding(Pawn pawn) => rideJob != null && pawn?.CurJob == rideJob;

        private static bool FindRide(Pawn pawn, bool wantDown, out IntVec3 to, out int span, out string kindName)
        {
            to = IntVec3.Invalid; span = int.MaxValue; kindName = null; blocked = null;
            if (!FloorSpace.ProjectZReady || !Ready() || kind == null || pawn?.Map == null) return false;
            object grid = forMap.Invoke(null, new object[] { pawn.Map });
            if (grid == null) return false;
            IntVec3 bestUp = IntVec3.Invalid, bestDown = IntVec3.Invalid; int spanUp = int.MaxValue, spanDown = int.MaxValue;
            string nameUp = null, nameDown = null;
            FieldInfo spanField = AccessTools.Field(stairs.DeclaringType, "span");
            foreach (object link in (IEnumerable)links.GetValue(grid))
            {
                string k = kind.GetValue(link).ToString();
                if (k == "Stairs") continue;
                IntVec3 lo = (IntVec3)bottom.GetValue(link), hi = (IntVec3)top.GetValue(link);
                if (!(stairs.GetValue(link) is Thing thing) || !thing.Spawned) continue;
                // An elevator's stop is the cell in front of its door: on it, next to it (same floor), or on the
                // elevator itself all count; the order walks the pawn onto the stop first.
                bool atLo = Near(pawn, lo, thing), atHi = Near(pawn, hi, thing);
                if (!atLo && !atHi) continue;
                if (!(bool)passable.Invoke(grid, new object[] { link, TraverseParms.For(pawn) })) { blocked = k + " " + lo + "->" + hi; continue; }
                int s = spanField != null ? (int)spanField.GetValue(link) : 1;
                if (atLo && s < spanUp) { bestUp = hi; spanUp = s; nameUp = k; }
                if (atHi && s < spanDown) { bestDown = lo; spanDown = s; nameDown = k; }
            }
            bool down = bestDown.IsValid && (wantDown || !bestUp.IsValid);
            to = down ? bestDown : bestUp; span = down ? spanDown : spanUp; kindName = down ? nameDown : nameUp;
            return to.IsValid;
        }

        // On an end, within one cell of it on the same floor, or on the link's own building cell beside it.
        private static bool Near(Pawn pawn, IntVec3 end, Thing thing)
        {
            Map map = pawn.Map;
            if (!end.InBounds(map) || FloorSpace.Band(map, end).Level != FloorSpace.Band(map, pawn.Position).Level) return false;
            IntVec3 d = pawn.Position - end;
            if (Math.Abs(d.x) <= 1 && Math.Abs(d.z) <= 1) return true;
            // The elevator's own cell on this floor (the shaft over it): the pawn may stand in the car.
            IntVec3 car = thing.Position;
            int level = FloorSpace.Band(map, end).Level, carLevel = FloorSpace.Band(map, car).Level;
            if (level != carLevel) car = new IntVec3(car.x, 0, car.z - FloorSpace.Band(map, car).Shift + FloorSpace.Band(map, end).Shift);
            return pawn.Position == car;
        }

        private static string Label(string k) => k == "Ladder" || k == "ElevatorLadder" ? L.T("梯子", "ladder") : k == "Lift" ? L.T("升降梯", "lift") : L.T("电梯", "elevator");

        // E on a ladder/elevator end. True when E was used here.
        public static bool TryRide(Pawn pawn)
        {
            if (Riding(pawn)) return true;
            bool wantDown = FirstPerson.Pitch > 30f;
            if (!FindRide(pawn, wantDown, out IntVec3 to, out _, out string k))
            {
                if (blocked == null) return false;
                Messages.Message(L.T("现在无法使用：出口被挡住、无权使用，或正在被占用。", "Cannot use it now: the exit is blocked, it is not allowed, or it is in use."), MessageTypeDefOf.RejectInput, false);
                Log.Message("[FpsBridge] LINK_BLOCKED pawn=" + pawn.thingIDNumber + "; at=" + pawn.Position + "; link=" + blocked);
                return true;
            }
            Job job = JobMaker.MakeJob(JobDefOf.Goto, to);
            job.playerForced = true;
            job.locomotionUrgency = LocomotionUrgency.Jog;
            pawn.pather?.StopDead();
            if (!pawn.jobs.TryTakeOrderedJob(job)) { Messages.Message(L.T("现在无法使用这个", "Cannot use this ") + Label(k) + L.T("。", "."), RimWorld.MessageTypeDefOf.RejectInput, false); return true; }
            rideJob = job; rideTo = to; rideLevel = FloorSpace.Band(pawn.Map, to).Level;
            Bindings.PhysicsPosition.SetValue(Bindings.Avatar, null);
            Log.Message("[FpsBridge] LINK_RIDE pawn=" + pawn.thingIDNumber + "; kind=" + k + "; to=" + to + "; level=" + FloorSpace.Band(pawn.Map, pawn.Position).Level + "->" + rideLevel);
            return true;
        }

        // Once per input frame: arriving shows the new floor; a ride that ended elsewhere is dropped.
        public static void RideFrame(Pawn pawn)
        {
            if (rideJob == null) return;
            if (pawn?.Map != null && pawn.Position == rideTo)
            {
                Api.Floors?.ViewFloor(pawn.Map, rideLevel);
                AimPicker.Reset();
                Log.Message("[FpsBridge] LINK_ARRIVED pawn=" + pawn.thingIDNumber + "; level=" + rideLevel);
            }
            else if (pawn?.CurJob == rideJob) return;
            rideJob = null; rideTo = IntVec3.Invalid;
        }

        public static void ResetRide() { rideJob = null; rideTo = IntVec3.Invalid; }

        // The hint while standing on a ladder/elevator end.
        public static string Prompt(Pawn pawn)
        {
            if (Riding(pawn)) return L.T("正在使用梯子 / 电梯…", "Using the ladder / elevator…");
            if (Time.frameCount % 15 != 0 && promptFrame >= 0 && pawn.Position == promptCell) return prompt;
            promptFrame = Time.frameCount; promptCell = pawn.Position; prompt = null;
            bool up = FindRide(pawn, false, out IntVec3 u, out _, out string ku) && FloorSpace.Band(pawn.Map, u).Level > FloorSpace.Band(pawn.Map, pawn.Position).Level;
            bool down = FindRide(pawn, true, out IntVec3 d, out _, out string kd) && FloorSpace.Band(pawn.Map, d).Level < FloorSpace.Band(pawn.Map, pawn.Position).Level;
            if (up && down) prompt = L.T("轻按 E 乘", "Tap E to ride the ") + Label(ku) + L.T("上楼 · 低头轻按 E 下楼", " up · look down and tap E to go down");
            else if (up) prompt = L.T("轻按 E ", "Tap E to ") + (Label(ku) == L.T("电梯", "elevator") ? L.T("乘电梯", "take the elevator") : L.T("爬", "climb the ") + Label(ku)) + L.T("上楼", " up");
            else if (down) prompt = L.T("轻按 E ", "Tap E to ") + (Label(kd) == L.T("电梯", "elevator") ? L.T("乘电梯", "take the elevator") : L.T("爬", "climb the ") + Label(kd)) + L.T("下楼", " down");
            return prompt;
        }
        private static int promptFrame = -1;
        private static IntVec3 promptCell;
        private static string prompt;

        private const float AscendAlong = .08f;
        private static float nextProbe;

        // While on a stair end and pushing: what the step test saw, at most twice a second (diagnosis of "can't climb").
        private static void Probe(Pawn pawn, string way, V3 at, V3 a, V3 b, V3 dir, bool approaching, bool open)
        {
            if (!FpsMod.Settings.verboseLog || Time.unscaledTime < nextProbe) return;
            nextProbe = Time.unscaledTime + .5f;
            V3 d = b - a; d.Y = 0f; float len = d.Length;
            V3 u = len > 1e-4f ? d * (1f / len) : d, rel = at - a; rel.Y = 0f;
            Log.Message("[FpsBridge] STAIR_PROBE " + way + " pawn=" + pawn.thingIDNumber + "; cell=" + pawn.Position + "; span=" + len.ToString("F2")
                + "; along=" + V3.Dot(rel, u).ToString("F2") + "; side=" + Math.Abs(rel.X * u.Z - rel.Z * u.X).ToString("F2")
                + "; facing=" + V3.Dot(dir * (1f / Math.Max(1e-4f, dir.Length)), u).ToString("F2") + "; approaching=" + approaching + "; passable=" + open);
        }

        public static bool TryStep(Pawn pawn, object avatar, Vector3 input)
        {
            if (!FloorSpace.ProjectZReady || !Ready() || input.sqrMagnitude < .01f || pawn?.Map == null
                || (lastPawn == pawn && Find.TickManager.TicksGame - lastTick < 2)) return false;
            object grid = forMap.Invoke(null, new object[] { pawn.Map });
            if (grid == null) return false;
            Vector3 at = (Vector3?)Bindings.PhysicsPosition.GetValue(avatar) ?? pawn.DrawPos;
            FloorBand ownBand = FloorSpace.Band(pawn.Map, pawn.Position);
            V3 world = ownBand.ToWorld(new V3(at.x, 0f, at.z));
            V3 direction = new V3(input.x, 0f, input.z);
            foreach (object link in (IEnumerable)links.GetValue(grid))
            {
                // Ladders and elevators keep ProjectZ's own climb time.
                if (kind != null && kind.GetValue(link).ToString() != "Stairs") continue;
                IntVec3 lo = (IntVec3)bottom.GetValue(link), hi = (IntVec3)top.GetValue(link);
                IntVec3 from, to;
                if (pawn.Position == lo) { from = lo; to = hi; }
                else if (pawn.Position == hi) { from = hi; to = lo; }
                else continue;
                if (!(stairs.GetValue(link) is Thing building) || !building.Spawned) continue;
                FloorBand other = FloorSpace.Band(pawn.Map, to);
                V3 a = ownBand.ToWorld(new V3(from.x + .5f, 0f, from.z + .5f));
                V3 b = other.ToWorld(new V3(to.x + .5f, 0f, to.z + .5f));
                // Down from the exit (top) cell: the stairwell blocks walking up to the old threshold, so any push toward it.
                // Up: since ProjectZ's 1x4 stairs the cell past the ramp's high end is solid, and the avatar's collision
                // stops short of it; a push from the forward half of the high end is enough.
                bool approaching = FloorGeometry.ApproachingStep(world, direction, a, b, from == hi ? FloorGeometry.DescendAlong : AscendAlong);
                bool open = approaching && (bool)passable.Invoke(grid, new object[] { link, TraverseParms.For(pawn) });
                Probe(pawn, from == lo ? "up" : "down", world, a, b, direction, approaching, open);
                if (!open) continue;
                V3 delta = b - a; delta.Y = 0f; delta = delta * (1f / delta.Length);
                V3 rel = world - a; rel.Y = 0f;
                V3 side = rel - delta * V3.Dot(rel, delta);
                V3 landing = b - delta * .39f + side;
                Vector3 logical = FloorSpace.Logical(pawn.Map, landing, to); logical.y = pawn.def.Altitude;
                pawn.pather?.StopDead();
                pawn.Position = to;
                pawn.Notify_Teleported(endCurrentJob: false, resetTweenedPos: true);
                pawn.pather.nextCell = to;
                Bindings.PhysicsPosition.SetValue(avatar, (Vector3?)logical);
                Api.Floors?.ViewFloor(pawn.Map, other.Level);
                AimPicker.Reset();
                lastPawn = pawn; lastTick = Find.TickManager.TicksGame;
                Log.Message("[FpsBridge] STAIR_STEP pawn=" + pawn.thingIDNumber + "; level=" + ownBand.Level + "->" + other.Level);
                return true;
            }
            return false;
        }
    }
}
