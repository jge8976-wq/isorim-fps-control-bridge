using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace HSK.FpsBridge
{
    // Giddy-up owns mounting, dismounting, riding skill/stat modifiers and the paired pawn job. This optional adapter
    // reads an existing pair; it does not create a second mount or replace the animal's delegate-movement job.
    internal static class GiddyMounts
    {
        private static PropertyInfo singleton, mountProperty;
        private static MethodInfo getData;
        private static FieldInfo mountedSet;
        private static bool bound;
        public static string Status { get; private set; } = "NOT_LOADED";
        public static Pawn Mount(Pawn rider)
        {
            if (!bound)
            {
                bound = true;
                Type storage = AccessTools.TypeByName("GiddyUp.ExtendedDataStorage");
                if (storage == null) return null;
                singleton = AccessTools.Property(storage, "Singleton");
                mountedSet = AccessTools.Field(storage, "isMounted") ?? AccessTools.Field(storage, "isMountedHashSet");
                getData = AccessTools.Method(storage, "GetExtendedPawnData", new[] { typeof(Pawn) });
                mountProperty = AccessTools.Property(AccessTools.TypeByName("GiddyUp.ExtendedPawnData"), "Mount");
                Status = singleton != null && getData != null && mountProperty != null && mountedSet != null ? "API_READY" : "API_MISMATCH";
                Log.Message("[FpsBridge] GIDDY_UP " + Status + "; source=" + storage.Assembly.Location);
            }
            if (Status != "API_READY" || rider?.Spawned != true || rider.Dead || rider.Downed || rider.Faction != Faction.OfPlayer) return null;
            try
            {
                if (!(mountedSet.GetValue(null) is HashSet<int> mounted) || !mounted.Contains(rider.thingIDNumber)) return null;
                object store = singleton.GetValue(null); if (store == null) return null;
                Pawn mount = mountProperty.GetValue(getData.Invoke(store, new object[] { rider })) as Pawn;
                return mount?.Spawned == true && mount.Map == rider.Map && !mount.Dead && !mount.Downed
                    && !mount.InMentalState && mount.RaceProps.Animal ? mount : null;
            }
            catch (Exception e)
            {
                Status = "API_MISMATCH";
                Log.Warning("[FpsBridge] Giddy-up sprint unavailable; normal control continues: " + e.GetBaseException().Message);
                return null;
            }
        }
    }

    internal static class MountedControl
    {
        private static Pawn rider, mount;
        private static object avatar;
        private static readonly MountedMomentum momentum = new MountedMomentum();
        private static readonly MountedSpeedSampler speedSamples = new MountedSpeedSampler();
        private static readonly Dictionary<Pawn, int> contacts = new Dictionary<Pawn, int>();
        private static readonly List<Thing> targets = new List<Thing>();
        private static Vector3 last, heading;
        private static float speed, baseSpeed = 4f, lastReal, jumpRatio, riderHeight, stompRadius = 3.4f;
        private static Vector3 jumpHeading;
        private static bool sample;
        private static bool poseInstalled;
        private static int frame = -1;
        public static bool For(Pawn p) => p != null && p == rider && mount != null;
        internal static bool OwnMount(Thing source, Thing target)
        {
            Pawn owner = source as Pawn ?? (source as Projectile)?.Launcher as Pawn;
            return owner != null && target is Pawn animal && owner == Bridge.ShootingPawn
                && Bridge.Active && For(owner) && animal == mount && GiddyMounts.Mount(owner) == animal;
        }
        public static float SpeedFactor => mount != null ? momentum.Factor : 1f;
        public static float Speed => speed;
        public static bool Flying => MountedTraversal.Flying;
        public static float Lift(Pawn p) => p == rider ? (poseInstalled ? 0f : riderHeight + MountedTraversal.Lift) : p == mount ? MountedTraversal.Lift : 0f;
        internal static void InstallPose(Harmony harmony)
        {
            if (poseInstalled || Api.Version < 13) return;
            MethodInfo flight = AccessTools.DeclaredMethod(typeof(Animals), "FlightOf", new[] { typeof(Pawn), typeof(Vector3).MakeByRefType() });
            if (flight == null) throw new MissingMethodException("Animals.FlightOf");
            harmony.Patch(flight, postfix: new HarmonyMethod(typeof(MountedControl), nameof(AnimalFlight)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(FigureWeapons), "Draw"), prefix: new HarmonyMethod(typeof(MountedControl), nameof(RiderEquipment)));
            Api.AddAttachments(RiderAttachment);
            Api.AddFigureHook(RiderFigure);
            poseInstalled = true;
        }
        internal static Attachment? RiderAttachment(Thing t)
        {
            if (!(t is Pawn p)) return null;
            Pawn animal = GiddyMounts.Mount(p);
            if (animal == null) return null;
            return new Attachment(animal, AttachAnchor.Back, AttachPose.SitNoLegs, AttachFacing.Aim) { offset = new Vector3(0, .055f, 0), leads = true };
        }
        internal static void AnimalFlight(Pawn pawn, ref float __result) { if (pawn == mount) __result += MountedTraversal.Lift; }
        private static void RiderFigure(FigureFrame f)
        {
            if (f.Pawn == rider && !ControlCamera.External && !FirstPersonTransition.Active)
                f.Hide(FigureParts.Head | FigureParts.Face | FigureParts.Hair | FigureParts.Beard | FigureParts.Headgear | FigureParts.Shield | FigureParts.Gear);
        }
        private static bool RiderEquipment(Pawn pawn) => !(pawn == rider && !ControlCamera.External && !FirstPersonTransition.Active);
        public static void Reset()
        {
            rider = mount = null; avatar = null; sample = false; speed = 0f; momentum.Reset(); MountedTraversal.Reset();
            lastReal = Time.unscaledTime; frame = -1; heading = Vector3.zero; contacts.Clear(); speedSamples.Reset();
        }
        private static Vector3 Position(Pawn p, object av) => (Vector3?)Bindings.PhysicsPosition.GetValue(av) ?? p.DrawPos;
        public static void Frame(Pawn p, object av, bool input, bool paused)
        {
            if (frame == Time.frameCount) return; frame = Time.frameCount;
            Pawn animal = GiddyMounts.Mount(p);
            if (animal != mount || (animal != null && rider != p))
            {
                Reset(); frame = Time.frameCount;
                rider = animal != null ? p : null; mount = animal; avatar = av;
                if (mount != null)
                {
                    Stances.State.SeatForRiding();
                    baseSpeed = Mathf.Max(1f, p.GetStatValue(StatDefOf.MoveSpeed));
                    riderHeight = Mathf.Clamp(Bindings.PickHeight(mount) * .6f, .3f, 1.6f);
                }
            }
            float now = Time.unscaledTime, real = Mathf.Clamp(now - lastReal, 0f, .1f); lastReal = now;
            if (mount == null) return;
            // Newer IsoRim attachment providers can already seat a rider. Use their placement when available.
            riderHeight = Poses.AttachRise(p, out _) ? 0f : Mathf.Clamp(Bindings.PickHeight(mount) * .6f, .3f, 1.6f);
            Vector3 pos = Position(p, av);
            float clock = (Find.TickManager?.TickRateMultiplier ?? 1f) * CyberSkills.PlayerTime;
            float dt = paused ? 0f : real * clock;
            if (sample && dt > .0001f)
            {
                Vector3 delta = pos - last; delta.y = 0f;
                // Reject large jumps, but do not mistake tick-sized movement for
                // a teleport merely because the render frame was very short.
                speed = speedSamples.Step(dt, delta.magnitude, Mathf.Max(2f, baseSpeed * .5f));
                if (delta.sqrMagnitude > .00001f) heading = delta.normalized;
            }
            else if (!sample) { speed = 0f; speedSamples.Reset(); }
            bool moving = input && (ControlKeys.Held(ControlAction.Forward) || ControlKeys.Held(ControlAction.Backward)
                || ControlKeys.Held(ControlAction.Left) || ControlKeys.Held(ControlAction.Right));
            if (!paused) momentum.Step(dt, moving, input && ControlKeys.Held(ControlAction.Sprint), speed);
            if (!input) { sample = false; momentum.Reset(); speedSamples.Reset(); speed = 0f; }
            MountedTraversal.Frame(p, mount, av, dt, input, speed, baseSpeed);
            if (input && !paused && !QuickTend.For(p))
            {
                if (ChargeReady(p)) Charge(p, last, pos);
                if (moving && ControlKeys.Held(ControlAction.Sprint) && dt > 0f)
                {
                    Need_Rest rest = mount.needs?.rest; Need_Food food = mount.needs?.food;
                    if (rest != null) rest.CurLevel = Mathf.Max(0f, rest.CurLevel - rest.RestFallPerTick * dt * 60f);
                    if (food != null) food.CurLevel = Mathf.Max(0f, food.CurLevel - food.FoodFallPerTick * dt * 60f);
                    if (rest?.CurLevelPercentage < .02f) momentum.Reset();
                }
            }
            last = Position(p, av); sample = input && !paused;
        }
        private static bool MeleeWeapon(Pawn p) => p.equipment?.Primary?.def.IsMeleeWeapon == true;
        private static float Ratio => Mathf.Min(momentum.Factor, speed / baseSpeed);
        private static bool ChargeReady(Pawn p) => sample && !Flying && MeleeWeapon(p)
            && speed >= MountedRules.MinChargeSpeed && Ratio >= MountedRules.ChargeRatio && heading.sqrMagnitude > .5f;
        private static float Length => Mathf.Clamp(speed * .2f + .9f, 1.6f, 4f);
        private static float Width => Mathf.Clamp(.4f + mount.BodySize * .17f, .55f, 1.3f);
        private static Vector3 World(Pawn p, Vector3 logical) => logical + FloorSpace.Offset(p);
        private static void Charge(Pawn p, Vector3 from, Vector3 to)
        {
            float sweep = Vector3.Distance(from, to), length = Length + sweep, width = Width;
            if (sweep > 2f) return;
            targets.Clear();
            foreach (Pawn victim in p.Map.mapPawns.AllPawnsSpawned)
            {
                if (victim == p || victim == mount || victim.Dead || victim.Destroyed || victim.IsHiddenFromPlayer()
                    || FloorSpace.Band(p.Map, victim.Position).Level != FloorSpace.Band(p.Map, p.Position).Level) continue;
                Vector3 rel = victim.DrawPos - from; rel.y = 0f;
                if (!MountedRules.InLane(Vector3.Dot(rel, heading), Vector3.Cross(rel, heading).y, length, width)) continue;
                if (contacts.TryGetValue(victim, out int until) && Find.TickManager.TicksGame < until) continue;
                if (!GenSight.LineOfSight(p.Position, victim.Position, p.Map, true)) continue;
                targets.Add(victim);
            }
            targets.Sort((a,b) => a.Position.DistanceToSquared(p.Position).CompareTo(b.Position.DistanceToSquared(p.Position)));
            float mass = mount.BodySize + p.BodySize * .5f;
            foreach (Pawn victim in targets)
            {
                if (Ratio < MountedRules.ChargeRatio) break;
                float ratio = Ratio;
                contacts[victim] = Find.TickManager.TicksGame + 120;
                Deal(p, victim, ratio);
                if (!victim.Dead && victim.Spawned) Knockback.Start(victim, p, heading, MountedRules.Impact(mass, victim.BodySize), ratio);
                momentum.Impact(MountedRules.Cost(mass, victim.BodySize));
            }
        }
        private static void Deal(Pawn p, Thing victim, float ratio)
        {
            Verb verb = p.equipment?.PrimaryEq?.PrimaryVerb;
            if (!Melee.HasDamageVerb(verb)) verb = p.meleeVerbs.TryGetMeleeVerb(victim);
            List<DamageInfo> infos = Melee.DamageInfos(verb, victim);
            if (infos == null) return;
            float factor = MountedRules.DamageMultiplier(p.skills?.GetSkill(SkillDefOf.Melee)?.Level ?? 0, ratio);
            foreach (DamageInfo info in infos)
            {
                if (victim.Destroyed) break;
                DamageInfo damage = new DamageInfo(info); damage.SetAmount(info.Amount * factor); Melee.TakeDamage(verb, victim, damage);
            }
            p.skills?.Learn(SkillDefOf.Melee, 80f);
            SoundDefOf.Pawn_Melee_Punch_HitPawn?.PlayOneShot(new TargetInfo(p.Position, p.Map));
        }
        internal static void Landed(Pawn p, float ratio, float charge, Vector3 direction)
        {
            jumpRatio = ratio; jumpHeading = direction; stompRadius = MountedJumpRules.Radius(charge);
            Cleave(p);
        }
        private static void Cleave(Pawn p)
        {
            if (!For(p) || !MeleeWeapon(p)) return;
            targets.Clear(); var seen = new HashSet<Thing>();
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(p.Position, stompRadius, true))
            {
                if (!cell.InBounds(p.Map) || FloorSpace.Band(p.Map, cell).Level != FloorSpace.Band(p.Map, p.Position).Level) continue;
                Vector3 rel = cell.ToVector3Shifted() - p.DrawPos; rel.y = 0f;
                if (!MountedRules.InCleave(Vector3.Dot(rel, jumpHeading), Vector3.Cross(rel, jumpHeading).y, stompRadius)
                    || !GenSight.LineOfSight(p.Position, cell, p.Map, true)) continue;
                foreach (Thing thing in cell.GetThingList(p.Map))
                    if (thing != p && thing != mount && !thing.Destroyed && (thing is Pawn || thing.def.useHitPoints)
                        && (thing is Pawn || thing.def.category == ThingCategory.Building || thing.def.category == ThingCategory.Plant || thing.def.category == ThingCategory.Item)
                        && seen.Add(thing)) targets.Add(thing);
            }
            foreach (Thing target in targets) if (!target.Destroyed) Deal(p, target, jumpRatio);
            V3 spot = FloorSpace.Spot(p);
            Visuals.GroundRing(new Vector3(spot.X, spot.Y, spot.Z), stompRadius, new Color(1f, .65f, .2f, .7f));
        }
        public static void Draw(Pawn p)
        {
            if (!For(p)) return;
            MountedTraversal.Preview(p, avatar, speed, baseSpeed, MeleeWeapon(p));
            bool ready = ChargeReady(p);
            bool sprinting = ControlKeys.Held(ControlAction.Sprint)
                && (ControlKeys.Held(ControlAction.Forward) || ControlKeys.Held(ControlAction.Backward)
                    || ControlKeys.Held(ControlAction.Left) || ControlKeys.Held(ControlAction.Right));
            if (!ready && !sprinting) return;
            Vector3 direction = heading;
            if (direction.sqrMagnitude < .5f) direction = Quaternion.Euler(0, ControlCamera.AttackYaw(p), 0) * Vector3.forward;
            Vector3 a = World(p, Position(p, avatar)); a.y = FloorSpace.Offset(p).y + .08f;
            Vector3 end = a + direction * Length, side = Vector3.Cross(Vector3.up, direction) * Width;
            Color c = ready ? new Color(1f, .75f, .15f, .9f) : new Color(.35f, .8f, 1f, .85f);
            Visuals.Line(a - side, end - side, c, .045f); Visuals.Line(a + side, end + side, c, .045f);
            Visuals.Line(end - side * 1.7f, end + direction * .7f, c, .055f); Visuals.Line(end + side * 1.7f, end + direction * .7f, c, .055f);
            Visuals.Line(a, end + direction * .7f, c, .10f);
        }
        public static void DrawHud(Pawn p)
        {
            if (!For(p) || InventoryPanel.Blocking || Bridge.MainTabBlocks) return;
            GameFont f = Text.Font; TextAnchor a = Text.Anchor; Color c = GUI.color;
            try
            {
                Text.Font = GameFont.Small; Text.Anchor = TextAnchor.MiddleLeft; GUI.color = Color.white;
                float y = Math.Max(12, UI.screenHeight - 460);
                Widgets.DrawBoxSolid(new Rect(12, y, 354, 34), new Color(.035f, .045f, .06f, .9f));
                Widgets.Label(new Rect(20, y, 338, 34), L.T("骑乘速度 ", "Riding speed ") + speed.ToString("F1") + L.T(" 格/秒", " cells/s")
                    + (ChargeReady(p) ? L.T(" · 冲击就绪", " · charge ready") : "")
                    + " · " + ControlKeys.Label(ControlAction.Sprint) + L.T(" 冲刺", " sprint"));
            }
            finally { Text.Font = f; Text.Anchor = a; GUI.color = c; }
        }
    }

    internal static class Knockback
    {
        private sealed class Flight { public Pawn pawn; public Vector3 dir; public float left, elapsed, peak, duration; public int floor; public Map map; }
        private static readonly Dictionary<Pawn, Flight> flights = new Dictionary<Pawn, Flight>();
        private static readonly List<Pawn> done = new List<Pawn>();
        private static float lastReal = -1f;
        public static void Frame()
        {
            // Root_Play can finish one update while returning to the menu. Find.TickManager's getter
            // dereferences Current.Game, so checking the getter itself for null is already too late.
            if (Current.Game == null || Current.ProgramState != ProgramState.Playing) { Reset(); return; }
            float now = Time.unscaledTime, dt = lastReal < 0f ? 0f : Mathf.Clamp(now - lastReal, 0f, .1f);
            lastReal = now;
            TickManager clock = Find.TickManager;
            if (clock == null || clock.Paused) return;
            Step(dt * clock.TickRateMultiplier);
        }
        public static void Start(Pawn target, Pawn source, Vector3 direction, ChargeImpact kind, float ratio)
        {
            if (!target.Spawned || target.Dead) return;
            int ticks = kind == ChargeImpact.Airborne ? 75 : kind == ChargeImpact.Push ? 40 : 65;
            target.stances?.stunner.StunFor(ticks, source);
            if (kind == ChargeImpact.Knockdown && !target.Downed)
            {
                JobDef def = DefDatabase<JobDef>.GetNamedSilentFail("FPS_ChargeKnockdown");
                if (def != null) { Job job = JobMaker.MakeJob(def); job.playerForced = true; target.jobs.TryTakeOrderedJob(job); }
            }
            flights[target] = new Flight { pawn = target, dir = direction, left = (kind == ChargeImpact.Airborne ? 2.8f : kind == ChargeImpact.Push ? 1.5f : .35f) * ratio,
                duration = .65f, peak = kind == ChargeImpact.Airborne ? .8f : .05f, floor = FloorSpace.Band(target.Map, target.Position).Level, map = target.Map };
        }
        public static void Reset() { flights.Clear(); lastReal = -1f; }
        public static void Step(float dt)
        {
            if (dt <= 0f || flights.Count == 0) return;
            done.Clear();
            foreach (var pair in flights)
            {
                Pawn p = pair.Key; Flight f = pair.Value;
                if (p.Destroyed || !p.Spawned || p.Dead || p.Map != f.map || FloorSpace.Band(p.Map, p.Position).Level != f.floor) { done.Add(p); continue; }
                float remain = Math.Min(f.left, dt * 9f);
                while (remain > .001f)
                {
                    float step = Math.Min(remain, .2f);
                    // Displace in cell-sized increments without tunnelling through walls or floor edges.
                    f.left -= step; remain -= step;
                    // Store sub-cell displacement on the flight so the legal cell advances continuously.
                    f.elapsed += step;
                    IntVec3 cell = (p.Position.ToVector3Shifted() + f.dir * f.elapsed).ToIntVec3();
                    if (!cell.InBounds(p.Map) || !cell.Standable(p.Map) || FloorSpace.Band(p.Map, cell).Level != f.floor
                        || (FloorSpace.ProjectZReady && f.floor != 0 && !FloorSpace.HasFloorAt(p.Map, cell))
                        || !GenSight.LineOfSight(p.Position, cell, p.Map, true)) { f.left = 0f; break; }
                    if (cell != p.Position) { p.pather.StopDead(); p.Position = cell; p.Drawer.tweener.ResetTweenedPosToRoot(); f.elapsed = 0f; }
                }
                f.duration -= dt;
                if (f.duration <= 0f || f.left <= .001f) done.Add(p);
            }
            foreach (Pawn p in done) flights.Remove(p);
        }
        public static float Lift(Pawn p) => flights.TryGetValue(p, out Flight f) ? Mathf.Sin(Mathf.Clamp01(f.duration / .65f) * Mathf.PI) * f.peak : 0f;
    }
    public sealed class JobDriver_ChargeKnockdown : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;
        protected override IEnumerable<Toil> MakeNewToils()
        {
            Toil down = Toils_General.Wait(65);
            down.initAction = () => { pawn.pather.StopDead(); pawn.jobs.posture = PawnPosture.LayingOnGroundNormal; };
            down.AddFinishAction(() => { if (!pawn.Dead&&!pawn.Downed&&pawn.jobs!=null) pawn.jobs.posture = PawnPosture.Standing; });
            yield return down;
        }
    }
}
