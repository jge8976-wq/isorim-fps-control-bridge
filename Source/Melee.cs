using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace HSK.FpsBridge
{
    // First-person melee.
    //  - Left button: a chain of four light attacks, one per press (or per beat while held): thrust, sweep (right to
    //    left), overhead (top to bottom), diagonal (high right to low left); back to the thrust after a pause.
    //  - Right button: the heavy slash (right to left), double damage split over every body part the edge crossed.
    // A thrust strikes the first creature (or solid building) along the view. A swing strikes every creature its edge
    // meets along its path (SwingPath): the one under the crosshair whatever its side, others only if hostile; past the
    // first, each takes 70 %. Every strike is the weapon's own damage on the part the edge met, after the game's own
    // rolls: the attacker's melee hit chance and the victim's melee dodge. The part struck shows over the point of
    // contact (MeleeFx), with the game's blood flash; the blade leaves a trail along its path. Pace follows the Melee
    // skill (CooldownRules): thrust x1, sweep and diagonal x1.1, overhead x1.2, heavy x1.5.
    internal static class Melee
    {
        // Reach follows the equipped model from grip to front, plus the arm extension of the current attack.
        private const int SwingRays = 15;
        private const float ComboReset = 1.5f, OthersShare = .7f;
        private static readonly SwingKind[] Chain = { SwingKind.Thrust, SwingKind.Sweep, SwingKind.Overhead, SwingKind.Diagonal };

        private static readonly List<HitVolume> volumes = new List<HitVolume>();
        private static readonly HashSet<Thing> seen = new HashSet<Thing>();
        private sealed class Struck { public Pawn pawn; public float t; public V3 at; public HitVolume first; public readonly List<BodyPartRecord> parts = new List<BodyPartRecord>(); }
        private static readonly List<Struck> struck = new List<Struck>();
        private static readonly List<Thing> objects = new List<Thing>();
        private static MethodInfo damageInfos;
        private static bool damageInfosLooked;
        private static readonly Type CeMeleeVerb = AccessTools.TypeByName("CombatExtended.Verb_MeleeAttackCE");
        private static readonly MethodInfo CeDamageInfos = CeMeleeVerb == null ? null :
            AccessTools.Method(CeMeleeVerb, "DamageInfosToApply", new[] { typeof(LocalTargetInfo), typeof(bool) });
        private static readonly PropertyInfo CeLastAttack = CeMeleeVerb == null ? null :
            AccessTools.Property(CeMeleeVerb, "LastAttackVerb");
        private static readonly FieldInfo CurrentTarget = AccessTools.Field(typeof(Verb), "currentTarget");
        private static int recoverUntil, combo;
        private static float lastSwing = -10f;

        public static string Status { get; private set; }

        public static void Reset() { recoverUntil = 0; Status = null; combo = 0; whirler = null; lastRight = -10f; MeleeFx.Reset(); }

        private static bool Ready(Pawn pawn) =>
            !(pawn.stances.curStance is Stance_Busy) && Find.TickManager.TicksGame >= recoverUntil;

        // heavy: right button pressed this frame; heavyHeld: right button down.
        public static void Frame(Pawn pawn, Verb verb, bool light, bool heavy, bool heavyHeld)
        {
            // Double right click, held on the second: the whirlwind, for as long as it is held and breath lasts.
            if (heavy)
            {
                bool second = Time.unscaledTime - lastRight < WhirlRules.DoubleClickSeconds;
                lastRight = Time.unscaledTime;
                if (second && StartWhirl(pawn)) return;
            }
            if (whirler == pawn)
            {
                whirlFrame = Time.frameCount;
                if (!heavyHeld || !WhirlFrame(pawn, verb)) StopWhirl(pawn, heavyHeld ? null : L.T("收势", "stopped"));
                return;
            }
            if (!Ready(pawn)) return;
            if (heavy) { Swing(pawn, SwingKind.Heavy); return; }
            if (!light) return;
            if (Time.unscaledTime - lastSwing > ComboReset) combo = 0;
            SwingKind kind = Chain[combo];
            combo = (combo + 1) % Chain.Length;
            Swing(pawn, kind);
        }

        // The Melee skill (a mech: its fixed level; anything without skills: 10).
        private static int SkillOf(Pawn pawn)
        {
            SkillRecord s = pawn.skills?.GetSkill(SkillDefOf.Melee);
            if (s != null) return s.Level;
            return pawn.RaceProps?.IsMechanoid == true ? pawn.RaceProps.mechFixedSkillLevel : 10;
        }

        private static float PaceFactor(SwingKind k) => k == SwingKind.Heavy ? 1.5f : k == SwingKind.Overhead ? 1.2f : k == SwingKind.Thrust ? 1f : 1.1f;

        private static void Swing(Pawn pawn, SwingKind kind)
        {
            lastSwing = Time.unscaledTime;
            V3 eye = Ballistics.Eye(pawn);
            float yaw = ControlCamera.AttackYaw(pawn), pitch = ControlCamera.AttackPitch(pawn);
            float reach = MeleeReach.For(pawn, kind == SwingKind.Thrust);
            WeaponView.Melee(kind);
            MeleeFx.Trail(eye, yaw, pitch, kind, reach);
            int pace = Mathf.RoundToInt(CooldownRules.MeleeTicks(SkillOf(pawn)) * PaceFactor(kind));
            string name = SwingPath.Name(kind);

            if (kind == SwingKind.Thrust)
            {
                V3 dir = ControlCamera.ShotDirection(pawn);
                Thing target = First(pawn, eye, eye + dir * reach, out float t, out HitVolume v);
                if (target is Pawn victim)
                {
                    var one = new Struck { pawn = victim, t = t, at = V3.Lerp(eye, eye + dir * reach, t), first = v };
                    BodyPartRecord part = BodyRegions.ChoosePart(victim, v);
                    if (part != null) one.parts.Add(part);
                    struck.Clear(); struck.Add(one);
                    Strike(pawn, kind, pace, name);
                    return;
                }
                if (ObjectTargets.Damageable(target))
                {
                    string label = target.LabelShortCap;
                    if (Building(pawn, target, pace, 1f)) { Status = name + " → " + label; return; }
                }
                Miss(pawn, .6f);
                Status = name + L.T("落空", " missed");
                return;
            }

            // A swing: every creature its edge meets on the way.
            struck.Clear(); objects.Clear();
            for (int i = 0; i < SwingRays; i++)
            {
                SwingPath.At(kind, i / (float)(SwingRays - 1), out float dy, out float dp);
                V3 dir = HitMath.Direction(yaw + dy, pitch + dp), end = eye + dir * reach;
                Thing contact = First(pawn, eye, end, out float t, out HitVolume v);
                if (!(contact is Pawn p)) { if (ObjectTargets.Damageable(contact) && !objects.Contains(contact)) objects.Add(contact); continue; }
                if (p.Dead) continue;
                Struck s = struck.Find(x => x.pawn == p);
                if (s == null) { s = new Struck { pawn = p, t = t, at = V3.Lerp(eye, end, t), first = v }; struck.Add(s); }
                else if (t < s.t) { s.t = t; s.at = V3.Lerp(eye, end, t); s.first = v; }
                BodyPartRecord part = BodyRegions.ChoosePart(p, v);
                if (part != null && !s.parts.Contains(part)) s.parts.Add(part);
            }
            // Bystanders are spared: the one under the crosshair is struck whatever its side, the rest only if hostile.
            Thing aimed = First(pawn, eye, eye + ControlCamera.ShotDirection(pawn) * reach, out _, out _);
            struck.RemoveAll(s => s.pawn != aimed && !s.pawn.HostileTo(pawn));
            struck.Sort((a, b) => a.t.CompareTo(b.t));
            bool objectHit = false; string objectName = null;
            foreach (Thing target in objects)
            {
                if (target.Destroyed) continue;
                string label = target.LabelShortCap;
                if (!Building(pawn, target, pace, kind == SwingKind.Heavy ? 2f : 1f)) continue;
                objectHit = true; objectName = objectName ?? label;
            }
            if (struck.Count == 0)
            {
                if (objectHit) { Status = name + " → " + objectName; return; }
                Miss(pawn, kind == SwingKind.Heavy ? 1f : .8f); Status = name + L.T("落空", " missed"); return;
            }
            Strike(pawn, kind, pace, name);
        }

        // The weapon's damage on everything in `struck` (nearest first), after the game's hit and dodge rolls.
        private static void Strike(Pawn pawn, SwingKind kind, int pace, string name)
        {
            Pawn first = struck[0].pawn;
            Verb verb = pawn.meleeVerbs.TryGetMeleeVerb(first);
            if (verb == null) { Miss(pawn, 1f); Status = name + L.T("：无法出手", ": cannot strike"); return; }
            int hits = 0;
            string firstPart = null;
            for (int n = 0; n < struck.Count; n++)
            {
                string label = Deal(pawn, verb, struck[n], (kind == SwingKind.Heavy ? 2f : 1f) * (n == 0 ? 1f : OthersShare), kind == SwingKind.Heavy);
                if (label == null) continue;
                if (firstPart == null) firstPart = label;
                hits++;
            }
            SoundDef sound = hits > 0 ? SoundDefOf.Pawn_Melee_Punch_HitPawn : SoundDefOf.Pawn_Melee_Punch_Miss;
            sound?.PlayOneShot(new TargetInfo(first.Position, first.Map));
            if (hits > 0) pawn.skills?.Learn(SkillDefOf.Melee, kind == SwingKind.Heavy ? 250f : 200f, false, false);
            Recover(pace);
            Status = hits == 0 ? name + L.T("：被躲开", ": dodged") : name + " → " + first.LabelShortCap + L.T("（", " (") + firstPart + L.T("）", ")") + (hits > 1 ? L.T(" 等 ", " and ") + hits + L.T(" 个目标", " targets") : "");
        }

        // One victim struck: the game's hit and dodge rolls, then the weapon's damage times share on the part the edge met
        // first (split: over every part crossed). The label of the part struck, null when it missed or did nothing.
        private static string Deal(Pawn pawn, Verb verb, Struck s, float share, bool split)
        {
            Pawn victim = s.pawn;
            if (victim.Destroyed || victim.Dead || s.parts.Count == 0) return null;
            if (!victim.Downed && !Rand.Chance(pawn.GetStatValue(StatDefOf.MeleeHitChance))) { MeleeFx.Miss(s.at, L.T("未命中", "Miss")); return null; }
            if (!victim.Downed && victim.RaceProps.Humanlike && Rand.Chance(victim.GetStatValue(StatDefOf.MeleeDodgeChance)))
            { MeleeFx.Miss(s.at, L.T("闪避", "Dodged")); MoteMaker.ThrowText(victim.DrawPos, victim.Map, L.T("闪避", "Dodged"), 1.6f); return null; }
            List<DamageInfo> infos = DamageInfos(verb, victim);
            if (infos == null || infos.Count == 0) return null;
            List<BodyPartRecord> parts = split ? s.parts : new List<BodyPartRecord> { BodyRegions.ChoosePart(victim, s.first) ?? s.parts[0] };
            DamageInfo main = infos[0];
            float each = SlashMath.PerPart(main.Amount * share, parts.Count);
            for (int i = 0; i < parts.Count && !victim.Destroyed; i++)
            {
                if (victim.health.hediffSet.PartIsMissing(parts[i])) continue;
                var d = new DamageInfo(main);
                d.SetAmount(each);
                d.SetHitPart(parts[i]);
                TakeDamage(verb, victim, d);
            }
            // Extra damage the weapon carries besides its main hit (stun, burn): once, on the first part.
            for (int i = 1; i < infos.Count && !victim.Destroyed; i++)
            {
                var d = new DamageInfo(infos[i]);
                if (!victim.health.hediffSet.PartIsMissing(parts[0])) d.SetHitPart(parts[0]);
                TakeDamage(verb, victim, d);
            }
            string label = parts[0].LabelCap + (parts.Count > 1 ? " +" + (parts.Count - 1) : "");
            MeleeFx.Hit(s.at, label, s.first.Region);
            Visuals.ImpactLight(victim.Map, s.at, victim);
            Ballistics.Notify(victim, parts[0], s.first.Region, true);
            return label;
        }

        // ---- the whirlwind (横扫千军): the pawn spins with its blade out, WhirlRules.TurnsPerSecond turns a second by its
        // Melee skill, striking every hostile within WhirlRules.Radius cells once a turn, as the blade passes it. Where the
        // view points (high or low) is where the blade cuts: on each victim, the body part at that height. Drains stamina
        // (WhirlRules.Drain a second); ends when the button is let go or breath runs out.
        private static Pawn whirler;
        private static float lastRight = -10f, whirlAngle, whirlYaw;
        private static readonly Dictionary<Pawn, float> lastStruckAt = new Dictionary<Pawn, float>();

        private static int whirlFrame;
        // Spinning now (a spin the combat input stopped reaching, e.g. a weapon change, ends by itself).
        public static bool Whirling(Pawn pawn)
        {
            if (whirler == null || whirler != pawn) return false;
            if (Time.frameCount - whirlFrame > 10) { StopWhirl(pawn, null); return false; }
            return true;
        }
        public static float WhirlAngle => whirlAngle;
        public static float WhirlRadius(Pawn pawn) => MeleeReach.For(pawn, false);

        private static bool StartWhirl(Pawn pawn)
        {
            int skill = SkillOf(pawn);
            if (WhirlRules.TurnsPerSecond(skill) <= 0f) { Messages.Message(L.T("格斗不到 ", "Whirlwind needs Melee ") + WhirlRules.MinSkill + L.T(" 级，使不出横扫千军。", "."), MessageTypeDefOf.RejectInput, false); return false; }
            if (!Stances.For(pawn) || Stances.State.Exhausted) { Messages.Message(L.T("太累了，转不起来。", "Too tired to spin."), MessageTypeDefOf.RejectInput, false); return false; }
            whirler = pawn; whirlAngle = 0f; whirlYaw = FirstPerson.Yaw; whirlFrame = Time.frameCount;
            lastStruckAt.Clear();
            WeaponView.Melee(SwingKind.Sweep);
            Log.Message("[FpsBridge] WHIRL_START pawn=" + pawn.thingIDNumber + "; skill=" + skill + "; turns=" + WhirlRules.TurnsPerSecond(skill));
            return true;
        }

        private static void StopWhirl(Pawn pawn, string why)
        {
            if (whirler == null) return;
            whirler = null;
            Recover(CooldownRules.MeleeTicks(SkillOf(pawn)));
            if (why != null) Status = L.T("横扫千军 · ", "Whirlwind · ") + why;
        }

        // Once per frame while spinning; false to stop.
        private static bool WhirlFrame(Pawn pawn, Verb verb)
        {
            if (Find.TickManager.Paused) return true;
            float dt = Mathf.Min(Time.deltaTime, .1f) * Find.TickManager.TickRateMultiplier * CyberSkills.PlayerTime;
            if (!Stances.State.Drain(WhirlRules.Drain, dt)) { Messages.Message(L.T("体力耗尽，停下了旋转。", "Out of stamina, the spin stops."), MessageTypeDefOf.RejectInput, false); Status = L.T("横扫千军 · 体力耗尽", "Whirlwind · out of stamina"); return false; }
            float turns = WhirlRules.TurnsPerSecond(SkillOf(pawn));
            float before = whirlAngle;
            whirlAngle += 360f * turns * dt;
            // The blade across the view once a turn.
            if (Mathf.FloorToInt(whirlAngle / 360f) != Mathf.FloorToInt(before / 360f)) WeaponView.Melee(SwingKind.Sweep);
            V3 eye = Ballistics.Eye(pawn), at = FloorSpace.Spot(pawn);
            float pitch = ControlCamera.AttackPitch(pawn) * Mathf.Deg2Rad;
            Map map = pawn.Map;
            float radius = WhirlRadius(pawn);
            foreach (IntVec3 c in GenRadial.RadialCellsAround(pawn.Position, radius + 1.5f, true))
            {
                if (!c.InBounds(map)) continue;
                List<Thing> things = c.GetThingList(map);
                for (int k = 0; k < things.Count; k++)
                {
                    // Hostile and neutral (no faction: wild animals, wanderers) are caught; own side and friends are not.
                    if (!(things[k] is Pawn victim) || victim == pawn || victim.Dead || !(victim.HostileTo(pawn) || Neutral(victim))) continue;
                    V3 spot = FloorSpace.Spot(victim);
                    float dx = spot.X - at.X, dz = spot.Z - at.Z, dist = Mathf.Sqrt(dx * dx + dz * dz);
                    if (dist > radius + .5f * victim.BodySize || Mathf.Abs(spot.Y - at.Y) > 1f) continue;
                    // Struck when the blade's angle sweeps past the victim's bearing (once a turn).
                    float bearing = Mathf.Repeat(Mathf.Atan2(dx, dz) * Mathf.Rad2Deg - whirlYaw, 360f);
                    float from = Mathf.Repeat(before, 360f), to = from + (whirlAngle - before);
                    bool passed = (bearing > from && bearing <= to) || (bearing + 360f > from && bearing + 360f <= to);
                    if (!passed || (lastStruckAt.TryGetValue(victim, out float last) && whirlAngle - last < 300f)) continue;
                    lastStruckAt[victim] = whirlAngle;
                    Struck s = AimedAt(victim, eye, pitch, dist);
                    if (s != null) Deal(pawn, verb, s, 1f, false);
                }
            }
            Status = L.T("横扫千军 · 每秒 ", "Whirlwind · ") + turns.ToString("0.#") + L.T(" 圈 · 松开右键收势", " turns/s · release RMB to stop");
            return true;
        }

        // Neutral as the friend-or-foe marks have it (a yellow dot): no faction, not a prisoner or guest of the colony.
        private static bool Neutral(Pawn p) => p.Faction == null && !p.IsPrisonerOfColony && p.HostFaction == null;

        // The blade's height where it meets a victim at this distance, from the view's pitch: the part there.
        private static Struck AimedAt(Pawn victim, V3 eye, float pitch, float dist)
        {
            volumes.Clear();
            BodyRegions.AddVolumes(victim, volumes);
            if (volumes.Count == 0) return null;
            V3 spot = FloorSpace.Spot(victim);
            float top = spot.Y + Bindings.PickHeight(victim);
            float y = Mathf.Clamp(eye.Y - Mathf.Tan(pitch) * dist, spot.Y + .05f, top);
            V3 point = new V3(spot.X, y, spot.Z);
            int i = FigureMeshes.NearestVolume(volumes, 0, point);
            if (i < 0) return null;
            var s = new Struck { pawn = victim, at = point, first = volumes[i] };
            BodyPartRecord part = BodyRegions.ChoosePart(victim, volumes[i]);
            if (part == null) return null;
            s.parts.Add(part);
            return s;
        }

        // A thrust into a solid building: the weapon's own damage.
        internal static bool Building(Pawn pawn, Thing target, int pace, float share)
        {
            if (!ObjectTargets.Damageable(target)) return false;
            // Contact is decided by our 3D ray, not a native attack job or adjacent-cell hit check.
            Verb verb = pawn.equipment?.PrimaryEq?.PrimaryVerb;
            if (!HasDamageVerb(verb)) verb = pawn.meleeVerbs?.TryGetMeleeVerb(target);
            List<DamageInfo> infos = verb != null ? DamageInfos(verb, target) : null;
            if (infos == null || infos.Count == 0) return false;
            var at = new TargetInfo(target.Position, target.Map);
            ApplyObjectDamage(verb, target, infos, share);
            (DefDatabase<SoundDef>.GetNamedSilentFail("Pawn_Melee_Punch_HitBuilding_Generic") ?? SoundDefOf.Pawn_Melee_Punch_HitPawn)?.PlayOneShot(at);
            Recover(pace);
            return true;
        }
        private static void ApplyObjectDamage(Verb verb, Thing target, List<DamageInfo> infos, float share)
        {
            if (!ObjectTargets.Damageable(target) || infos == null || !(share > 0f)) return;
            foreach (DamageInfo info in infos)
            {
                if (target.Destroyed) break;
                var d = new DamageInfo(info); d.SetAmount(d.Amount * share); TakeDamage(verb, target, d);
            }
        }

        private static void Miss(Pawn pawn, float factor)
        {
            SoundDefOf.Pawn_Melee_Punch_Miss?.PlayOneShot(new TargetInfo(pawn.Position, pawn.Map));
            Recover(Mathf.RoundToInt(CooldownRules.MeleeTicks(SkillOf(pawn)) * factor));
        }

        // Hit and run: the next blow waits out the pace, but the pawn is never put in the game's cooldown stance (which
        // freezes movement under Perspective Shift), so it keeps moving through and after every strike.
        private static void Recover(int ticks) =>
            recoverUntil = Find.TickManager.TicksGame + Math.Max(1, Mathf.RoundToInt(CooldownRules.Scale(ticks, FpsMod.Settings.cooldownMultiplier) * CyberSkills.RecoverFactor(Bridge.ShootingPawn)));

        // The weapon's damage for this target, as the game's melee verb would deal it (private in 1.7: reflection).
        internal static List<DamageInfo> DamageInfos(Verb verb, Thing target)
        {
            if (IsCeMelee(verb))
            {
                if (CeDamageInfos == null || CurrentTarget == null || CeLastAttack?.GetSetMethod(true) == null)
                {
                    Log.WarningOnce("[FpsBridge] CE melee damage contract could not be bound.", 19271823);
                    return null;
                }
                var hit = new LocalTargetInfo(target);
                object previous = CurrentTarget.GetValue(verb);
                CurrentTarget.SetValue(verb, hit);
                try
                {
                    // CE generates DamageInfo lazily and reads CurrentTarget for its hit-height rules.
                    return CeDamageInfos.Invoke(verb, new object[] { hit, false }) is IEnumerable<DamageInfo> ceDamage
                        ? new List<DamageInfo>(ceDamage) : null;
                }
                finally { CurrentTarget.SetValue(verb, previous); }
            }
            if (!(verb is Verb_MeleeAttackDamage)) return null;
            if (!damageInfosLooked)
            {
                damageInfosLooked = true;
                damageInfos = AccessTools.Method(typeof(Verb_MeleeAttackDamage), "DamageInfosToApply", new[] { typeof(LocalTargetInfo) });
                if (damageInfos == null) Log.Warning("[FpsBridge] Verb_MeleeAttackDamage.DamageInfosToApply not found: melee damage is off.");
            }
            if (damageInfos == null) return null;
            var list = new List<DamageInfo>();
            if (damageInfos.Invoke(verb, new object[] { new LocalTargetInfo(target) }) is IEnumerable<DamageInfo> all)
                foreach (DamageInfo d in all) list.Add(d);
            return list;
        }

        private static bool IsCeMelee(Verb verb) => verb != null && CeMeleeVerb?.IsInstanceOfType(verb) == true;
        internal static bool HasDamageVerb(Verb verb) => verb is Verb_MeleeAttackDamage || IsCeMelee(verb);

        internal static void TakeDamage(Verb verb, Thing target, DamageInfo info)
        {
            if (!IsCeMelee(verb)) { target.TakeDamage(info); return; }
            // CE reads LastAttackVerb during armor deflection to get blunt penetration.
            object previous = CeLastAttack.GetValue(null);
            CeLastAttack.SetValue(null, verb);
            try { target.TakeDamage(info); }
            finally { CeLastAttack.SetValue(null, previous); }
        }

        // The first creature or solid building whose volume the segment a..b crosses.
        internal static Thing First(Pawn self, V3 a, V3 b, out float best, out HitVolume hit)
        {
            best = float.PositiveInfinity; hit = default;
            Thing found = null;
            Map map = self.Map;
            seen.Clear();
            IntVec3 centre = self.Position;
            V3 ground = FloorSpace.Spot(self);
            int around = MeleeReachRules.SearchRadius((b - a).Length);
            for (int dx = -around; dx <= around; dx++) for (int dz = -around; dz <= around; dz++)
            foreach (FloorBand band in FloorSpace.Bands(map))
            {
                int z = Mathf.FloorToInt(ground.Z) + dz;
                if (z < 0 || z >= band.Rows) continue;
                IntVec3 c = new IntVec3(centre.x + dx, 0, z + band.Shift);
                if (!c.InBounds(map)) continue;
                List<Thing> things = map.thingGrid.ThingsListAtFast(c);
                for (int k = 0; k < things.Count; k++)
                {
                    Thing thing = things[k];
                    if (thing == self || MountedControl.OwnMount(self, thing) || !seen.Add(thing) || !thing.Spawned) continue;
                    volumes.Clear();
                    if (thing is Pawn pawn) BodyRegions.AddVolumes(pawn, volumes);
                    else ObjectTargets.Volumes(thing, volumes);
                    if (volumes.Count == 0) continue;
                    int i = HitMath.Nearest(a, b, volumes, out float t);
                    if (i < 0 || t >= best) continue;
                    best = t; hit = volumes[i]; found = thing;
                }
            }
            if (FloorSpace.PlaneContact(map, a, b, ref best, out _)) return null;
            return found;
        }
    }
}
