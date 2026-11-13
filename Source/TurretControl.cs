using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using HSK.FpsBridge.Interop;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace HSK.FpsBridge
{
    // 0.8.0 operating a turret in person: E next to an own turret under the crosshair puts the view on its top. The
    // mouse turns the top at the turret's own turn speed, LMB fires along the view; E, the side buttons or the
    // session's end let go. The pawn stays where it stood.
    internal static class TurretControl
    {
        private const float MinPitch = -25f, MaxPitch = 50f;
        private static IManualTurret current;
        private static Pawn owner;
        private static Pawn_JobTracker preservingJobs;
        private static float rotation;
        private static int lookFrame = -1, targetFrame = -1;
        private static IManualTurret looked;
        private static LocalTargetInfo target = LocalTargetInfo.Invalid;
        public static bool Enabled { get; private set; }

        public static bool Manned => current != null && (RemoteTurret.On || owner != null && owner == Bridge.ShootingPawn);
        public static IManualTurret Current => Manned ? current : null;
        public static LocalTargetInfo Target => Manned ? target : LocalTargetInfo.Invalid;
        internal static bool DirectProjectile(Verb verb) => Ballistics.IsRound(verb) || RatkinTurretShots.Supported(verb)
            || CeCompat.ProjectileFor(verb)?.projectile is ProjectileProperties ce&&!ce.flyOverhead;
        internal static void ReloadKey(Pawn pawn)
        {
            if(ControlKeys.Down(ControlAction.Reload)&&Current is VanillaTurret native&&CeCompat.Owns(native.Verb)){CeCompat.RequestReload(native.Verb);return;}
            if (Artillery.On && Current is not CeTurret) { Artillery.ReloadKey(pawn); return; }
            if (Current is IArtilleryTurret t)
            {
                string why = t.Reload(pawn);
                if (why != null) Messages.Message(why, MessageTypeDefOf.RejectInput, false);
            }
        }

        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Building_TurretGun), nameof(Building_TurretGun.TryFindNewTarget))
                ?? throw new MissingMethodException("Building_TurretGun.TryFindNewTarget"),
                postfix: new HarmonyMethod(typeof(VanillaTurret), nameof(VanillaTurret.AfterFindTarget)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(TurretTop), nameof(TurretTop.TurretTopTick)) ?? throw new MissingMethodException("TurretTop.TurretTopTick"),
                prefix: new HarmonyMethod(typeof(VanillaTurret), nameof(VanillaTurret.BeforeTopTick)) { priority = Priority.First });
            harmony.Patch(AccessTools.PropertyGetter(typeof(CompMannable), nameof(CompMannable.MannedNow)) ?? throw new MissingMethodException("CompMannable.MannedNow"),
                postfix: new HarmonyMethod(typeof(VanillaTurret), nameof(VanillaTurret.AfterMannedNow)) { priority = Priority.Last });
            harmony.Patch(AccessTools.PropertyGetter(typeof(CompMannable), nameof(CompMannable.ManningPawn)) ?? throw new MissingMethodException("CompMannable.ManningPawn"),
                postfix: new HarmonyMethod(typeof(VanillaTurret), nameof(VanillaTurret.AfterManningPawn)) { priority = Priority.Last });
            // Direct-fire turrets operated in person shoot 3D rounds along the view, as a gun in hand does.
            harmony.Patch(AccessTools.Method(typeof(Verb_LaunchProjectile), "TryCastShot") ?? throw new MissingMethodException("Verb_LaunchProjectile.TryCastShot"),
                prefix: new HarmonyMethod(typeof(TurretControl), nameof(BeforeTurretShot)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob)),
                prefix: new HarmonyMethod(typeof(TurretControl), nameof(KeepTurretJob)));
            harmony.Patch(AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryTakeOrderedJob),
                new[] { typeof(Job), typeof(JobTag?), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(TurretControl), nameof(SkipAvatarWait)));
            VanillaTurret.Bind();
            TurretView.Install(harmony);
            Enabled = true;
        }

        public static void Reset() => Release(false);

        internal static void Release(bool resumeWork)
        {
            WeaponLight.Release();
            TurretOptics.Release();
            IManualTurret was = current;
            Pawn operatorPawn = owner;
            Thing pendingAmmo = (was as CeTurret)?.ManualReloadSource;
            Artillery.End();
            current = null; owner = null; looked = null; target = LocalTargetInfo.Invalid; lookFrame = targetFrame = -1;
            TurretView.Set(null);
            if (was == null) return;
            try
            {
                was.End();
                if (resumeWork) ResumeWork(was, operatorPawn, pendingAmmo);
            }
            catch (Exception e) { Log.ErrorOnce("[FpsBridge] Turret release failed: " + e, 19271711); }
        }

        private static bool IsManning(Job job, Thing turret) => job?.def == JobDefOf.ManTurret && job.targetA.Thing == turret;
        private static bool IsReloading(Job job, Thing turret) => job?.def.defName == "ReloadTurret" && job.targetA.Thing == turret;

        internal static IManualTurret CurrentWork(Pawn pawn)
        {
            Job job = pawn.CurJob;
            if (job?.targetA.Thing is not Building_Turret turret || !turret.Spawned || turret.Map != pawn.Map
                || turret.Faction != pawn.Faction || turret.TryGetComp<CompMannable>() is not CompMannable manning) return null;
            if (!(IsManning(job, turret) && manning.ManningPawn == pawn)
                && !(IsReloading(job, turret) && pawn.jobs.jobQueue.Any(q => IsManning(q.job, turret)))) return null;
            var adapter = ManualTurrets.For(turret);
            return adapter?.Refusal(pawn) == null ? adapter : null;
        }

        internal static void BindAvatar(Pawn pawn, bool preserveWork)
        {
            // Perspective Shift replaces the current job with a Wait during SetAvatar.
            if (preserveWork) preservingJobs = pawn.jobs;
            try { Bindings.SetAvatar(pawn, false); }
            finally { preservingJobs = null; }
        }

        private static bool KeepTurretJob(Pawn_JobTracker __instance) => __instance != preservingJobs;
        private static bool SkipAvatarWait(Pawn_JobTracker __instance, Job job, ref bool __result)
        {
            if (__instance != preservingJobs || job.def != JobDefOf.Wait) return true;
            __result = true;
            return false;
        }

        internal static void TakeCurrentWork(Pawn pawn, IManualTurret turret) => ManCore(pawn, turret, true);

        private static void ResumeWork(IManualTurret adapter, Pawn pawn, Thing pendingAmmo)
        {
            Thing turret = adapter.Thing;
            if (pawn?.Spawned != true || pawn.Dead || pawn.Downed || !turret.Spawned || turret.Map != pawn.Map
                || turret.Faction != pawn.Faction || turret.TryGetComp<CompMannable>() == null
                || !Reach(pawn, turret) || IsReloading(pawn.CurJob, turret)) return;
            if (!IsManning(pawn.CurJob, turret))
            {
                if (pawn.CurJob != null && pawn.CurJob.def != JobDefOf.Wait && pawn.CurJob.def != JobDefOf.Wait_Combat) return;
                if (!pawn.CanReserveAndReach(turret, PathEndMode.InteractionCell, Danger.Deadly)) return;
                pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.ManTurret, turret), JobTag.Misc);
            }
            if (adapter is CeTurret ce && IsManning(pawn.CurJob, turret) && (pendingAmmo != null || !ce.Loaded))
                ce.ResumeNativeReload(pawn, pendingAmmo);
        }

        // ---- getting on and off

        // An own turret under the crosshair, within two cells of the pawn (cached per frame; only while on foot).
        public static IManualTurret Looked(Pawn pawn)
        {
            if (!Enabled || Manned || pawn?.Map == null) return null;
            if (lookFrame == Time.frameCount) return looked;
            lookFrame = Time.frameCount; looked = null;
            Vector3 eye = FirstPerson.Eye;
            V3 a = new V3(eye.x, eye.y, eye.z);
            V3 dir = HitMath.Direction(FirstPerson.Yaw, FirstPerson.Pitch);
            V3 hit = ControlCamera.AimPoint(pawn);
            if ((hit - a).Length > WorkControl.Range + 3f) return null;
            // The contact lies on the surface: a hair inside it names the thing that was hit.
            IntVec3 cell = FloorSpace.Cell(pawn.Map, hit + dir * .05f);
            if (!cell.InBounds(pawn.Map)) return null;
            foreach (Thing t in cell.GetThingList(pawn.Map))
            {
                if (!(t is Building) || t.Faction != Faction.OfPlayer || !Reach(pawn, t)) continue;
                IManualTurret m = ManualTurrets.For(t);
                if (m != null) { looked = m; break; }
            }
            return looked;
        }

        private static bool Reach(Pawn pawn, Thing t) => (t.OccupiedRect().ClosestCellTo(pawn.Position) - pawn.Position).LengthHorizontal <= WorkControl.Range;

        // E: onto the looked-at turret on foot; on a turret, the next one in the network. True when E was used here.
        public static bool Toggle(Pawn pawn)
        {
            if (!Enabled) return false;
            if (Manned) { Switch(pawn, 1); return true; }
            IManualTurret t = Looked(pawn);
            if (t == null) return false;
            string why = MechControl.IsMech(pawn) ? L.T("机械体不能操纵炮台。", "Mechs cannot man turrets.") : pawn.WorkTagIsDisabled(WorkTags.Violent) ? pawn.LabelShort + L.T(" 不能使用暴力。", " is incapable of violence.") : t.Refusal(pawn);
            if (why != null) { Messages.Message(why, MessageTypeDefOf.RejectInput, false); return true; }
            WeaponView.Release(); AimMotion.Release(); FireControl.CancelInput(); AimPicker.Reset();
            Stances.State.CancelSprint();
            Man(pawn, t);
            return true;
        }

        private static void Man(Pawn pawn, IManualTurret t) => ManCore(pawn, t, false);

        private static void ManCore(Pawn pawn, IManualTurret t, bool immediate)
        {
            current = t; owner = pawn; looked = null; targetFrame = -1;
            t.Begin(pawn);
            TurretView.Set(t.AimMode == TurretAimMode.Direct ? t.Thing : null);
            rotation = t.Rotation;
            if (t.AimMode == TurretAimMode.Artillery && t is IArtilleryTurret a) Artillery.Begin(a, pawn);
            else FirstPerson.Look(Mathf.DeltaAngle(FirstPerson.Yaw, rotation), Mathf.Clamp(8f, MinPitch, MaxPitch) - FirstPerson.Pitch);
            Bridge.SyncView();
            if (pawn != null && !immediate) FirstPersonTransition.Begin(t.Thing);
            int others = Network(pawn).Count - 1;
            Messages.Message(L.T("操纵 ", "Manning ") + t.Thing.LabelShort + (others > 0 ? L.T(" · Q/E 切换（另有 ", " · Q/E switch (") + others + L.T(" 座）", " more)") : "") + L.T(" · F / 前侧键离开 · 后侧键回上帝视角", " · F / forward button to leave · back button to the god view"),
                MessageTypeDefOf.SilentInput, false);
            Log.Message("[FpsBridge] TURRET_ON thing=" + t.Thing.ThingID + "; impl=" + t.GetType().FullName);
        }

        internal static void TakeRemote(IManualTurret t) => Man(null, t);

        // ---- turret network: Q/E steps through own turrets around the operator, like switching between cameras

        public const float NetworkRange = 50f;

        // Operable own turrets within range of the operator (the current one included), clockwise around them.
        private static List<IManualTurret> Network(Pawn pawn)
        {
            var list = new List<(float angle, IManualTurret t)>();
            Map map = RemoteTurret.On ? RemoteTurret.Map : pawn?.Map;
            if (map == null) return new List<IManualTurret>();
            IntVec3 anchor = RemoteTurret.On ? RemoteTurret.Anchor : pawn.Position;
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
            {
                if (!b.Spawned || (b.Position - anchor).LengthHorizontal > NetworkRange) continue;
                IManualTurret t = current != null && current.Thing == b ? current : ManualTurrets.For(b);
                if (t == null || (t != current && (RemoteTurret.On ? RemoteTurret.Refusal(t) : t.Refusal(pawn)) != null)) continue;
                list.Add(((b.TrueCenter() - anchor.ToVector3Shifted()).AngleFlat(), t));
            }
            list.Sort((x, y) => x.angle.CompareTo(y.angle));
            var result = new List<IManualTurret>(list.Count);
            foreach (var e in list) result.Add(e.t);
            return result;
        }

        public static void Switch(Pawn pawn, int direction)
        {
            if (!Manned) return;
            List<IManualTurret> net = Network(pawn);
            int at = net.IndexOf(current);
            if (net.Count < 2 || at < 0) { Messages.Message(NetworkRange.ToString("F0") + L.T(" 格内没有其他可操纵的己方炮台。", " cells around: no other turret of yours to man."), MessageTypeDefOf.RejectInput, false); return; }
            IManualTurret next = net[(at + (direction < 0 ? net.Count - 1 : 1)) % net.Count];
            Thing from = current.Thing;
            Reset();
            Man(pawn, next);
            Log.Message("[FpsBridge] TURRET_SWITCH from=" + from?.ThingID + "; to=" + next.Thing.ThingID);
        }

        public static void Leave(string why)
        {
            if (RemoteTurret.On) { RemoteTurret.End(why); return; }
            if (current == null) return;
            Thing thing = current.Thing;
            Release(true);
            if (why != null) Messages.Message(why, MessageTypeDefOf.SilentInput, false);
            Log.Message("[FpsBridge] TURRET_OFF thing=" + thing?.ThingID + "; reason=" + why);
        }

        // ---- view

        public static Vector3 Eye()
        {
            if (Artillery.On) return Artillery.Eye();
            Thing t = current.Thing;
            V3 s = FloorSpace.Spot(t);
            ImportedTurretGeometry g = t.def.GetModExtension<ImportedTurretGeometry>();
            if (g != null) { Vector3 at = Quaternion.AngleAxis(FirstPerson.Yaw, Vector3.up) * g.eye; return new Vector3(s.X + at.x, s.Y + at.y, s.Z + at.z); }
            return new Vector3(s.X, s.Y + current.EyeHeight, s.Z);
        }

        // After the mouse turned the view: the top follows at its turn speed and the view never runs ahead of it.
        public static void ClampLook(Pawn pawn, bool paused)
        {
            if (!Manned) return;
            Thing t = current.Thing;
            if (t == null || !t.Spawned || t.Map != (RemoteTurret.On ? RemoteTurret.Map : pawn?.Map) || t.Faction != Faction.OfPlayer) { Leave(L.T("炮台已失去", "Turret lost")); return; }
            float dt = paused ? 0f : Time.unscaledDeltaTime * (Find.TickManager?.TickRateMultiplier ?? 1f);
            // On the artillery map the top turns toward the impact point and the view stays put.
            bool map = Artillery.MapView;
            float want = map ? Artillery.AimYaw() : FirstPerson.Yaw;
            float step = Mathf.Clamp(Mathf.DeltaAngle(rotation, want), -current.TurnRate * dt, current.TurnRate * dt);
            rotation = Mathf.Repeat(rotation + step, 360f);
            float minPitch = current.AimMode == TurretAimMode.Direct ? -55f : MinPitch;
            if (!map) FirstPerson.Look(Mathf.DeltaAngle(FirstPerson.Yaw, rotation), Mathf.Clamp(FirstPerson.Pitch, minPitch, MaxPitch) - FirstPerson.Pitch);
            current.Aim(rotation);
        }

        // ---- firing (once per frame from the combat dispatch)

        public static void Frame(Pawn pawn, bool held, bool pressed)
        {
            if (!Manned) return;
            LocalTargetInfo t = AimTarget(pawn);
            if (Artillery.On) { target = t; targetFrame = Time.frameCount; }
            try
            {
                current.Trigger(t, held, pressed);
                Artillery.AfterTrigger(pawn);
            }
            catch (Exception e) { Log.ErrorOnce("[FpsBridge] Turret trigger failed: " + e, 19271712); Leave(L.T("炮台出错，已离开", "Turret error, left it")); }
        }

        // Where a round leaves: just outside the turret's own footprint, along the view.
        internal static V3 Muzzle(out V3 dir)
        {
            Thing self = current.Thing;
            Vector3 e = Eye();
            dir = HitMath.Direction(FirstPerson.Yaw, FirstPerson.Pitch);
            ImportedTurretGeometry geometry = self.def.GetModExtension<ImportedTurretGeometry>();
            if (geometry != null)
            {
                V3 root = FloorSpace.Spot(self);
                Vector3 at = geometry.pitchPivot + Quaternion.AngleAxis(FirstPerson.Pitch, Vector3.right) * (geometry.muzzle - geometry.pitchPivot);
                at = Quaternion.AngleAxis(FirstPerson.Yaw, Vector3.up) * at;
                return root + new V3(at.x, at.y, at.z) + dir * .04f;
            }
            return new V3(e.x, e.y, e.z) + dir * (Mathf.Max(self.def.size.x, self.def.size.z) * .75f + .3f);
        }

        internal static V3 ManualShotDirection(Pawn operatorPawn, V3 direction, ThingDef projectile)
        {
            if (operatorPawn == null || CeCompat.IsExplosiveDef(projectile)) return direction;
            var skill = operatorPawn?.skills?.GetSkill(SkillDefOf.Shooting);
            float level = skill == null || skill.TotallyDisabled ? 0f : skill.Level;
            float steadiness = Mathf.Lerp(.5f, AimSkill.Steadiness((int)level), FpsMod.Settings.shootingSkillInfluence);
            float spread = FpsMod.Settings.manualTurretDispersion * Mathf.Lerp(1f, .25f, steadiness);
            if (spread <= 0f) return direction;
            float heading = Mathf.Atan2(direction.X, direction.Z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Atan2(direction.Y, new Vector2(direction.X, direction.Z).magnitude) * Mathf.Rad2Deg;
            return HitMath.Direction(heading + Rand.Range(-spread,spread), pitch + Rand.Range(-spread,spread));
        }

        // What the crosshair rests on. Nothing there (sky) or out of reach: a cell along the view within range, so the
        // turret still fires where it points (the round itself flies along the view, BeforeTurretShot).
        internal static LocalTargetInfo AimTarget(Pawn pawn) => Artillery.On ? Artillery.Target() : Pick(pawn);

        private static LocalTargetInfo Pick(Pawn pawn)
        {
            if (targetFrame == Time.frameCount) return target;
            targetFrame = Time.frameCount;
            Thing self = current.Thing;
            V3 a = Muzzle(out V3 dir);
            V3 b = a + dir * Mathf.Max(10f, current.Range + 5f);
            Map map = self.Map;
            LocalTargetInfo t = Ballistics.PickTarget(RemoteTurret.On ? self : pawn, a, b);
            if (t.Thing == self || t.Thing == pawn) t = LocalTargetInfo.Invalid;
            float reach = Mathf.Max(current.MinRange + .5f, current.Range - .5f);
            if (!t.IsValid || (t.Cell - self.Position).LengthHorizontal > current.Range)
            {
                V3 far = a + new V3(dir.X, 0f, dir.Z) * (reach / Mathf.Max(.05f, Mathf.Sqrt(dir.X * dir.X + dir.Z * dir.Z)));
                IntVec3 cell = FloorSpace.Cell(map, new V3(far.X, FloorSpace.Spot(self).Y + .01f, far.Z));
                t = cell.InBounds(map) ? new LocalTargetInfo(cell) : LocalTargetInfo.Invalid;
            }
            target = t;
            return target;
        }

        // The operated direct-fire turret's shot: our round along the view instead of the game's 2D shot at the target.
        public static bool BeforeTurretShot(Verb_LaunchProjectile __instance, ref bool __result)
        {
            if (!Manned || Artillery.On || !current.OwnsVerb(__instance) || !DirectProjectile(__instance)) return true;
            V3 start = Muzzle(out V3 dir);
            __result = Ballistics.FireTurretRound(RemoteTurret.On ? current.Thing : owner, __instance, start, ManualShotDirection(owner, dir, __instance.Projectile));
            return false;
        }

        // ---- HUD

        private static bool TryAntiAirLead(out Vector2 point2D)
        {
            point2D = default;
            if (!FpsMod.Settings.showAntiAirLead || !Manned || current.AimMode != TurretAimMode.Direct
                || AerialFlight.Store == null || Artillery.On) return false;
            var gun = current.Thing as Building_Turret;
            var verb = gun?.AttackVerb;
            var ce = CeCompat.ProjectileFor(verb);
            var projectile = ce ?? (verb as Verb_LaunchProjectile)?.Projectile;
            if (projectile?.projectile == null || projectile.projectile.flyOverhead) return false;
            float speed = AerialTurretTargeting.ShotSpeed(verb, projectile);
            V3 muzzle = Muzzle(out V3 bore);
            Pawn best = null;
            V3 center = default;
            float angle = 14f;
            foreach (var entry in AerialFlight.Store.Flights)
            {
                var plane = entry.Key;
                if (!entry.Value.Airborne || !plane.Spawned || plane.Map != gun.Map
                    || !plane.HostileTo(gun) || plane.Position.Fogged(gun.Map)) continue;
                V3 point = VehicleModels.V(AerialTargetLock.Point(plane));
                V3 delta = point - muzzle;
                if (delta.Length > current.Range || delta.Length < current.MinRange) continue;
                float next = Vector3.Angle(VehicleModels.U(bore), VehicleModels.U(delta));
                if (next < angle) { best = plane; center = point; angle = next; }
            }
            if (best == null || Ballistics.SightContact(gun, muzzle, center, out V3 contact, false)
                && (contact - center).Length > AerialGeometry.BoundsAt(best,AerialFlight.Store.Flights[best].Motor).extents.magnitude) return false;
            if (!AerialCombat.Intercept(best, muzzle, center, speed, 1f, out var velocity) || velocity.Length < .001f) return false;
            Vector3 projected = VehicleModels.U(muzzle + velocity * ((center - muzzle).Length / velocity.Length));
            return IffMarkers.Project(projected, out point2D, out _)
                && point2D.x >= 8 && point2D.x <= UI.screenWidth - 8 && point2D.y >= 8 && point2D.y <= UI.screenHeight - 8;
        }

        private static void DrawAntiAirLead()
        {
            if (!TryAntiAirLead(out Vector2 point2D)) return;
            Color cyan = new Color(.2f,.95f,1f,.9f);
            Widgets.DrawBoxSolid(new Rect(point2D.x - 7, point2D.y - 7, 14, 2), cyan);
            Widgets.DrawBoxSolid(new Rect(point2D.x - 7, point2D.y + 5, 14, 2), cyan);
            Widgets.DrawBoxSolid(new Rect(point2D.x - 7, point2D.y - 5, 2, 10), cyan);
            Widgets.DrawBoxSolid(new Rect(point2D.x + 5, point2D.y - 5, 2, 10), cyan);
        }

        public static void DrawHud(Pawn pawn)
        {
            DrawAntiAirLead();
            float x = UI.screenWidth * .5f, y = UI.screenHeight * .5f;
            if (!Manned)
            {
                IManualTurret t = Looked(pawn);
                if (t != null) Label(new Rect(x - 160, y + 22, 320, 24), L.T("轻按 ", "Tap ") + ControlKeys.Label(ControlAction.InteractLeanRight)
                    + L.T(" 操纵 ", " to man ") + t.Thing.LabelShort, new Color(1f, .9f, .55f), GameFont.Small);
                return;
            }
            if (Artillery.On) { Artillery.DrawHud(pawn); return; }
            DrawWeaponCard();
            bool scoped = TurretOptics.Draw();
            LocalTargetInfo tg = Pick(pawn);
            float dist = tg.IsValid ? (tg.Cell - current.Thing.Position).LengthHorizontal : -1f;
            bool inRange = dist >= 0f && dist >= current.MinRange && dist <= current.Range;
            Color c = !tg.IsValid ? new Color(1f, 1f, 1f, .6f) : inRange ? new Color(.55f, 1f, .6f) : new Color(1f, .35f, .3f);
            if (!scoped) {
            Widgets.DrawBoxSolid(new Rect(x - 20, y, 14, 1), c); Widgets.DrawBoxSolid(new Rect(x + 7, y, 14, 1), c);
            Widgets.DrawBoxSolid(new Rect(x, y - 20, 1, 14), c); Widgets.DrawBoxSolid(new Rect(x, y + 7, 1, 14), c);
            Widgets.DrawBoxSolid(new Rect(x - 1, y - 1, 3, 3), c);
            }
            (current as VanillaTurret)?.DrawHeat(x, y + 30f);
            Thing thing = current.Thing;
            string line2 = (dist >= 0f ? L.T("距离 ", "Distance ") + dist.ToString("F1") : L.T("距离 —", "Distance —")) + L.T(" / 射程 ", " / range ") + current.Range.ToString("F0")
                + (current.MinRange > 0f ? L.T("（最近 ", " (min ") + current.MinRange.ToString("F0") + L.T("）", ")") : "")
                + L.T(" · 耐久 ", " · HP ") + (thing.MaxHitPoints > 0 ? (100f * thing.HitPoints / thing.MaxHitPoints).ToString("F0") + "%" : "—");
            float w = 520f, top = UI.screenHeight - 150f;
            Widgets.DrawBoxSolid(new Rect(x - w / 2, top, w, 64), new Color(0f, 0f, 0f, .55f));
            if (current is ISelectableFireMode modes && modes.FireModes.Count > 1)
                Label(new Rect(x - w / 2, top + 3, w, 22), TurretModeLabel(modes.FireMode), Color.white, GameFont.Small);
            Label(new Rect(x - w / 2, top + 25, w, 20), line2, new Color(.85f, .85f, .85f), GameFont.Tiny);
            Label(new Rect(x - w / 2, top + 43, w, 20), ControlKeys.Label(ControlAction.Fire) + L.T(" 开火 · ", " fire · ")
                + ControlKeys.Label(ControlAction.Aim) + L.T(" 开镜 · 滚轮倍率 · ", " scope · wheel zoom · ")
                + ControlHints.For(L.T("B 模式 · Q/E 切换 · F 离开", "B mode · Q/E switch · F leave")), new Color(.7f, .7f, .7f), GameFont.Tiny);
        }

        private static string TurretModeLabel(TurretFireMode mode) => mode switch
        {
            TurretFireMode.Auto => L.T("全自动", "Automatic"),
            TurretFireMode.Burst3 => L.T("三连发", "3-round burst"),
            _ => L.T("单发", "Single")
        };

        internal static void DrawWeaponCard()
        {
            if (Current is not { } turret || Bridge.MainTabBlocks || InventoryPanel.Blocking) return;
            var thing = turret.Thing;
            var ammo = turret switch
            {
                CeTurret ce => ce.HudAmmo,
                VanillaTurret vanilla => vanilla.HudAmmo,
                _ => default
            };
            float width = Math.Min(310f, UI.screenWidth - 24f);
            if (width < 180f) return;
            float height = ammo.Reload.HasValue ? 81f : 57f;
            float y = UI.screenHeight * .35f - height * .5f;
            GameFont font = Text.Font;
            TextAnchor anchor = Text.Anchor;
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.UpperLeft;
            try
            {
                Widgets.DrawBoxSolid(new Rect(12f, y, width, height), CombatHudCard.Background);
                CombatHudCard.Weapon(new Rect(17f, y + 5f, width - 10f, 46f), thing.LabelCap.ToString(), thing.def.uiIcon, ammo, false, false);
                if (ammo.Reload is float value)
                    CombatHudCard.Reload(new Rect(20f, y + 57f, width - 16f, 18f), value);
            }
            finally { Text.Font = font; Text.Anchor = anchor; }
        }

        private static void Label(Rect r, string text, Color color, GameFont font)
        {
            GameFont f0 = Text.Font; TextAnchor a0 = Text.Anchor; Color c0 = GUI.color;
            Text.Font = font; Text.Anchor = TextAnchor.UpperCenter; GUI.color = color;
            Widgets.Label(r, text);
            Text.Font = f0; Text.Anchor = a0; GUI.color = c0;
        }
    }

    // Built-in adapter for vanilla gun turrets. While operated, the turret's own target search answers with the
    // crosshair's target (only while the trigger is held) and its idle turning stops; bursts, warmup, cooldown,
    // barrel wear, power and hold-fire stay the game's.
    internal sealed class VanillaTurret : IArtilleryTurret, ISelectableFireMode, IRemoteManualTurret
    {
        private const int ReloadTicks = 60;
        private const float ShellReach = 2.9f;
        private static AccessTools.FieldRef<Building_TurretGun, int> cooldown, warmup;
        private static AccessTools.FieldRef<Building_TurretGun, bool> holdFire;
        private static AccessTools.FieldRef<Building_TurretGun, CompMannable> mannable;
        private static AccessTools.FieldRef<Verb, int> burstLeft;
        private static System.Reflection.MethodInfo startCooldown;
        private static readonly TurretFireMode[] RocketModes = { TurretFireMode.Single, TurretFireMode.Burst3, TurretFireMode.Auto };
        private static TurretFireMode rocketMode = TurretFireMode.Single;
        internal static VanillaTurret Operated;
        private readonly Building_TurretGun gun;
        private LocalTargetInfo pending = LocalTargetInfo.Invalid;
        private Pawn operatorPawn;
        private int reloadDone = -1;
        private int retryReloadAt;
        private Thing reloadFrom;
        private static ThingDef lastShell;
        private bool triggerHeld, firedHere;
        private int shotsThisPull;

        public VanillaTurret(Building_TurretGun gun) { this.gun = gun; }

        public static void Bind()
        {
            cooldown = AccessTools.FieldRefAccess<Building_TurretGun, int>("burstCooldownTicksLeft");
            warmup = AccessTools.FieldRefAccess<Building_TurretGun, int>("burstWarmupTicksLeft");
            holdFire = AccessTools.FieldRefAccess<Building_TurretGun, bool>("holdFire");
            mannable = AccessTools.FieldRefAccess<Building_TurretGun, CompMannable>("mannableComp");
            burstLeft = AccessTools.FieldRefAccess<Verb, int>("burstShotsLeft");
            startCooldown = AccessTools.Method(typeof(CompInteractable), "StartCooldown") ?? throw new MissingMethodException("CompInteractable.StartCooldown");
        }

        // ---- rocket launchers: fuel per shot, fired on activation (rocketswarm)

        private CompInteractable Activation => gun.TryGetComp<CompInteractable>();
        // Rocket launchers only: 1.7's autocannon and mini-turret also burn barrel "fuel" per shot in bursts, but fire
        // ordinary bullets and stay direct-fire turrets.
        private bool Rocket => gun is Building_TurretRocket || (Verb.verbProps.consumeFuelPerShot > 0f && Verb.verbProps.burstShotCount > 1
            && Verb.verbProps.defaultProjectile?.thingClass != null && !typeof(Bullet).IsAssignableFrom(Verb.verbProps.defaultProjectile.thingClass));
        private int RocketsLeft
        {
            get
            {
                float per = Verb.verbProps.consumeFuelPerShot;
                return per > 0f && gun.refuelableComp != null ? (int)(gun.refuelableComp.Fuel / per + .001f) : int.MaxValue;
            }
        }

        // Powered, awake and not cooling off: what Active means, without the one-tick activation an interactable
        // launcher needs (the trigger activates it).
        private bool Running
        {
            get
            {
                CompInteractable act = Activation;
                if (act == null) return gun.Active;
                CompPowerTrader power = gun.TryGetComp<CompPowerTrader>();
                return (power == null || power.PowerOn) && !act.OnCooldown && !(gun.TryGetComp<CompMechPowerCell>()?.depleted ?? false);
            }
        }

        public IReadOnlyList<TurretFireMode> FireModes => Rocket ? RocketModes : Array.Empty<TurretFireMode>();
        public TurretFireMode FireMode { get => rocketMode; set => rocketMode = value; }

        // Before each shot of an operated turret's burst: the burst is cut to the fire mode, the rack and the trigger.
        public static void ShapeBurst(Verb verb)
        {
            VanillaTurret t = Operated;
            if (t == null || !t.OwnsVerb(verb) || !t.Rocket) return;
            ref int left = ref burstLeft(verb);
            // Shots left counting this one (which fires regardless): Auto keeps the burst open while the trigger is held.
            int want = rocketMode == TurretFireMode.Single ? 1 - t.shotsThisPull
                : rocketMode == TurretFireMode.Burst3 ? 3 - t.shotsThisPull
                : t.triggerHeld ? Math.Max(left, 2) : 1;
            left = Math.Max(1, Math.Min(want, t.RocketsLeft));
            t.shotsThisPull++;
            t.firedHere = true;
        }

        public static void AfterFindTarget(Building_TurretGun __instance, ref LocalTargetInfo __result)
        {
            if (Operated != null && Operated.gun == __instance) __result = Operated.pending;
            else if (TargetMark.ForTurret(__instance, out LocalTargetInfo marked)) __result = marked;   // 0.9.0: the marked target first
        }
        public static bool BeforeTopTick(Building_Turret ___parentTurret) => Operated == null || Operated.gun != ___parentTurret;

        // The operator counts as the crew of a mannable turret (mortars) for as long as they operate it: the game then
        // ticks it, and the operator's shooting sets the shell's spread as for any crew.
        public static void AfterMannedNow(CompMannable __instance, ref bool __result)
        { if (!__result && Operated != null && Operated.gun == __instance.parent && Operated.operatorPawn?.Spawned == true) __result = true; }
        public static void AfterManningPawn(CompMannable __instance, ref Pawn __result)
        { if (Operated != null && Operated.gun == __instance.parent && Operated.operatorPawn?.Spawned == true) __result = Operated.operatorPawn; }

        internal Verb Verb => gun.AttackVerb;
        public Thing Thing => gun;
        public TurretAimMode AimMode => gun.def.building.IsMortar || Verb.ProjectileFliesOverhead() || Rocket ? TurretAimMode.Artillery : TurretAimMode.Direct;
        private int Size => Math.Max(gun.def.size.x, gun.def.size.z);
        public float EyeHeight => 1.15f + .25f * (Size - 1);
        public float TurnRate => Size >= 2 ? 180f : 360f;
        public float Range => Verb.EffectiveRange;
        public float MinRange => Verb.verbProps.minRange;
        public float Rotation => gun.Top.CurRotation;
        public void Aim(float r) => gun.Top.CurRotation = r;

        public string Refusal(Pawn by)
        {
            if (!gun.Spawned || gun.Faction != Faction.OfPlayer) return L.T("这不是己方炮台。", "This is not your turret.");
            if (Operated != null && Operated != this && Operated.gun == gun) return L.T("这座炮台已有人操纵。", "Someone is already manning this turret.");
            Pawn crew = mannable(gun)?.ManningPawn;
            if (crew != null && crew != by) return crew.LabelShort + L.T(" 正在值守这座炮台。", " is manning this turret.");
            if (Shells != null && !Shells.Loaded && FindShell(by) == null) return gun.LabelCap + L.T(" 没有装填，周围两格和身上也没有可用炮弹。", " is not loaded, and there are no shells within two cells or on the pawn.");
            if (RocketsLeft <= 0) return gun.LabelCap + L.T(" 火箭已打光，需要小人补充化学燃料。", " is out of rockets; a pawn must refill it with chemfuel.");
            CompInteractable act = Activation;
            if (act != null && act.OnCooldown) return gun.LabelCap + L.T(" 正在冷却。", " is cooling down.");
            return null;
        }

        public string RemoteRefusal
        {
            get
            {
                if (AimMode != TurretAimMode.Direct || mannable(gun) != null)
                    return L.T("需要操作员的炮台和炮兵武器仍需小人操作。", "Crewed turrets and artillery still require an operator.");
                if (!TurretControl.DirectProjectile(Verb))
                    return L.T("这类炮台尚未适配远程直射弹道。", "This turret's projectile is not supported by remote direct fire yet.");
                if (!Running) return L.T("炮台未运作：检查供电或休眠状态。", "Turret not working: check power or dormancy.");
                if (Shells != null && !Shells.Loaded) return L.T("请先让小人装填炮台。", "Have a pawn load the turret first.");
                return Refusal(null);
            }
        }

        // ---- shells (mortars and other changeable-projectile guns)

        private CompChangeableProjectile Shells => gun.gun?.TryGetComp<CompChangeableProjectile>();
        // A rocket rack counts as loaded while it holds a rocket's worth of fuel.
        public bool Loaded => (CeCompat.TurretLoaded(Verb)??(Shells == null || Shells.Loaded)) && RocketsLeft > 0;
        public bool Reloading => reloadDone >= 0;
        internal float? ReloadProgress => Reloading ? Mathf.Clamp01(1f - (reloadDone - Find.TickManager.TicksGame) / (float)ReloadTicks) : null;
        internal CombatHudCard.Ammo HudAmmo => new CombatHudCard.Ammo(
            Shells?.LoadedShell ?? lastShell ?? reloadFrom?.def ?? ShellProjectile,
            Shells != null ? (Shells.Loaded ? 1 : 0) : RocketsLeft,
            Shells != null ? 1 : Rocket && gun.refuelableComp != null
                ? Mathf.FloorToInt(gun.refuelableComp.Props.fuelCapacity / Verb.verbProps.consumeFuelPerShot) : 0,
            null, ReloadProgress);

        private bool Accepts(Thing t)
        {
            CompChangeableProjectile s = Shells;
            if (t?.def?.projectileWhenLoaded == null || s == null) return false;
            StorageSettings fixedSettings = s.GetParentStoreSettings();
            return (fixedSettings == null || fixedSettings.AllowedToAccept(t)) && s.allowedShellsSettings.AllowedToAccept(t);
        }

        // Carried shells first, then the nearest stack within two cells of the turret; the last shell type is preferred.
        private Thing FindShell(Pawn by)
        {
            Thing best = null; float bestScore = float.MaxValue;
            if (by?.inventory?.innerContainer != null)
                foreach (Thing t in by.inventory.innerContainer)
                    if (Accepts(t)) { float s = t.def == lastShell ? -2f : -1f; if (s < bestScore) { best = t; bestScore = s; } }
            if (best != null || !gun.Spawned) return best;
            Map map = gun.Map;
            CellRect around = gun.OccupiedRect().ExpandedBy(2);
            foreach (IntVec3 c in around)
            {
                if (!c.InBounds(map) || (gun.OccupiedRect().ClosestCellTo(c) - c).LengthHorizontal > ShellReach) continue;
                foreach (Thing t in c.GetThingList(map))
                {
                    if (!Accepts(t) || t.IsForbidden(by)) continue;
                    float s = (c - gun.Position).LengthHorizontalSquared + (t.def == lastShell ? 0f : 1000f);
                    if (s < bestScore) { best = t; bestScore = s; }
                }
            }
            return best;
        }

        public string Reload(Pawn by)
        {
            if (RocketsLeft <= 0) return L.T("火箭已打光，需要小人补充化学燃料。", "Out of rockets; a pawn must refill it with chemfuel.");
            CompChangeableProjectile s = Shells;
            if (s == null || s.Loaded || Reloading) return null;
            Thing from = FindShell(by);
            if (from == null) return L.T("周围两格和身上都没有可用炮弹。", "No shells within two cells or on the pawn.");
            reloadFrom = from; reloadDone = Find.TickManager.TicksGame + ReloadTicks;
            return null;
        }

        // Called every frame while operated: completes a reload whose time has passed.
        private void StepReload()
        {
            if (!Reloading || Find.TickManager.TicksGame < reloadDone) return;
            Thing from = reloadFrom;
            reloadDone = -1; reloadFrom = null;
            CompChangeableProjectile s = Shells;
            bool carried = from != null && operatorPawn?.inventory?.innerContainer.Contains(from) == true;
            bool near = from != null && from.Spawned && from.Map == gun.Map && (gun.OccupiedRect().ClosestCellTo(from.Position) - from.Position).LengthHorizontal <= ShellReach;
            if (s == null || s.Loaded || from == null || from.Destroyed || (!carried && !near) || !Accepts(from)) return;
            ThingDef def = from.def;
            from.SplitOff(1).Destroy();
            s.LoadShell(def, 1);
            lastShell = def;
            Log.Message("[FpsBridge] TURRET_RELOAD thing=" + gun.ThingID + "; shell=" + def.defName + "; carried=" + carried);
        }

        public string AmmoLabel(Pawn by)
        {
            CompChangeableProjectile s = Shells;
            if (s == null)
            {
                if (!Rocket || gun.refuelableComp == null) return null;
                int max = (int)(gun.refuelableComp.Props.fuelCapacity / Verb.verbProps.consumeFuelPerShot + .001f);
                return L.T("火箭 ", "Rockets ") + RocketsLeft + " / " + max + L.T("（补充需小人添加化学燃料）", " (a pawn refills it with chemfuel)");
            }
            int carried = 0, near = 0;
            if (by?.inventory?.innerContainer != null)
                foreach (Thing t in by.inventory.innerContainer) if (Accepts(t)) carried += t.stackCount;
            if (gun.Spawned)
                foreach (IntVec3 c in gun.OccupiedRect().ExpandedBy(2))
                    if (c.InBounds(gun.Map) && (gun.OccupiedRect().ClosestCellTo(c) - c).LengthHorizontal <= ShellReach)
                        foreach (Thing t in c.GetThingList(gun.Map)) if (Accepts(t)) near += t.stackCount;
            string loaded = s.Loaded ? s.LoadedShell.LabelCap.ToString() : Reloading ? L.T("装填中…", "Loading…") : L.T("未装填", "Not loaded");
            return loaded + L.T(" · 身上 ", " · carried ") + carried + L.T(" · 附近 ", " · nearby ") + near;
        }

        // ---- artillery numbers, as the game computes them

        internal ThingDef ShellProjectile
        {
            get
            {
                ThingDef p = CeCompat.ProjectileFor(Verb)??(Verb as Verb_LaunchProjectile)?.Projectile;
                if (p != null) return p;
                Thing next = FindShell(operatorPawn);
                return next?.def.projectileWhenLoaded ?? lastShell?.projectileWhenLoaded;
            }
        }

        private float WarmupTicks => gun.def.building.turretBurstWarmupTime.Average.SecondsToTicks();

        public float FlightTicks(IntVec3 target)
        {
            ThingDef p = ShellProjectile;
            if (p?.projectile == null || p.projectile.SpeedTilesPerTick <= 0f) return -1f;
            float dist = (target.ToVector3Shifted() - gun.DrawPos).MagnitudeHorizontal();
            return (OwnFire ? 0f : WarmupTicks) + Mathf.Ceil(dist / p.projectile.SpeedTilesPerTick);   // own trigger: no warm-up
        }

        public float Spread(IntVec3 target, Pawn by)
        {
            VerbProperties v = Verb.verbProps;
            float r = v.ForcedMissRadius;
            if (r <= .5f) return 0f;
            // As in Verb_LaunchProjectile: only a crew's shooting changes the spread.
            if (by != null && mannable(gun) != null) r *= v.GetForceMissFactorFor(gun, by);
            return VerbUtility.CalculateAdjustedForcedMiss(r, target - gun.Position);
        }

        public float BlastRadius => ShellProjectile?.projectile?.explosionRadius ?? 0f;

        public string Status
        {
            get
            {
                CompInteractable act = Activation;
                if (act != null && act.OnCooldown) return act.CanInteract().Reason;
                if (!Running) return L.T("未运作（断电/休眠）", "Not working (no power / dormant)");
                if (Rocket && RocketsLeft <= 0) return L.T("火箭已打光", "Out of rockets");
                if (Verb.ProjectileFliesOverhead() && gun.Position.Roofed(gun.Map)) return L.T("头顶有屋顶，无法开火", "Roofed over, cannot fire");
                if (Reloading) return L.T("装填中", "Loading");
                if (!Loaded) return L.T("未装填（R 装填）", "Not loaded (R to load)");
                if (holdFire(gun)) return L.T("停火中（在炮台上取消停火）", "Hold fire (cancel it on the turret)");
                if (OwnFire)
                {
                    Heat h = H;
                    return h.heat.Locked ? L.T("过热", "Overheated") : Find.TickManager.TicksGame < h.nextTick ? L.T("冷却 ", "Cooldown ") + (h.nextTick - Find.TickManager.TicksGame).ToStringSecondsFromTicks() : L.T("就绪", "Ready");
                }
                if (Verb.state == VerbState.Bursting) return L.T("开火", "Firing");
                if (warmup(gun) > 0) return L.T("预热", "Warming up");
                int cd = cooldown(gun);
                return cd > 0 ? L.T("冷却 ", "Cooldown ") + cd.ToStringSecondsFromTicks() : L.T("就绪", "Ready");
            }
        }

        public float? Ammo => gun.refuelableComp != null ? gun.refuelableComp.FuelPercentOfMax : (float?)null;

        // ---- 0.9.0 own trigger (every operated turret but a rocket launcher): the shot leaves on the click, paced and
        // heated as a gun in hand: burst turrets have single / three-round / auto (B) at their burst rhythm, single-shot
        // turrets and mortars fire once per click at most once per their cool-down; 100 heat, +1 a shot, a full bar locks
        // the trigger until it has drained (in the turret's own cool-down time when not firing).
        private const int HeatCapacity = 100;
        private sealed class Heat { public HeatState heat; public int interval, coolTicks, nextTick; public bool burst; public readonly SemiAutoBuffer trigger = new SemiAutoBuffer(); }
        private static readonly Dictionary<int, Heat> heats = new Dictionary<int, Heat>();
        private static AccessTools.FieldRef<Verb, LocalTargetInfo> verbTarget;

        private bool OwnFire => !Rocket;
        private bool DirectRound => AimMode == TurretAimMode.Direct && TurretControl.DirectProjectile(Verb);

        private Heat H
        {
            get
            {
                if (heats.TryGetValue(gun.thingIDNumber, out Heat h)) return h;
                VerbProperties v = Verb.verbProps;
                float coolSeconds = gun.def.building.turretBurstCooldownTime >= 0f ? gun.def.building.turretBurstCooldownTime : v.defaultCooldownTime;
                h = new Heat { heat = new HeatState(Find.TickManager.TicksGame), burst = v.burstShotCount > 1, coolTicks = Math.Max(1, coolSeconds.SecondsToTicks()) };
                h.interval = h.burst ? Math.Max(1, v.ticksBetweenBurstShots) : Math.Max(4, h.coolTicks);
                if (!h.burst) h.heat.Mode = HSK.FpsBridge.FireMode.Single;
                heats[gun.thingIDNumber] = h;
                return h;
            }
        }

        public bool HasModes => OwnFire && H.burst;
        public void CycleHeatMode()
        {
            if (!HasModes) { Messages.Message(gun.LabelCap + L.T(" 只能单发。", " fires single shots only."), MessageTypeDefOf.RejectInput, false); return; }
            H.heat.Cycle(); H.trigger.Clear();
            Messages.Message(L.T("射击模式：", "Fire mode: ") + HeatState.Label(H.heat.Mode), MessageTypeDefOf.SilentInput, false);
        }

        private void FireOwn(LocalTargetInfo target, bool held, bool pressed)
        {
            Heat h = H;
            int now = Find.TickManager.TicksGame;
            int interval = CooldownRules.ShotInterval(h.interval, h.burst, h.heat.Mode, FpsMod.Settings.cooldownMultiplier);
            h.heat.Advance(now, HeatCapacity, CooldownRules.Scale(h.coolTicks, FpsMod.Settings.cooldownMultiplier), interval);
            if (!h.heat.CanFire(HeatCapacity) || !Running || !Loaded || Reloading || holdFire(gun)
                || (Verb.ProjectileFliesOverhead() && gun.Position.Roofed(gun.Map))) { h.heat.ClearQueue(); h.trigger.Clear(); return; }
            bool single = !h.burst || h.heat.Mode == HSK.FpsBridge.FireMode.Single;
            bool wants = single ? h.trigger.Wants(now, h.nextTick, pressed) : HSK.FpsBridge.Trigger.Wants(h.heat, h.burst, pressed, held);
            int fired = 0;
            while (wants && now >= h.nextTick && h.heat.CanFire(HeatCapacity) && fired < 4)
            {
                if (!Verb.Available() || !Shoot(target)) { h.heat.ClearQueue(); h.trigger.Clear(); return; }
                h.heat.Shot(now, HeatCapacity);
                h.heat.Consume();
                h.trigger.Clear();
                h.nextTick = HSK.FpsBridge.Trigger.NextShotTick(now, interval);
                fired++;
                wants = h.burst && (h.heat.Mode == HSK.FpsBridge.FireMode.Automatic ? held : h.heat.Mode == HSK.FpsBridge.FireMode.Burst3 && h.heat.Queued > 0) && Loaded;
            }
        }

        // One shot: a direct-fire round along the view, or the game's own shell at the aimed cell (its spread, the crew's
        // skill, the shell used up). Flash, sound and barrel wear as the game's burst step does them.
        private bool Shoot(LocalTargetInfo target)
        {
            // Spray verbs build their per-shot path during native warmup.
            if (Verb is Verb_Spray) return target.IsValid && Verb.TryStartCastOn(target);
            bool ok;
            if(CeCompat.Owns(Verb))ok=CeCompat.TurretShot(gun,operatorPawn,Verb,target);
            else if (DirectRound)
            {
                V3 start = TurretControl.Muzzle(out V3 dir);
                var direct = (Verb_LaunchProjectile)Verb;
                ok = Ballistics.FireTurretRound(RemoteTurret.On ? gun : operatorPawn, direct, start, TurretControl.ManualShotDirection(operatorPawn, dir, direct.Projectile));
            }
            else
            {
                float dist = target.IsValid ? (target.Cell - gun.Position).LengthHorizontal : -1f;
                if (!target.IsValid || dist < MinRange || dist > Range) return false;
                verbTarget ??= AccessTools.FieldRefAccess<Verb, LocalTargetInfo>("currentTarget");
                verbTarget(Verb) = target;
                ok = (bool)AccessTools.Method(Verb.GetType(), "TryCastShot", Type.EmptyTypes).Invoke(Verb, null);
            }
            if (!ok) return false;
            if (DirectRound&&!CeCompat.Owns(Verb)) Shells?.Notify_ProjectileLaunched();
            VerbProperties v = Verb.verbProps;
            if (v.muzzleFlashScale > .01f)
            {
                if (View3D.Active && AerialCombat.Target(target, out _))
                {
                    V3 muzzle = TurretControl.Muzzle(out V3 direction);
                    AerialTurretFire.Record(gun, muzzle, direction);
                }
                else FleckMaker.Static(gun.Position, gun.Map, FleckDefOf.ShotFlash, v.muzzleFlashScale);
            }
            v.soundCast?.PlayOneShot(new TargetInfo(gun.Position, gun.Map));
            v.soundCastTail?.PlayOneShotOnCamera(gun.Map);
            if (v.consumeFuelPerShot > 0f) gun.refuelableComp?.ConsumeFuel(v.consumeFuelPerShot);
            return true;
        }

        // The heat bar and mode under the crosshair (own trigger only).
        public void DrawHeat(float cx, float y)
        {
            if (!OwnFire) return;
            Heat h = H;
            h.heat.Advance(Find.TickManager.TicksGame, HeatCapacity, CooldownRules.Scale(h.coolTicks, FpsMod.Settings.cooldownMultiplier),
                CooldownRules.ShotInterval(h.interval, h.burst, h.heat.Mode, FpsMod.Settings.cooldownMultiplier));
            GameFont f0 = Text.Font; TextAnchor a0 = Text.Anchor; Color c0 = GUI.color;
            try
            {
                Text.Font = GameFont.Tiny; Text.Anchor = TextAnchor.UpperCenter; GUI.color = new Color(1f, 1f, 1f, .85f);
                Widgets.Label(new Rect(cx - 120f, y, 240f, 20f), (h.burst ? HeatState.Label(h.heat.Mode) + "  [B]" : L.T("单发", "Single")) + L.T("  · 热量 ", "  · heat ") + (int)h.heat.Heat + "/" + HeatCapacity);
                float w = 160f, fill = (float)(h.heat.Heat / HeatCapacity);
                Rect bar = new Rect(cx - w * .5f, y + 20f, w, 6f);
                Widgets.DrawBoxSolid(bar, new Color(0f, 0f, 0f, .55f));
                Color c = h.heat.Locked ? new Color(1f, .2f, .15f, .95f) : Color.Lerp(new Color(1f, .85f, .3f, .9f), new Color(1f, .35f, .1f, .95f), fill);
                Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, w * Mathf.Clamp01(fill), 6f), c);
                if (h.heat.Locked) { GUI.color = new Color(1f, .4f, .3f); Widgets.Label(new Rect(cx - 120f, y + 28f, 240f, 20f), L.T("过热 · 冷却中", "Overheated · cooling")); }
            }
            finally { Text.Font = f0; Text.Anchor = a0; GUI.color = c0; }
        }

        public void Trigger(LocalTargetInfo target, bool held, bool pressed)
        {
            if (RatkinTurretShots.IsTurret(gun.def.defName) && !Loaded && !Reloading && Find.TickManager.TicksGame >= retryReloadAt)
            { retryReloadAt = Find.TickManager.TicksGame + 60; Reload(operatorPawn); }
            StepReload();
            triggerHeld = held;
            if (OwnFire) { pending = LocalTargetInfo.Invalid; FireOwn(target, held, pressed); return; }
            // Single and three-round bursts start on a press; everything else fires for as long as the trigger is held.
            bool want = Rocket && rocketMode != TurretFireMode.Auto ? pressed : held;
            float dist = target.IsValid ? (target.Cell - gun.Position).LengthHorizontal : -1f;
            pending = want && target.IsValid && dist >= MinRange && dist <= Range ? target : LocalTargetInfo.Invalid;
            if (pending.IsValid && Running && Loaded && !Reloading && Verb.state != VerbState.Bursting && warmup(gun) <= 0 && cooldown(gun) <= 0)
            {
                shotsThisPull = 0;
                if (Activation != null) gun.TryActivateBurst(); else gun.TryStartShootSomething(true);
            }
            // A rack emptied by hand cools off as after the game's own activation.
            if (firedHere && RocketsLeft <= 0 && Verb.state != VerbState.Bursting)
            {
                firedHere = false;
                CompInteractable act = Activation;
                if (act != null && !act.OnCooldown) startCooldown.Invoke(act, null);
            }
        }

        internal void CancelTrigger()
        {
            pending = LocalTargetInfo.Invalid;
            triggerHeld = false;
            if (OwnFire) { H.heat.ClearQueue(); H.trigger.Clear(); }
        }

        public bool OwnsVerb(Verb verb) => verb != null && verb.caster == gun;

        public void Begin(Pawn by)
        {
            // Do not inherit the AI's unfinished burst when the player takes the trigger.
            Verb.Reset();
            warmup(gun) = 0;
            Operated = this;
            operatorPawn = by;
            reloadDone = -1; reloadFrom = null;
            pending = LocalTargetInfo.Invalid;
            triggerHeld = firedHere = false; shotsThisPull = 0;
            gun.OrderAttack(LocalTargetInfo.Invalid);   // drops a forced target; the crosshair decides from now on
        }

        public void End()
        {
            CancelTrigger();
            Verb.Reset();
            if (firedHere && RocketsLeft <= 0 && Activation is CompInteractable act && !act.OnCooldown) startCooldown.Invoke(act, null);
            firedHere = false;
            pending = LocalTargetInfo.Invalid;
            reloadDone = -1; reloadFrom = null; operatorPawn = null;
            if (Operated == this) Operated = null;
        }
    }
}
