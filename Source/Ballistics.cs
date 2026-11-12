using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace HSK.FpsBridge
{
    // First-person 3D flight (R3 rounds, R4 gravity, tracers, impact light and thrown grenades).
    // Only projectiles the bridge fires for its own first-person pawn are changed. The game's projectile object is kept
    // (graphic, sound, impact, damage); its flight, contacts and target are ours:
    //  - rounds leave the eye exactly along the view, fall under game gravity and stop at the first volume they cross
    //    (body part, solid building, tree trunk, ground, roof) or when they leave the map; no range or time limit;
    //  - grenades leave the hand along the view at a speed that reaches the weapon's range at 45 degrees, bounce once
    //    off a wall and land; the game's own fuse and explosion follow.
    internal static partial class Ballistics
    {
        private enum Kind { Round, Grenade, Beam }

        private sealed class BeamFlash
        {
            public Map map;
            public V3 start, end;
            public float until;
        }
        private static readonly List<BeamFlash> beams = new List<BeamFlash>();

        private sealed class Shot
        {
            public Kind kind;
            public V3 pos, vel;
            public float gravity, startY, muzzleY, flown;
            public int lastTick, bounces;
            public int lastAirDrawFrame = -1;
            public bool airLaunched, explosiveGun;
            public Thing launcher;
            public Thing aerialTarget;
            public float aerialTurn;
            public Pawn homing;   // Death Eye: steered onto this pawn's head every tick
            public RatkinFlight ratkin;
            // 0.6.0 penetration (null: the round stops at its first contact as before).
            public PenetrationState pen;
            public Dictionary<long, HitVolume> envelopes;
            public Dictionary<long, BattleLogEntry_RangedImpact> logs;   // one combat log entry per target per round
        }

        private static readonly Dictionary<Projectile, Shot> shots = new Dictionary<Projectile, Shot>();
        private static readonly List<HitVolume> volumes = new List<HitVolume>();
        private static readonly HashSet<Thing> seen = new HashSet<Thing>();

        private static AccessTools.FieldRef<Projectile, Vector3> originRef, destinationRef;
        private static AccessTools.FieldRef<Projectile, int> ticksRef;
        private static MethodInfo impact;
        private static Thing pendingThing;
        private static BodyPartRecord pendingPart;

        public static bool Enabled { get; private set; }
        public static string LastHit { get; private set; }
        public static float LastHitUntil { get; private set; }

        public static void Install(Harmony harmony)
        {
            originRef = AccessTools.FieldRefAccess<Projectile, Vector3>("origin");
            destinationRef = AccessTools.FieldRefAccess<Projectile, Vector3>("destination");
            ticksRef = AccessTools.FieldRefAccess<Projectile, int>("ticksToImpact");
            impact = AccessTools.DeclaredMethod(typeof(Projectile), "Impact", new[] { typeof(Thing), typeof(bool) })
                ?? throw new MissingMethodException("Projectile.Impact(Thing,bool)");
            var t = typeof(Ballistics);
            // Beam.Launch immediately calls ImpactSomething (2D) and destroys itself. Initialize that
            // same Beam with Projectile's original base method instead; only our controlled shot uses it.
            MethodInfo launch = Bindings.Method(typeof(Projectile), "Launch", false, typeof(Thing), typeof(Vector3),
                typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef));
            harmony.CreateReversePatcher(launch, new HarmonyMethod(t, nameof(LaunchBase))).Patch(HarmonyReversePatchType.Original);
            MethodInfo tick = Bindings.Method(typeof(Projectile), "Tick", false);
            MethodInfo tickInterval = Bindings.Method(typeof(Projectile), "TickInterval", false, typeof(int));
            MethodInfo exact = AccessTools.PropertyGetter(typeof(Projectile), "ExactPosition") ?? throw new MissingMethodException("Projectile.ExactPosition");
            MethodInfo arc = AccessTools.PropertyGetter(typeof(Projectile), "ArcHeightFactor") ?? throw new MissingMethodException("Projectile.ArcHeightFactor");
            MethodInfo damage = AccessTools.DeclaredMethod(typeof(Thing), "TakeDamage", new[] { typeof(DamageInfo) }) ?? throw new MissingMethodException("Thing.TakeDamage");
            MethodInfo drawnHeight = AccessTools.DeclaredMethod(typeof(Projectiles), "Height", new[] { typeof(Projectile), typeof(Vector3) })
                ?? throw new MissingMethodException("IsoRim.Projectiles.Height");
            MethodInfo drawAt = AccessTools.DeclaredMethod(typeof(Projectiles), "DrawAt", new[] { typeof(Projectile), typeof(Vector3) })
                ?? throw new MissingMethodException("IsoRim.Projectiles.DrawAt");
            harmony.Patch(tick, prefix: new HarmonyMethod(t, nameof(BeforeTick)) { priority = Priority.First });
            harmony.Patch(tickInterval, prefix: new HarmonyMethod(t, nameof(BeforeTick)) { priority = Priority.First });
            harmony.Patch(exact, prefix: new HarmonyMethod(t, nameof(ExactPosition)) { priority = Priority.First });
            harmony.Patch(arc, prefix: new HarmonyMethod(t, nameof(NoArc)) { priority = Priority.First });
            harmony.Patch(damage, prefix: new HarmonyMethod(t, nameof(BeforeDamage)) { priority = Priority.First });
            harmony.Patch(drawnHeight, prefix: new HarmonyMethod(t, nameof(DrawnHeight)) { priority = Priority.First });
            harmony.Patch(drawAt, postfix: new HarmonyMethod(t, nameof(AfterDrawAt)) { priority = Priority.Last });
            harmony.Patch(drawAt, prefix: new HarmonyMethod(t, nameof(BeforeDrawAt)));
            harmony.Patch(AccessTools.Method(typeof(Projectile),"ExposeData"),postfix:new HarmonyMethod(t,nameof(ExposeAerialMissile)));
            InstallNativeFlights(harmony, launch);
            InstallPenetration(harmony);
            Enabled = true;
        }

        public static void Disable() { Enabled = false; Reset(); }

        public static void Reset()
        {
            pendingThing = null; pendingPart = null;
            shots.Clear(); beams.Clear(); BodyRegions.Clear();
        }

        // ---- eligibility

        // Ordinary rounds (Bullet and subclasses, not lobbed): fired by FireControl along the view.
        public static bool IsRound(Verb verb)
        {
            if (!Enabled || verb == null || verb.verbProps.IsMeleeAttack) return false;
            ThingDef projectile = (verb as Verb_LaunchProjectile)?.Projectile;
            return projectile?.projectile != null && !projectile.projectile.flyOverhead && typeof(Bullet).IsAssignableFrom(projectile.thingClass);
        }

        // Hand-thrown grenades: the plain launch verb (launchers use Verb_Shoot) with a projectile.
        public static bool IsGrenade(Verb verb)
        {
            if (!Enabled || verb == null || verb.verbProps.IsMeleeAttack) return false;
            return verb.verbProps.verbClass == typeof(Verb_LaunchProjectile) && (verb as Verb_LaunchProjectile)?.Projectile?.projectile != null;
        }

        // ---- firing

        public static bool IsBeam(Verb verb) => (verb as Verb_LaunchProjectile)?.Projectile?.thingClass == typeof(Beam);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void LaunchBase(Projectile instance, Thing launcher, Vector3 origin, LocalTargetInfo usedTarget,
            LocalTargetInfo intendedTarget, ProjectileHitFlags hitFlags, bool preventFriendlyFire, Thing equipment, ThingDef targetCoverDef)
            => throw new NotImplementedException("Projectile.Launch reverse patch was not installed");

        // Native Beam (Odyssey's pulse repeater) is instantaneous, with no bullet drop or flight-speed setting.
        // Keep its real object/weapon/DamageDef so Bullet.Impact retains shield bypass, quality and damage rules.
        private static bool FireBeam(Pawn pawn, Verb_LaunchProjectile verb, V3 start, V3 direction, LocalTargetInfo intended)
        {
            Map map = pawn.Map;
            ThingDef def = verb.Projectile;
            if (!BeamGeometry.End(start, direction, verb.verbProps.range, map.Size.x, FloorSpace.Bands(map)[0].Rows, out V3 end)) return false;
            var shot = new Shot { kind = Kind.Beam, launcher = pawn, pos = start, vel = direction };
            if (PenetrationGroupOf(verb) != PenetrationGroup.Legacy) return FirePenetratingBeam(pawn, verb, shot, start, end, intended);
            bool contact = Trace(map, shot, start, end, out float fraction, out Thing thing, out BodyPartRecord part, out HitRegion region, out _);
            V3 at = contact ? V3.Lerp(start, end, fraction) : end;
            IntVec3 cell = FloorSpace.ContactCell(map, at, direction.Y, thing);
            if (!cell.InBounds(map)) return false;
            Projectile p = (Projectile)GenSpawn.Spawn(def, pawn.Position, map, WipeMode.Vanish);
            try
            {
                Vector3 origin = FloorSpace.Logical(map, start, cell); origin.y = def.Altitude;
                LaunchBase(p, pawn, origin, new LocalTargetInfo(cell), intended, ProjectileHitFlags.None, false, verb.EquipmentSource, null);
                FloorSpace.OwnFlight(p);
                shot.pos = at;
                shots[p] = shot;
                V3 visualStart = Hand(pawn);
                // Keep the muzzle streak in front of a close obstruction; damage still follows the sight ray.
                if ((at - start).Length < .5f) visualStart = start;
                beams.Add(new BeamFlash { map = map, start = visualStart, end = at, until = Time.unscaledTime + .12f });
                if (contact) Hit(p, shot, thing, part, region);
                return true;
            }
            finally
            {
                shots.Remove(p);
                if (!p.Destroyed) p.Destroy(DestroyMode.Vanish);
            }
        }

        public static void DrawBeams(Map map)
        {
            float now = Time.unscaledTime;
            for (int i = beams.Count - 1; i >= 0; i--)
            {
                BeamFlash beam = beams[i];
                if (beam.until <= now) { beams.RemoveAt(i); continue; }
                if (beam.map == map) Visuals.BeamLine(beam.start, beam.end, Mathf.Clamp01((beam.until - now) / .12f));
            }
        }

        // One round from the eye along the current view (sway and kick included). Sound, flash and record as the game's.
        public static bool FireRound(Pawn pawn, Verb_LaunchProjectile verb, LocalTargetInfo intended)
        {
            ThingDef def = verb.Projectile;
            if (def?.projectile == null || pawn.Map == null) return false;
            V3 eye = Eye(pawn), dir = ControlCamera.ShotDirection(pawn);
            if (IsBeam(verb))
            {
                if (!FireBeam(pawn, verb, eye, dir, intended)) return false;
            }
            else
            {
                // (In the Sandevistan's slowed world the round flies, and falls, at the world's pace: slowly.)
                float speed = ViewRules.Speed(BulletSpeeds.ForWeapon(verb.EquipmentSource?.def.defName, def.projectile.SpeedTilesPerTick), FpsMod.Settings.speed);
                float gravity = ViewRules.Gravity(FpsMod.Settings.gravity);
                // Death Eye: the round goes for the chosen head (aimed at it, then steered onto it in flight).
                Vector3 head = default;
                Pawn homing = CyberSkills.Eye(pawn) ? CyberSkills.Target(pawn, verb, out head) : null;
                if (homing != null)
                {
                    V3 to = new V3(head.x, head.y, head.z) - eye;
                    if (to.Length > 1e-4f) dir = to * (1f / to.Length);
                    gravity = 0f;
                }
                if (!Launch(pawn, verb, def, eye, dir * speed, gravity, Kind.Round, intended, out Projectile round)) return false;
                if (homing != null && shots.TryGetValue(round, out Shot flight)) flight.homing = homing;
                AttachPenetration(round, verb);
            }
            if (verb.verbProps.soundCast != null) verb.verbProps.soundCast.PlayOneShot(new TargetInfo(pawn.Position, pawn.Map));
            if (verb.verbProps.muzzleFlashScale > 0.01f) FleckMaker.Static(pawn.Position, pawn.Map, FleckDefOf.ShotFlash, verb.verbProps.muzzleFlashScale);
            pawn.records?.Increment(RecordDefOf.ShotsFired);
            return true;
        }

        // A turret operated in person: one round from start (just outside the turret) along dir, exactly where the view
        // points. The turret's own burst, sound, flash and barrel wear stay the game's (TryCastNextBurstShot).
        public static bool FireTurretRound(Thing launcher, Verb_LaunchProjectile verb, V3 start, V3 dir)
        {
            ThingDef def = verb.Projectile;
            if (def?.projectile == null || launcher?.Map == null) return false;
            float speed = ViewRules.Speed(BulletSpeeds.ForWeapon(verb.EquipmentSource?.def.defName, def.projectile.SpeedTilesPerTick), FpsMod.Settings.speed);
            if (!Launch(launcher, verb, def, start, dir * speed, ViewRules.Gravity(FpsMod.Settings.gravity), Kind.Round, LocalTargetInfo.Invalid, out Projectile round)) return false;
            shots[round].muzzleY = start.Y;
            if (RatkinTurretShots.Supported(verb))
                shots[round].ratkin = new RatkinFlight { cannon = def.thingClass.FullName == RatkinTurretShots.ShellClass };
            else AttachPenetration(round, verb);
            return true;
        }

        public static bool ThrowGrenade(Pawn pawn, Verb_LaunchProjectile verb)
        {
            ThingDef def = verb.Projectile;
            if (def?.projectile == null || pawn.Map == null) return false;
            float yaw = ControlCamera.AttackYaw(pawn), elevation = -ControlCamera.AttackPitch(pawn);
            ThrowMath.Launch(verb.verbProps.range, elevation, out float speed, out float gravity);
            V3 hand = Hand(pawn), dir = HitMath.Direction(yaw, -elevation);
            if (!Launch(pawn, verb, def, hand, dir * speed, gravity, Kind.Grenade, LocalTargetInfo.Invalid, out _)) return false;
            if (verb.verbProps.soundCast != null) verb.verbProps.soundCast.PlayOneShot(new TargetInfo(pawn.Position, pawn.Map));
            return true;
        }

        private static bool Launch(Thing pawn, Verb verb, ThingDef def, V3 start, V3 vel, float gravity, Kind kind, LocalTargetInfo intended, out Projectile spawned)
        {
            Map map = pawn.Map;
            V3 far = Landing(map, start, vel, gravity);
            IntVec3 cell = FloorSpace.Cell(map, far).ClampInsideMap(map);
            var p = (Projectile)GenSpawn.Spawn(def, pawn.Position, map, WipeMode.Vanish);
            spawned = p;
            Vector3 origin = FloorSpace.Logical(map, start, pawn.Position); origin.y = def.Altitude;
            p.Launch(pawn, origin, new LocalTargetInfo(cell), intended, ProjectileHitFlags.None, false, verb.EquipmentSource, null);
            // ProjectZ's native 2D-flight remapping must not also advance/impact a custom 3D projectile.
            FloorSpace.OwnFlight(p);
            originRef(p) = origin;
            destinationRef(p) = FloorSpace.Logical(map, far, cell);
            float flat = Mathf.Max(0.01f, Mathf.Sqrt(vel.X * vel.X + vel.Z * vel.Z));
            ticksRef(p) = Math.Max(1, Mathf.CeilToInt(new Vector2(far.X - start.X, far.Z - start.Z).magnitude / flat));
            shots[p] = new Shot { kind = kind, pos = start, vel = vel, gravity = gravity, startY = start.Y, muzzleY = kind == Kind.Round && pawn is Pawn person ? FloorSpace.Offset(person).y + Projectiles.MuzzleHeight(person) : start.Y,
                lastTick = Find.TickManager.TicksGame, launcher = pawn, bounces = kind == Kind.Grenade ? 1 : 0 };
            p.Position = FloorSpace.Cell(map, start).ClampInsideMap(map);
            return true;
        }

        // ---- flight

        public static bool BeforeTick(Projectile __instance)
        {
            if (shots.Count == 0 || !shots.TryGetValue(__instance, out Shot shot)) return true;
            try { Step(__instance, shot); }
            catch (Exception e)
            {
                shots.Remove(__instance);
                Log.ErrorOnce("[FpsBridge] Flight step failed; the projectile was removed: " + e, 19271704);
                if (!__instance.Destroyed) __instance.Destroy(DestroyMode.Vanish);
            }
            return false;
        }

        public static bool ExactPosition(Projectile __instance, ref Vector3 __result)
        {
            if (shots.Count == 0 || !shots.TryGetValue(__instance, out Shot shot)) return true;
            // Sustainers can query DrawPos after Impact/Destroy removed the projectile
            // from its map. Its vanilla origin/destination remain valid; floor lookup does not.
            if (__instance.Map == null || __instance.Destroyed)
            { shots.Remove(__instance); return true; }
            __result = FloorSpace.Logical(__instance.Map, shot.pos, __instance.Position);
            __result.y = __instance.def.Altitude;
            return false;
        }

        // Our flights carry their own height; the game's drawn arc would be added on top of it.
        public static bool NoArc(Projectile __instance, ref float __result)
        {
            if (shots.Count == 0 || !shots.ContainsKey(__instance)) return true;
            __result = 0f;
            return false;
        }

        // IsoRim draws the projectile at this height (true cells). A round starts at the drawn muzzle and joins its real
        // flight within a cell and a half, so the tracer leaves the gun and still lands where the eye aimed.
        public static bool DrawnHeight(Projectile p, ref float __result)
        {
            if (shots.Count == 0 || p == null || !shots.TryGetValue(p, out Shot shot)) return true;
            float blend = shot.kind == Kind.Round ? Mathf.Clamp01(1f - shot.flown / 1.5f) : 0f;
            __result = shot.pos.Y + (shot.muzzleY - shot.startY) * blend;
            return false;
        }

        public static void AfterDrawAt(Projectile p)
        {
            if (p == null) return;
            if (!shots.TryGetValue(p, out Shot shot)) { TurretTracers.ObserveVanilla(p); return; }
            if (shot.kind != Kind.Round) return;
            try
            {
                if (shot.airLaunched) { DrawAirborneRound(p, shot); return; }
                if (CeCompat.IsExplosiveDef(p.def) && !TurretTracers.IsMount(shot.launcher)) return;
                DrawRoundTracer(p, shot);
            }
            catch (Exception e) { Log.ErrorOnce("[FpsBridge] Tracer draw failed: " + e, 19271705); }
        }
        internal static bool OwnsTurretTracerFlight(Projectile projectile) => shots.ContainsKey(projectile);

        public static bool BeforeDrawAt(Projectile p) => !WeaponCompatibility.Pulse(p?.def) || !shots.ContainsKey(p);

        internal static void DrawAirborneRounds(Map map)
        {
            if (!View3D.Active || map == null) return;
            foreach (var entry in shots)
            {
                var round = entry.Key;
                var shot = entry.Value;
                if (!shot.airLaunched || !round.Spawned || round.Map != map) continue;
                IntVec3 cell = FloorSpace.Cell(map, shot.pos);
                if (cell.InBounds(map) && !cell.Fogged(map)) DrawAirborneRound(round, shot);
            }
        }

        private static void DrawAirborneRound(Projectile round, Shot shot)
        {
            if (!View3D.Active || shot.lastAirDrawFrame == Time.frameCount) return;
            shot.lastAirDrawFrame = Time.frameCount;
            if (!CeCompat.IsExplosiveDef(round.def) || shot.explosiveGun) { DrawRoundTracer(round, shot); return; }
            Visuals.Ordnance(shot.pos, shot.vel);
            if (shot.flown > .01f) Visuals.AerialOrdnanceTrail(shot.pos, shot.vel);
        }

        private static void DrawRoundTracer(Projectile round, Shot shot)
        {
            if (WeaponCompatibility.Pulse(round.def))
                Visuals.Pulse(shot.pos, shot.vel, Mathf.Min(.8f, shot.flown + .12f), Vector3.zero, round.thingIDNumber);
            else
            {
                float scale = AerialCombat.TracerScale(shot.launcher, round.intendedTarget);
                if (!TurretTracers.DrawOwned(round, shot.launcher, round.EquipmentDef, shot.pos, shot.vel, shot.flown + .2f, scale, false))
                    Visuals.Tracer(shot.pos, shot.vel, Mathf.Min(2.4f, shot.flown + .2f), Vector3.zero, scale);
            }
        }

        private static void Step(Projectile p, Shot shot)
        {
            Map map = p.Map;
            if (map == null || p.Destroyed) { shots.Remove(p); return; }
            int now = Find.TickManager.TicksGame;
            int steps = Math.Min(10, now - shot.lastTick);
            if (steps <= 0) return;
            shot.lastTick = now;
            for (int s = 0; s < steps; s++)
            {
                // Death Eye: straight for the head wherever it is now, at the round's own speed (no drop).
                Pawn arriving = null;
                V3 goal = default;
                if (shot.homing != null)
                {
                    if (shot.homing.Spawned && !shot.homing.Dead && shot.homing.Map == map)
                    {
                        Vector3 head = CyberSkills.HeadOf(shot.homing);
                        goal = new V3(head.x, head.y, head.z);
                        V3 to = goal - shot.pos;
                        float len = to.Length, speed = shot.vel.Length;
                        if (len > 1e-4f) shot.vel = to * (speed / len);
                        // Reaching the head point this step: end there, so a small model whose mesh misses the
                        // estimated head centre is still hit instead of being circled.
                        if (len <= speed) arriving = shot.homing;
                    }
                    else { shot.homing = null; }
                }
                else if(shot.aerialTarget!=null)
                {
                    if(!AerialTargetLock.Valid(shot.aerialTarget,map))shot.aerialTarget=null;
                    else shot.vel=AerialTargetLock.Steer(shot.aerialTarget,map,shot.pos,shot.vel,shot.aerialTurn);
                }
                else shot.vel.Y -= shot.gravity;
                V3 a = shot.pos, b = a + shot.vel;
                if (shot.ratkin != null && float.IsFinite(shot.ratkin.remaining))
                    b = a + shot.vel * Mathf.Min(1f, shot.ratkin.remaining / Mathf.Max(.001f,shot.vel.Length));
                if (arriving != null) b = goal;
                if (shot.pen != null)
                {
                    // 0.6.0: every contact of this sub-step in order; gravity was applied once above.
                    if (PenetrateSegment(p, shot, a, b, out float stopAt))
                    {
                        shot.pos = V3.Lerp(a, b, stopAt);
                        shot.flown += (shot.pos - a).Length;
                        FinishPenetration(p, shot);
                        return;
                    }
                    if (p.Destroyed) { shots.Remove(p); return; }
                }
                else if (Trace(map, shot, a, b, out float t, out Thing thing, out BodyPartRecord part, out HitRegion region, out V3 normal))
                {
                    V3 at = V3.Lerp(a, b, t);
                    shot.flown += (at - a).Length;
                    if (shot.kind == Kind.Grenade && shot.bounces > 0 && thing != null && !(thing is Pawn) && Math.Abs(normal.Y) < 0.5f)
                    {
                        // One bounce off a wall: reflect across the face, lose most of the speed, carry on from just outside.
                        shot.bounces--;
                        float d = shot.vel.X * normal.X + shot.vel.Z * normal.Z;
                        shot.vel = new V3((shot.vel.X - 2f * d * normal.X) * 0.45f, shot.vel.Y * 0.6f, (shot.vel.Z - 2f * d * normal.Z) * 0.45f);
                        shot.pos = at + new V3(normal.X * 0.05f, 0f, normal.Z * 0.05f);
                        continue;
                    }
                    shot.pos = at;
                    if (thing == null && CeStructureRoof.TryContact(map, a, b, t, out var roof))
                        CeStructureRoof.Hit(map, roof, RoofHealth.ProjectileDamage(p));
                    if (shot.kind == Kind.Grenade) { Land(p, shot, thing); return; }
                    if (Hit(p, shot, thing, part, region, normal)) continue;
                    return;
                }
                if (AerialProximityFuse.TryBurst(p,shot.launcher,a,b)) {shots.Remove(p);return;}
                if (arriving != null && !p.Destroyed)
                {
                    shot.flown += (b - a).Length;
                    shot.pos = b;
                    Hit(p, shot, arriving, HeadPart(arriving), HitRegion.Head);
                    return;
                }
                shot.pos = b;
                shot.flown += shot.vel.Length;
                if (shot.ratkin != null && float.IsFinite(shot.ratkin.remaining))
                {
                    shot.ratkin.remaining -= (b-a).Length;
                    if (shot.ratkin.remaining <= .001f) { Hit(p,shot,null,null,HitRegion.Solid,new V3(0,1,0)); return; }
                }
                IntVec3 cell = FloorSpace.Cell(map, b);
                if (!FloorSpace.InWorld(map, b) || !cell.InBounds(map))
                {
                    // Left the map: the only way, besides a contact, for a round to end.
                    shots.Remove(p);
                    p.Destroy(DestroyMode.Vanish);
                    return;
                }
                if (cell != p.Position) p.Position = cell;
            }
        }

        // Put the game's own state on the contact point before its Impact runs (effects, explosion cell, drawing).
        private static void Settle(Projectile p, Shot shot, Thing surface = null)
        {
            Map map = p.Map;
            AerialBlast.Remember(p,map,shot.pos);
            IntVec3 cell = FloorSpace.ContactCell(map, shot.pos, shot.vel.Y, surface);
            if (cell.InBounds(map) && cell != p.Position) p.Position = cell;
            destinationRef(p) = FloorSpace.Logical(map, shot.pos, cell);
            ticksRef(p) = 0;
        }

        private static void Land(Projectile p, Shot shot, Thing surface)
        {
            Settle(p, shot, surface);
            shots.Remove(p);
            // Fused grenades lie where they landed and the game's own countdown takes over.
            impact.Invoke(p, new object[] { null, false });
        }

        private static bool Hit(Projectile p, Shot shot, Thing thing, BodyPartRecord part, HitRegion region, V3 normal = default)
        {
            if(thing==shot.homing&&thing is Pawn eyeTarget){part=CyberSkills.HeadPart(eyeTarget)??part;region=HitRegion.Head;}
            int previousTicks = ticksRef(p);
            Vector3 previousDestination = destinationRef(p);
            bool keep = false;
            Settle(p, shot, thing);
            if (shot.ratkin != null)
            {
                destinationRef(p) = previousDestination;
                if (!shot.ratkin.cannon && thing != null) ticksRef(p) = Math.Max(1,previousTicks);
            }
            pendingThing = part != null ? thing : null;
            pendingPart = part;
            pendingCover = thing is Building ? thing : null;   // 0.9.0: stone and metal wear less (CoverWear)
            try
            {
                Vector3 logicalHit = FloorSpace.Logical(p.Map, shot.pos, p.Position);
                Visuals.ImpactLight(p.Map, new V3(logicalHit.x, shot.pos.Y - FloorSpace.Band(p.Map, p.Position).Height, logicalHit.z), thing);
                if (thing is Pawn victim) Notify(victim, part, region);
                // ExactPosition still answers the hit point while the game's impact effects run.
                bool blocked = shot.ratkin?.cannon == true && thing == null && normal.Y < .5f;
                impact.Invoke(p, new object[] { thing, blocked });
                keep = ContinueRatkin(p,shot,thing,normal);
            }
            finally
            {
                pendingThing = null; pendingPart = null; pendingCover = null;
                if (!keep) shots.Remove(p);
            }
            if (!keep && !p.Destroyed) p.Destroy(DestroyMode.Vanish);
            return keep;
        }

        private static Thing pendingCover;

        private static BodyPartRecord HeadPart(Pawn pawn)
        {
            foreach (BodyPartRecord r in pawn.health.hediffSet.GetNotMissingParts())
                if (r.def == BodyPartDefOf.Head) return r;
            return null;
        }

        public static void BeforeDamage(Thing __instance, ref DamageInfo dinfo)
        {
            if (pendingCover != null && __instance == pendingCover)
            {
                pendingCover = null;
                dinfo.SetAmount(dinfo.Amount * CoverWear.Factor(ObstacleOf(__instance)));
                return;
            }
            if (pendingThing == null || __instance != pendingThing) return;
            if (__instance is Pawn pawn && pendingPart != null && !pawn.health.hediffSet.PartIsMissing(pendingPart) && dinfo.HitPart == null)
                dinfo.SetHitPart(pendingPart);
            pendingThing = null; pendingPart = null;
        }

        internal static void Notify(Pawn victim, BodyPartRecord part, HitRegion region, bool melee = false)
        {
            HitMarker.Hit(victim, part != null && region != HitRegion.Head && part.def == BodyPartDefOf.Head ? HitRegion.Head : region, melee);
            string where = region == HitRegion.Head ? L.T("头部", "head") : region == HitRegion.Leg ? L.T("腿部", "legs") : L.T("躯干", "torso");
            LastHit = L.T("命中 ", "Hit ") + victim.LabelShortCap + " · " + where + (part != null ? L.T("（", " (") + part.LabelCap + L.T("）", ")") : "");
            LastHitUntil = Time.unscaledTime + 1.2f;
        }

        // ---- collision

        // External projectile owners can reuse the exact FPS mesh/volume/roof trace without entering
        // our Projectile flight table or damage/penetration implementation. One context per projectile.
        internal sealed class ExternalTrace
        {
            private readonly Shot shot;
            internal ExternalTrace(Thing launcher) { shot = new Shot { launcher = launcher }; }
            internal bool Segment(Map map, V3 from, V3 to, out float fraction, out Thing thing,
                out BodyPartRecord part, out HitRegion region, out V3 normal, ISet<Thing> passedThings = null, ISet<IntVec3> passedRoofs = null) =>
                Trace(map, shot, from, to, out fraction, out thing, out part, out region, out normal,
                    passedThings: passedThings, passedRoofs: passedRoofs);
        }

        internal static void ExternalBeam(V3 start,V3 end) => beams.Add(new BeamFlash {map=Bridge.ShootingPawn?.Map??VehicleControl.Vehicle?.Map,start=start,end=end,until=Time.unscaledTime+.12f});

        private static bool Trace(Map map, Shot shot, V3 a, V3 b, out float best, out Thing hitThing, out BodyPartRecord hitPart, out HitRegion hitRegion, out V3 normal, bool includePawns = true,
            ISet<Thing> passedThings = null, ISet<IntVec3> passedRoofs = null)
        {
            best = float.PositiveInfinity; hitThing = null; hitPart = null; hitRegion = HitRegion.None; normal = new V3(0f, 1f, 0f);
            seen.Clear();
            float length = (b - a).Length;
            int samples = Math.Max(1, Mathf.CeilToInt(length / 0.4f));
            IntVec3 last = IntVec3.Invalid;
            for (int i = 0; i <= samples; i++)
            {
                V3 q = V3.Lerp(a, b, i / (float)samples);
                IntVec3 cell = new IntVec3(Mathf.FloorToInt(q.X), 0, Mathf.FloorToInt(q.Z));
                if (cell == last) continue;
                last = cell;
                for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++)
                foreach (FloorBand band in FloorSpace.Bands(map))
                {
                    if (cell.z + dz < 0 || cell.z + dz >= band.Rows) continue;
                    IntVec3 c = new IntVec3(cell.x + dx, 0, cell.z + dz + band.Shift);
                    if (!c.InBounds(map)) continue;
                    List<Thing> things = map.thingGrid.ThingsListAtFast(c);
                    for (int k = 0; k < things.Count; k++)
                    {
                        Thing thing = things[k];
                        if (thing == shot.launcher || MountedControl.OwnMount(shot.launcher, thing) || shot.ratkin?.pierced.Contains(thing) == true
                            || passedThings?.Contains(thing) == true || (!includePawns && thing is Pawn) || !seen.Add(thing) || thing.Destroyed || !thing.Spawned) continue;
                        volumes.Clear();
                        if (thing is Pawn pawn) BodyRegions.AddVolumes(pawn, volumes);
                        else AddSolid(thing, volumes);
                        if (volumes.Count == 0) continue;
                        // 0.8.1: a pawn IsoRim has just drawn is hit exactly where its drawn model is (never past it);
                        // the nearest part volume says which body part.
                        if (thing is Pawn drawn && !VehicleApi.IsVehicle(drawn) && FigureMeshes.Has(drawn))
                        {
                            if (!FigureMeshes.Hit(drawn, a, b, out float mt, out V3 mn) || mt >= best) continue;
                            int vi = FigureMeshes.NearestVolume(volumes, 0, V3.Lerp(a, b, mt));
                            if (vi < 0) continue;
                            best = mt; hitThing = thing; hitRegion = volumes[vi].Region;
                            hitPart = BodyRegions.ChoosePart(drawn, volumes[vi]);
                            normal = mn;
                            continue;
                        }
                        int index = HitMath.Nearest(a, b, volumes, out float t);
                        if (index < 0 || t >= best) continue;
                        best = t; hitThing = thing; hitRegion = volumes[index].Region;
                        hitPart = thing is Pawn hitPawn ? BodyRegions.ChoosePart(hitPawn, volumes[index]) : null;
                        normal = HitMath.BoxNormal(volumes[index], V3.Lerp(a, b, t));
                    }
                }
            }
            if (includePawns && AerialFlight.Store != null)
                foreach (var flight in AerialFlight.Store.Flights)
                {
                    Pawn plane = flight.Key;
                    if (plane == shot.launcher || !plane.Spawned || plane.Map != map || !seen.Add(plane)) continue;
                    volumes.Clear(); VehicleModels.Volumes(plane, volumes);
                    int index = HitMath.Nearest(a, b, volumes, out float t);
                    if (index < 0 || t >= best) continue;
                    best = t; hitThing = plane; hitPart = null; hitRegion = HitRegion.Solid; normal = HitMath.BoxNormal(volumes[index], V3.Lerp(a,b,t));
                }
            if (FloorSpace.PlaneContact(map, a, b, ref best, out V3 planeNormal, passedRoofs))
            { hitThing = null; hitPart = null; hitRegion = HitRegion.Solid; normal = planeNormal; }
            // Roofs as drawn (a pitched roof's slope, a flat roof's slab): rounds stop on them as the enemy's sight does.
            float roofT = Sight3D.RoofContact(map, a, b, passedRoofs);
            if (roofT < best)
            { best = roofT; hitThing = null; hitPart = null; hitRegion = HitRegion.Solid; normal = new V3(0f, b.Y > a.Y ? -1f : 1f, 0f); }
            if (!float.IsFinite(best)) return false;
            best = Mathf.Clamp01(best);
            return true;
        }

        internal static bool SightContact(Thing pawn, V3 a, V3 b, out V3 point, bool includePawns)
        {
            bool found = Trace(pawn.Map, new Shot { launcher = pawn }, a, b, out float t, out _, out _, out _, out _, includePawns);
            point = found ? V3.Lerp(a, b, t) : b;
            return found;
        }

        internal static LocalTargetInfo PickTarget(Thing pawn, V3 a, V3 b)
        {
            if (!Trace(pawn.Map, new Shot { launcher = pawn }, a, b, out float t, out Thing thing, out _, out _, out _)) return LocalTargetInfo.Invalid;
            if (thing != null) return new LocalTargetInfo(thing);
            V3 point = V3.Lerp(a, b, t);
            IntVec3 cell = FloorSpace.ContactCell(pawn.Map, point, b.Y - a.Y);
            return cell.InBounds(pawn.Map) && !cell.Fogged(pawn.Map) ? new LocalTargetInfo(cell) : LocalTargetInfo.Invalid;
        }

        internal static void AddSolid(Thing thing, List<HitVolume> into)
        {
            ThingDef def = thing.def;
            if (def == null) return;
            if (def.category == ThingCategory.Building)
            {
                if(FurnitureSurfaces.IsSeat(thing)){FurnitureSurfaces.Volumes(thing,into);return;}
                if (def.Fillage == FillCategory.None) return;
                if (thing is Building_Door door && door.Open) return;
                float height = Bindings.PickHeight(thing);
                if (height <= 0.01f) return;
                CellRect r = thing.OccupiedRect();
                float hx = (r.maxX - r.minX + 1) * 0.5f, hz = (r.maxZ - r.minZ + 1) * 0.5f;
                Vector3 offset = FloorSpace.Offset(thing);
                into.Add(BodyVolumes.Box(HitRegion.Solid, new V3(r.minX + hx + offset.x, height * 0.5f + offset.y, r.minZ + hz + offset.z), new V3(hx, height * 0.5f, hz), 0f));
                return;
            }
            // 0.6.0: stone chunks and slag stop rounds: a low block on their cell, not the whole cell to the ceiling.
            if (def.category == ThingCategory.Item && IsChunk(def))
            {
                float h = Bindings.PickHeight(thing);
                if (!(h > .05f) || h > .9f) h = .4f;
                Vector3 o = FloorSpace.Offset(thing);
                IntVec3 cpos = thing.Position;
                into.Add(BodyVolumes.Box(HitRegion.Solid, new V3(cpos.x + .5f + o.x, h * .5f + o.y, cpos.z + .5f + o.z), new V3(.38f, h * .5f, .38f), 0f));
                return;
            }
            if (def.category == ThingCategory.Plant && def.plant != null && def.plant.IsTree)
            {
                float height = Bindings.PickHeight(thing);
                if (height <= 0.05f) return;
                Vector3 c = thing.TrueCenter();
                Vector3 o = FloorSpace.Offset(thing);
                float trunk = 0.12f + 0.08f * (thing is Plant plant ? plant.Growth : 1f);
                into.Add(BodyVolumes.Capsule(HitRegion.Solid, -1, new V3(c.x + o.x, o.y, c.z + o.z), new V3(c.x + o.x, o.y + height * 0.7f, c.z + o.z), trunk));
            }
        }

        internal static bool IsChunk(ThingDef def) => def == ThingDefOf.ChunkSlagSteel
            || (def.thingCategories != null && (def.thingCategories.Contains(ThingCategoryDefOf.StoneChunks) || def.thingCategories.Contains(ThingCategoryDefOf.Chunks)));

        // ---- geometry helpers

        // The eye in true space (ground rows, height above the ground).
        public static V3 Eye(Pawn pawn)
        {
            Vector3 e = FirstPerson.Eye;
            return new V3(e.x, e.y, e.z);
        }

        // Where a thrown thing leaves the hand: a little right of and below the eye, ahead of the body.
        public static V3 Hand(Pawn pawn)
        {
            V3 eye = Eye(pawn), f = HitMath.Forward(FirstPerson.Yaw), r = HitMath.Right(FirstPerson.Yaw);
            return eye + f * 0.35f + r * 0.18f + new V3(0f, -0.2f, 0f);
        }

        // Where a flight with this start, velocity and gravity meets the ground, or leaves the map (flat-ground estimate).
        internal static V3 Landing(Map map, V3 start, V3 vel, float g)
        {
            V3 p = start, v = vel;
            for (int i = 0; i < 20000; i++)
            {
                v.Y -= g;
                V3 n = p + v;
                if (n.Y < 0f) return V3.Lerp(p, n, p.Y / Math.Max(1e-6f, p.Y - n.Y));
                if (!FloorSpace.InWorld(map, n)) return n;
                p = n;
            }
            return p;
        }

        // The predicted path of a grenade throw, with the same contacts and bounce as the real flight, for the preview.
        internal static void PredictThrow(Pawn pawn, Verb_LaunchProjectile verb, List<V3> path, out V3 landing)
        {
            path.Clear();
            Map map = pawn.Map;
            float elevation = -ControlCamera.AttackPitch(pawn);
            ThrowMath.Launch(verb.verbProps.range, elevation, out float speed, out float gravity);
            var shot = new Shot { kind = Kind.Grenade, pos = Hand(pawn), vel = HitMath.Direction(ControlCamera.AttackYaw(pawn), -elevation) * speed, gravity = gravity, launcher = pawn, bounces = 1 };
            path.Add(shot.pos);
            landing = shot.pos;
            for (int i = 0; i < 600; i++)
            {
                shot.vel.Y -= shot.gravity;
                V3 a = shot.pos, b = a + shot.vel;
                if (Trace(map, shot, a, b, out float t, out Thing thing, out _, out _, out V3 normal))
                {
                    V3 at = V3.Lerp(a, b, t);
                    if (shot.bounces > 0 && thing != null && !(thing is Pawn) && Math.Abs(normal.Y) < 0.5f)
                    {
                        shot.bounces--;
                        float d = shot.vel.X * normal.X + shot.vel.Z * normal.Z;
                        shot.vel = new V3((shot.vel.X - 2f * d * normal.X) * 0.45f, shot.vel.Y * 0.6f, (shot.vel.Z - 2f * d * normal.Z) * 0.45f);
                        shot.pos = at + new V3(normal.X * 0.05f, 0f, normal.Z * 0.05f);
                        path.Add(shot.pos);
                        continue;
                    }
                    path.Add(at);
                    landing = at;
                    return;
                }
                shot.pos = b;
                path.Add(b);
                landing = b;
                if (!FloorSpace.InWorld(map, b)) return;
            }
        }

        // The cell under the view at a distance (in bounds and visible), for the game's sight checks of launchers and
        // other native verbs when the crosshair is on nothing nearer.
        public static LocalTargetInfo FallbackTarget(Pawn pawn, Verb verb)
        {
            Map map = pawn.Map;
            V3 eye = Eye(pawn), dir = ControlCamera.ShotDirection(pawn);
            float flat = Mathf.Sqrt(dir.X * dir.X + dir.Z * dir.Z);
            if (flat < 0.05f) return LocalTargetInfo.Invalid;
            float reach = Mathf.Max(1.5f, verb.verbProps.range - 1f);
            for (float d = reach; d >= 1.5f; d -= 1f)
            {
                IntVec3 c = FloorSpace.Cell(map, eye + dir * (d / flat));
                if (c != pawn.Position && c.InBounds(map) && !c.Fogged(map) && verb.CanHitTarget(new LocalTargetInfo(c))) return new LocalTargetInfo(c);
            }
            return LocalTargetInfo.Invalid;
        }
    }

    // Body parts per BodyDef (0.6.0): every hit volume is tied to one real BodyPartRecord (head, neck, torso, each
    // shoulder, arm, hand, leg and foot where the body has them). Animals and mechanoids map head, neck, torso and each
    // moving limb with its foot/paw; anything not resolvable falls to the next larger part, never to a random organ.
    internal static class BodyRegions
    {
        private sealed class Layout
        {
            public readonly List<BodyPartRecord> parts = new List<BodyPartRecord>();
            public readonly List<HitRegion> region = new List<HitRegion>();
            public readonly BodyPlan plan = new BodyPlan();
            public readonly List<LegSlot> slots = new List<LegSlot>();
        }

        private static readonly Dictionary<BodyDef, Layout> layouts = new Dictionary<BodyDef, Layout>();
        private static readonly List<bool> present = new List<bool>();

        public static void Clear() => layouts.Clear();

        private static Layout For(BodyDef body)
        {
            if (layouts.TryGetValue(body, out Layout l)) return l;
            l = new Layout();
            l.parts.AddRange(body.AllParts);
            for (int i = 0; i < l.parts.Count; i++)
            {
                HitRegion r = HitRegion.Torso;
                for (BodyPartRecord p = l.parts[i]; p != null; p = p.parent)
                {
                    if (IsHead(p.def)) { r = HitRegion.Head; break; }
                    if (HasTag(p.def, BodyPartTagDefOf.MovingLimbCore) || HasTag(p.def, BodyPartTagDefOf.MovingLimbSegment) || HasTag(p.def, BodyPartTagDefOf.MovingLimbDigit))
                        r = HitRegion.Leg;
                }
                l.region.Add(r);
            }
            BodyPlan plan = l.plan;
            plan.Torso = Math.Max(0, l.parts.IndexOf(body.corePart));
            int depth(BodyPartRecord p) { int d = 0; for (BodyPartRecord q = p.parent; q != null; q = q.parent) d++; return d; }
            int Shallowest(Func<BodyPartRecord, bool> match)
            {
                int best = -1, bestDepth = int.MaxValue;
                for (int i = 0; i < l.parts.Count; i++)
                    if (match(l.parts[i]) && depth(l.parts[i]) < bestDepth) { best = i; bestDepth = depth(l.parts[i]); }
                return best;
            }
            plan.Head = Shallowest(p => p.def == BodyPartDefOf.Head || Ends(p.def, "Head"));
            plan.Neck = Shallowest(p => p.def == BodyPartDefOf.Neck || Ends(p.def, "Neck"));
            // Arms come in pairs (shoulder > arm > hand); the side is the part's own label, else the order found.
            AssignPair(l, plan.Shoulder, "Shoulder");
            AssignPair(l, plan.Arm, "Arm");
            AssignPair(l, plan.Hand, "Hand");
            var legs = new List<int>();
            for (int i = 0; i < l.parts.Count; i++)
            {
                BodyPartRecord part = l.parts[i];
                if (!HasTag(part.def, BodyPartTagDefOf.MovingLimbCore)) continue;
                bool nested = false;
                for (BodyPartRecord p = part.parent; p != null; p = p.parent) if (HasTag(p.def, BodyPartTagDefOf.MovingLimbCore)) nested = true;
                if (!nested) legs.Add(i);
            }
            for (int n = 0; n < legs.Count; n++)
            {
                BodyPartRecord leg = l.parts[legs[n]];
                LegSlot slot = SlotOf(leg);
                if (slot.Side == 0) slot = new LegSlot(n % 2 == 0 ? -1 : 1, slot.Front);
                int foot = -1;
                for (int i = 0; i < l.parts.Count; i++)
                {
                    BodyPartRecord c = l.parts[i];
                    if (c.parent != leg) continue;
                    if (Ends(c.def, "Foot") || Ends(c.def, "Paw") || Ends(c.def, "Hoof") || HasTag(c.def, BodyPartTagDefOf.MovingLimbSegment)) { foot = i; break; }
                }
                plan.Legs.Add(new LegRef(slot.Side, slot.Front, legs[n], foot));
                l.slots.Add(slot);
            }
            layouts[body] = l;
            return l;
        }

        private static bool Ends(BodyPartDef def, string name) => def?.defName != null && def.defName.EndsWith(name, StringComparison.OrdinalIgnoreCase);

        private static void AssignPair(Layout l, int[] pair, string name)
        {
            var found = new List<int>();
            for (int i = 0; i < l.parts.Count; i++) if (Ends(l.parts[i].def, name)) found.Add(i);
            foreach (int i in found)
            {
                int side = SlotOf(l.parts[i]).Side;
                int slot = side < 0 ? 0 : side > 0 ? 1 : (pair[0] < 0 ? 0 : 1);
                if (pair[slot] < 0) pair[slot] = i;
            }
        }

        private static bool IsHead(BodyPartDef def) => def == BodyPartDefOf.Head || def == BodyPartDefOf.Neck
            || (def.defName != null && (def.defName.IndexOf("Head", StringComparison.OrdinalIgnoreCase) >= 0 || def.defName.IndexOf("Neck", StringComparison.OrdinalIgnoreCase) >= 0));

        private static bool HasTag(BodyPartDef def, BodyPartTagDef tag) => tag != null && def.tags != null && def.tags.Contains(tag);

        private static LegSlot SlotOf(BodyPartRecord part)
        {
            string label = (part.untranslatedCustomLabel ?? part.customLabel ?? "").ToLowerInvariant();
            int side = label.Contains("left") ? -1 : label.Contains("right") ? 1 : 0;
            int front = label.Contains("front") || label.Contains("fore") ? 1 : label.Contains("rear") || label.Contains("hind") || label.Contains("back") ? -1 : 0;
            return new LegSlot(side, front);
        }

        // A pawn's volumes, built once per frame and game tick: the view's aim trace, rounds in flight, melee sweeps and
        // the friend-or-foe sight checks all ask for the same pawns, often many times a frame on a crowded map.
        private sealed class Cached { public int frame = -1, tick = -1; public readonly List<HitVolume> list = new List<HitVolume>(); }
        private static readonly Dictionary<Pawn, Cached> cache = new Dictionary<Pawn, Cached>();

        public static void AddVolumes(Pawn pawn, List<HitVolume> into)
        {
            if (VehicleApi.IsVehicle(pawn)) { VehicleModels.Volumes(pawn, into); return; }
            int frame = Time.frameCount, tick = Find.TickManager?.TicksGame ?? 0;
            if (!cache.TryGetValue(pawn, out Cached c))
            {
                if (cache.Count > 2000) cache.Clear();
                cache[pawn] = c = new Cached();
            }
            if (c.frame != frame || c.tick != tick)
            {
                c.list.Clear();
                Build(pawn, c.list);
                c.frame = frame; c.tick = tick;
            }
            into.AddRange(c.list);
        }

        public static void Invalidate(Pawn pawn) { if (cache.TryGetValue(pawn, out Cached c)) c.frame = -1000000; }

        private static void Build(Pawn pawn, List<HitVolume> into)
        {
            if (pawn.Dead || pawn.RaceProps?.body == null) return;
            Layout l = For(pawn.RaceProps.body);
            present.Clear();
            for (int i = 0; i < l.parts.Count; i++) present.Add(!pawn.health.hediffSet.PartIsMissing(l.parts[i]));
            // 0.8.1: a person IsoRim has just drawn is hit where its model is.
            if (pawn.RaceProps.Humanlike && ModelHits.Build(pawn, l.plan, present, into)) return;
            BodyShape shape = pawn.GetPosture() != PawnPosture.Standing || pawn.Downed ? BodyShape.Lying
                : (pawn.RaceProps.Humanlike || l.plan.Legs.Count == 2) ? BodyShape.Upright : BodyShape.Long;
            V3 at = FloorSpace.Spot(pawn);
            at.Y += MountedControl.Lift(pawn) + Knockback.Lift(pawn);
            float height = shape == BodyShape.Lying ? 0.35f : Bindings.PickHeight(pawn);
            if (Stances.For(pawn) && shape != BodyShape.Lying)
            {
                // 0.7.0: the controlled pawn's posture: prone lies along its facing, crouched is lower, a jump lifts it.
                StanceState st = Stances.State;
                if (st.Posture == Posture.Prone && st.Height < .5f) { shape = BodyShape.Lying; height = 0.35f; }
                else height *= st.Height;
                at.Y += st.Lift + Surface.HeightOf(pawn);
            }
            int start = into.Count;
            BodyVolumes.BuildFine(into, shape, at, pawn.Rotation.AsAngle, height, pawn.BodySize, l.plan, present);
            LeanControl.Volumes(pawn, into, start);
        }

        public static BodyPartRecord PartOf(Pawn pawn, in HitVolume v)
        {
            if (VehicleApi.IsVehicle(pawn)) return null;
            if (pawn?.RaceProps?.body == null) return null;
            Layout l = For(pawn.RaceProps.body);
            if (v.Part < 0 || v.Part >= l.parts.Count) return null;
            BodyPartRecord part = l.parts[v.Part];
            return pawn.health.hediffSet.PartIsMissing(part) ? null : part;
        }

        // Kept for melee and the non-penetrating path: the volume's own part (no random choice any more).
        public static BodyPartRecord ChoosePart(Pawn pawn, in HitVolume v) => PartOf(pawn, v);
    }
}
