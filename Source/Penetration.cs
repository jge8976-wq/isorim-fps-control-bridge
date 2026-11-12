using System;
using System.Collections.Generic;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // 0.6.0 penetration, game side: contact collection along a segment, the non-destroying damage adapter (armour
    // snapshot, fixed body part, no damage propagation, shield result), obstacle classes and the end of a round.
    // Rules and ordering live in PenetrationRules (engine-free, tested offline).
    internal static partial class Ballistics
    {
        private struct ContactInfo { public Thing thing; public BodyPartRecord part; public HitRegion region; public CeStructureRoof.Contact? roof; }

        private static readonly List<Contact> contacts = new List<Contact>();
        private static readonly List<ContactInfo> infos = new List<ContactInfo>();
        private static readonly List<FloorSpace.PlaneHit> planes = new List<FloorSpace.PlaneHit>();
        private static readonly HashSet<long> livePawns = new HashSet<long>();
        private static readonly List<float> layers = new List<float>();
        private static readonly HashSet<ThingDef> legacyNoted = new HashSet<ThingDef>();
        private static readonly PenSink sink = new PenSink();
        private static AccessTools.FieldRef<Projectile, QualityCategory> qualityRef;
        private static AccessTools.FieldRef<Projectile, LocalTargetInfo> intendedRef;
        private static AccessTools.FieldRef<Projectile, ThingDef> coverRef;
        private static Pawn shieldWatch;
        private static bool shieldAbsorbed;
        private static float nextDiagnostic;

        public static string LastStop { get; private set; }

        // Called from Install (same Harmony instance as the flight hooks).
        private static void InstallPenetration(Harmony harmony)
        {
            qualityRef = AccessTools.FieldRefAccess<Projectile, QualityCategory>("equipmentQuality");
            intendedRef = AccessTools.FieldRefAccess<Projectile, LocalTargetInfo>("intendedTarget");
            coverRef = AccessTools.FieldRefAccess<Projectile, ThingDef>("targetCoverDef");
            var pre = AccessTools.DeclaredMethod(typeof(Pawn), "PreApplyDamage", new[] { typeof(DamageInfo).MakeByRefType(), typeof(bool).MakeByRefType() })
                ?? throw new MissingMethodException("Pawn.PreApplyDamage(ref DamageInfo, out bool)");
            harmony.Patch(pre, postfix: new HarmonyMethod(typeof(Ballistics), nameof(AfterPreApplyDamage)) { priority = Priority.Last });
        }

        // Only inside the bridge's own penetrating damage call: did the game's shield (or anything else) absorb it?
        public static void AfterPreApplyDamage(Pawn __instance, ref bool absorbed)
        {
            if (shieldWatch != null && __instance == shieldWatch && absorbed) shieldAbsorbed = true;
        }

        // ---- eligibility

        private static bool StandardImpact(ThingDef projectile) => projectile?.thingClass == typeof(Bullet) || projectile?.thingClass == typeof(Beam);

        internal static PenetrationGroup PenetrationGroupOf(Verb_LaunchProjectile verb)
        {
            ThingDef def = verb?.Projectile;
            if (def?.projectile == null) return PenetrationGroup.Legacy;
            bool standard = StandardImpact(def);
            if (!standard && typeof(Bullet).IsAssignableFrom(def.thingClass) && legacyNoted.Add(def))
                Log.Message("[FpsBridge] PENETRATION_COMPAT " + def.defName + " uses " + def.thingClass.Name + " (custom impact): keeps the stop-at-first-contact flow.");
            return PenetrationRules.Classify(verb.EquipmentSource?.def.defName, verb.verbProps.burstShotCount, standard);
        }

        private static PenetrationState NewPenetration(Projectile p, Verb_LaunchProjectile verb)
        {
            PenetrationGroup group = PenetrationGroupOf(verb);
            if (group == PenetrationGroup.Legacy) return null;
            ThingDef weapon = verb.EquipmentSource?.def;
            bool shotgun = group == PenetrationGroup.Single && PenetrationRules.IsShotgun(weapon?.defName, weapon?.graphicData?.texPath);
            // The projectile's own figures after launch: weapon quality, unique traits and equipment multipliers included.
            return new PenetrationState(group, p.DamageAmount, p.ArmorPenetration, shotgun, FpsMod.Settings.ObstacleArmor);
        }

        private static void AttachPenetration(Projectile p, Verb_LaunchProjectile verb)
        {
            if (p == null || !shots.TryGetValue(p, out Shot shot)) return;
            shot.pen = NewPenetration(p, verb);
            if (shot.pen != null) shot.envelopes = new Dictionary<long, HitVolume>();
        }

        private static bool FirePenetratingBeam(Pawn pawn, Verb_LaunchProjectile verb, Shot shot, V3 start, V3 end, LocalTargetInfo intended)
        {
            Map map = pawn.Map;
            ThingDef def = verb.Projectile;
            IntVec3 startCell = FloorSpace.Cell(map, start);
            if (!startCell.InBounds(map)) return false;
            Projectile p = (Projectile)GenSpawn.Spawn(def, pawn.Position, map, WipeMode.Vanish);
            try
            {
                IntVec3 far = FloorSpace.Cell(map, end).ClampInsideMap(map);
                Vector3 origin = FloorSpace.Logical(map, start, startCell); origin.y = def.Altitude;
                LaunchBase(p, pawn, origin, new LocalTargetInfo(far), intended, ProjectileHitFlags.None, false, verb.EquipmentSource, null);
                FloorSpace.OwnFlight(p);
                shot.pen = NewPenetration(p, verb);
                shot.envelopes = new Dictionary<long, HitVolume>();
                shots[p] = shot;
                // One instantaneous ray: no speed, no gravity; the same contact rules as a round.
                bool stopped = PenetrateSegment(p, shot, start, end, out float t);
                V3 at = stopped ? V3.Lerp(start, end, t) : end;
                shot.pos = at;
                V3 visualStart = Hand(pawn);
                if ((at - start).Length < .5f) visualStart = start;
                beams.Add(new BeamFlash { map = map, start = visualStart, end = at, until = Time.unscaledTime + .12f });
                if (!stopped) shot.pen.Stop = StopReason.World;
                Diagnose(p, shot);
                return true;
            }
            finally
            {
                shots.Remove(p);
                if (!p.Destroyed) p.Destroy(DestroyMode.Vanish);
            }
        }

        // ---- one segment

        private static bool PenetrateSegment(Projectile p, Shot shot, V3 a, V3 b, out float stopT)
        {
            stopT = 1f;
            Map map = p.Map;
            if (map == null) return false;
            Collect(map, shot, a, b);
            sink.Bind(p, shot, a, b);
            try { return PenetrationWalker.Process(contacts, shot.pen, sink, out stopT); }
            finally { sink.Bind(null, null, a, b); }
        }

        private static void Collect(Map map, Shot shot, V3 a, V3 b)
        {
            contacts.Clear(); infos.Clear(); seen.Clear(); livePawns.Clear();
            PenetrationState s = shot.pen;
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
                        if (thing == shot.launcher || MountedControl.OwnMount(shot.launcher,thing) || !seen.Add(thing) || thing.Destroyed || !thing.Spawned) continue;
                        if (thing is Pawn vehicle && VehicleApi.IsVehicle(vehicle)) CollectVehicle(vehicle, a, b);
                        else if (thing is Pawn pawn) CollectPawn(pawn, shot, a, b);
                        else CollectSolid(thing, s, a, b);
                    }
                }
            }
            if (AerialFlight.Store != null)
                foreach (var flight in AerialFlight.Store.Flights)
                    if (flight.Key != shot.launcher && flight.Key.Spawned && flight.Key.Map == map && seen.Add(flight.Key)) CollectVehicle(flight.Key, a, b);
            // A pawn this round is still inside but which vanished (killed, carried off): leave it where it was last seen.
            foreach (long id in s.Pawns)
            {
                if (!s.Open(id) || livePawns.Contains(id) || !shot.envelopes.TryGetValue(id, out HitVolume env)) continue;
                float exit = HitMath.Span(a, b, env, out _, out float tOut) ? tOut : 0f;
                if (exit <= 1f) contacts.Add(new Contact { T = exit, Kind = ContactKind.PawnExit, Entity = id, Ref = -1 });
            }
            planes.Clear();
            FloorSpace.PlaneCrossings(map, a, b, planes);
            long preciseRoofId = 0;
            if (RoofSegmentContact.Try(map, a, b, 1, out float roofT, out var roofCell)
                && CeStructureRoof.TryContact(map, a, b, roofT, out var roofHit) && roofHit.Breakable)
            {
                FloorBand roofBand = FloorSpace.Band(map, roofCell);
                preciseRoofId = -(1L + (((long)(roofBand.Level + 1 + 64) & 0xFF) << 40 | ((long)roofCell.x & 0xFFFFF) << 20 | ((long)(roofCell.z - roofBand.Shift) & 0xFFFFF)));
                infos.Add(new ContactInfo { roof = roofHit });
                contacts.Add(new Contact { T = roofT, Kind = ContactKind.SlabCross, Entity = preciseRoofId,
                    Obstacle = roofHit.Roof.isNatural ? ObstacleClass.NaturalRock : ObstacleClass.Wood, Ref = infos.Count - 1 });
            }
            foreach (FloorSpace.PlaneHit h in planes)
            {
                if (h.Bottom) { contacts.Add(new Contact { T = h.T, Kind = ContactKind.Terminal, Reason = StopReason.Ground, Ref = -1 }); continue; }
                long id = -(1L + (((long)(h.Level + 64) & 0xFF) << 40 | ((long)h.X & 0xFFFFF) << 20 | ((long)h.Z & 0xFFFFF)));
                if (id == preciseRoofId) continue;
                contacts.Add(new Contact { T = h.T, Kind = ContactKind.SlabCross, Entity = id, Obstacle = h.Thick ? ObstacleClass.NaturalRock : h.Roof ? ObstacleClass.Wood : ObstacleClass.Slab, Ref = -1 });   // a thin roof: light, as wood
            }
        }

        private static void CollectPawn(Pawn pawn, Shot shot, V3 a, V3 b)
        {
            volumes.Clear();
            BodyRegions.AddVolumes(pawn, volumes);
            if (volumes.Count == 0 || !BodyVolumes.Envelope(volumes, 0, pawn.Rotation.AsAngle, out HitVolume env)) return;
            long id = pawn.thingIDNumber;
            livePawns.Add(id);
            shot.envelopes[id] = env;
            if (!HitMath.Span(a, b, env, out _, out float envOut)) return;
            // 0.8.1: a drawn pawn is entered only where the segment meets its model; parts count from that surface on,
            // and a hit on apparel/headgear no part volume covers lands on the nearest part.
            float surface = 0f;
            if (FigureMeshes.Has(pawn))
            {
                if (!FigureMeshes.Hit(pawn, a, b, out surface, out _))
                {
                    // Past the model: no part, but a round already inside still leaves it here.
                    if (envOut <= 1f) contacts.Add(new Contact { T = envOut, Kind = ContactKind.PawnExit, Entity = id, Ref = -1 });
                    return;
                }
                int near = FigureMeshes.NearestVolume(volumes, 0, V3.Lerp(a, b, surface));
                bool any = false;
                for (int i = 0; i < volumes.Count && !any; i++)
                    any = volumes[i].Part >= 0 && HitMath.Span(a, b, volumes[i], out _, out float o) && o >= surface;
                if (!any && near >= 0 && BodyRegions.PartOf(pawn, volumes[near]) is BodyPartRecord nearPart)
                {
                    infos.Add(new ContactInfo { thing = pawn, part = nearPart, region = volumes[near].Region });
                    contacts.Add(new Contact { T = surface, Kind = ContactKind.PartEnter, Entity = id, Part = volumes[near].Part, Ref = infos.Count - 1 });
                }
            }
            if(shot.homing==pawn&&CyberSkills.HeadPart(pawn) is BodyPartRecord head)
            {
                int index=pawn.RaceProps.body.AllParts.IndexOf(head);
                infos.Add(new ContactInfo{thing=pawn,part=head,region=HitRegion.Head});
                contacts.Add(new Contact{T=surface,Kind=ContactKind.PartEnter,Entity=id,Part=index,Ref=infos.Count-1});
                if(envOut<=1f)contacts.Add(new Contact{T=envOut,Kind=ContactKind.PawnExit,Entity=id,Ref=-1});
                return; // One head contact, rather than remapping several body-part contacts onto the same head.
            }
            for (int i = 0; i < volumes.Count; i++)
            {
                HitVolume v = volumes[i];
                if (v.Part < 0 || !HitMath.Span(a, b, v, out float tIn, out float tOut) || tOut < surface) continue;
                BodyPartRecord part = BodyRegions.PartOf(pawn, v);
                if (part == null) continue;
                infos.Add(new ContactInfo { thing = pawn, part = part, region = v.Region });
                contacts.Add(new Contact { T = Math.Max(tIn, surface), Kind = ContactKind.PartEnter, Entity = id, Part = v.Part, Ref = infos.Count - 1 });
            }
            if (envOut <= 1f) contacts.Add(new Contact { T = envOut, Kind = ContactKind.PawnExit, Entity = id, Ref = -1 });
        }

        private static void CollectSolid(Thing thing, PenetrationState s, V3 a, V3 b)
        {
            volumes.Clear();
            AddSolid(thing, volumes);
            if (volumes.Count == 0) return;
            long id = thing.thingIDNumber;
            float tIn = float.PositiveInfinity, tOut = 0f;
            bool any = false;
            for (int i = 0; i < volumes.Count; i++)
            {
                if (!HitMath.Span(a, b, volumes[i], out float vi, out float vo)) continue;
                any = true; tIn = Math.Min(tIn, vi); tOut = Math.Max(tOut, vo);
            }
            if (!any) return;
            infos.Add(new ContactInfo { thing = thing, region = HitRegion.Solid });
            ObstacleClass cls = ObstacleOf(thing);
            if (!s.Obstacles.Contains(id)) contacts.Add(new Contact { T = tIn, Kind = ContactKind.ObstacleEnter, Entity = id, Obstacle = cls, Ref = infos.Count - 1 });
            if (tOut <= 1f) contacts.Add(new Contact { T = tOut, Kind = ContactKind.ObstacleExit, Entity = id, Obstacle = cls, Ref = infos.Count - 1 });
        }

        // Obstacle classes (0.6.0 table). Material decides only for walls and doors: metal or stone furniture is ordinary.
        internal static ObstacleClass ObstacleOf(Thing thing)
        {
            ThingDef def = thing.def;
            if (def.category == ThingCategory.Item)
            {
                if (def == ThingDefOf.ChunkSlagSteel || (def.defName != null && def.defName.IndexOf("Slag", StringComparison.OrdinalIgnoreCase) >= 0)) return ObstacleClass.Slag;
                return ObstacleClass.StoneChunk;
            }
            // Trees: a light wooden obstacle (1 credit, the wood armour value).
            if (def.category == ThingCategory.Plant && def.plant != null && def.plant.IsTree) return ObstacleClass.Wood;
            if (def.category != ThingCategory.Building) return ObstacleClass.Barrier;   // anything else unrecognised: stop as before
            if (def.building != null && def.building.isNaturalRock && !def.IsSmoothed) return ObstacleClass.NaturalRock;
            if (def.IsSmoothed) return ObstacleClass.StoneWall;
            List<StuffCategoryDef> stuff = thing.Stuff?.stuffProps?.categories;
            if (stuff != null && stuff.Contains(StuffCategoryDefOf.Woody)) return ObstacleClass.Wood;
            if (def.IsWall || def.IsDoor)
            {
                if (stuff != null && stuff.Contains(StuffCategoryDefOf.Metallic)) return ObstacleClass.MetalWall;
                if (stuff != null && stuff.Contains(StuffCategoryDefOf.Stony)) return ObstacleClass.StoneWall;
                return ObstacleClass.Barrier;   // a wall of unknown make: keep its blocking
            }
            return ObstacleClass.Cover;
        }

        // ---- the end of a penetrating round

        private static void FinishPenetration(Projectile p, Shot shot)
        {
            StopReason reason = shot.pen.Stop;
            Diagnose(p, shot);
            if (reason == StopReason.Ground || ((reason == StopReason.Armor || reason == StopReason.Credits) && sink.LastWasSlab))
            {
                // The ground (or a slab it could not pass): the game's own miss effects, sound and dirt.
                Settle(p, shot, null);
                shots.Remove(p);
                impact.Invoke(p, new object[] { null, false });
                return;
            }
            Settle(p, shot, sink.LastThing);
            shots.Remove(p);
            if (!p.Destroyed) p.Destroy(DestroyMode.Vanish);
        }

        private static void Diagnose(Projectile p, Shot shot)
        {
            PenetrationState s = shot.pen;
            LastStop = s.Stop + L.T(" · 人数 ", " · pawns ") + s.Pawns.Count + "/" + s.PawnCap + L.T(" · 余额 ", " · budget ") + s.Credits + L.T(" · 穿甲 ", " · AP ") + s.P.ToString("0.###");
            if (!FpsMod.Settings.penetrationDiagnostics || Time.unscaledTime < nextDiagnostic) return;
            nextDiagnostic = Time.unscaledTime + .25f;
            Log.Message("[FpsBridge] PENETRATION_END " + p.def.defName + " group=" + s.Group + " stop=" + s.Stop + " D0=" + s.D0 + " P0=" + s.P0.ToString("0.###")
                + " P=" + s.P.ToString("0.###") + " pawns=" + s.Pawns.Count + "/" + s.PawnCap + " parts=" + s.Parts.Count + " credits=" + s.Credits);
        }

        // ---- damage adapter

        private static float ArmorAt(Pawn pawn, BodyPartRecord part, DamageDef def)
        {
            StatDef stat = def?.armorCategory?.armorRatingStat;
            if (stat == null) return 0f;
            layers.Clear();
            List<Apparel> worn = pawn.apparel?.WornApparel;
            if (worn != null)
                for (int i = 0; i < worn.Count; i++)
                    if (worn[i].def.apparel != null && worn[i].def.apparel.CoversBodyPart(part)) layers.Add(worn[i].GetStatValue(stat, true));
            return PenetrationRules.EffectiveArmor(pawn.GetStatValue(stat, true), layers);
        }

        private sealed class PenSink : IPenetrationSink
        {
            private Projectile p;
            private Shot shot;
            private V3 a, b;
            public Thing LastThing;
            public bool LastWasSlab;

            public void Bind(Projectile projectile, Shot s, V3 from, V3 to)
            {
                p = projectile; shot = s; a = from; b = to;
                if (projectile != null) { LastThing = null; LastWasSlab = false; }
            }

            private float Angle => Mathf.Atan2(shot.vel.X, shot.vel.Z) * Mathf.Rad2Deg;
            private bool Guilty => !(p.Launcher is Pawn l) || !l.Drafted;

            private DamageInfo Damage(DamageDef def, float amount, float penetration, BodyPartRecord part) =>
                new DamageInfo(def, amount, penetration, Angle, p.Launcher, part, p.EquipmentDef, DamageInfo.SourceCategory.ThingOrUnknown,
                    intendedRef(p).Thing, Guilty, true, qualityRef(p), true, false);

            private BattleLogEntry_RangedImpact LogFor(Thing target)
            {
                var logs = shot.logs ?? (shot.logs = new Dictionary<long, BattleLogEntry_RangedImpact>());
                if (logs.TryGetValue(target.thingIDNumber, out var entry)) return entry;
                entry = new BattleLogEntry_RangedImpact(p.Launcher, target, intendedRef(p).Thing, p.EquipmentDef, p.def, coverRef(p));
                Find.BattleLog.Add(entry);
                logs[target.thingIDNumber] = entry;
                return entry;
            }

            private void Light(float t, Thing thing)
            {
                V3 at = V3.Lerp(a, b, t);
                Map map = p.Map;
                IntVec3 cell = FloorSpace.ContactCell(map, at, shot.vel.Y, thing);
                if (!cell.InBounds(map)) return;
                Vector3 logical = FloorSpace.Logical(map, at, cell);
                Visuals.ImpactLight(map, new V3(logical.x, at.Y - FloorSpace.Band(map, cell).Height, logical.z), thing);
            }

            public bool HitPart(in Contact c, float damage, float penetration, bool firstOnPawn, out float armor, out bool absorbed)
            {
                armor = 0f; absorbed = false;
                ContactInfo info = c.Ref >= 0 && c.Ref < infos.Count ? infos[c.Ref] : default;
                if(info.thing==shot.homing&&info.thing is Pawn eyeTarget)
                {info.part=CyberSkills.HeadPart(eyeTarget)??info.part;info.region=HitRegion.Head;}
                if (!(info.thing is Pawn pawn) || pawn.Destroyed || !pawn.Spawned || pawn.Dead || info.part == null
                    || pawn.health.hediffSet.PartIsMissing(info.part)) return false;
                DamageDef def = p.DamageDef;
                armor = ArmorAt(pawn, info.part, def);     // before this hit: a shredded vest does not let the round through
                DamageInfo d = Damage(def, damage, penetration, info.part);
                d.SetAllowDamagePropagation(false);
                BattleLogEntry_RangedImpact entry = LogFor(pawn);
                Pawn outerWatch = shieldWatch; bool outerAbsorbed = shieldAbsorbed;
                shieldWatch = pawn; shieldAbsorbed = false;
                DamageWorker.DamageResult result;
                try { result = pawn.TakeDamage(d); }
                finally { absorbed = shieldAbsorbed; shieldWatch = outerWatch; shieldAbsorbed = outerAbsorbed; }
                result?.AssociateWithLog(entry);
                LastThing = pawn; LastWasSlab = false;
                Light(c.T, pawn);
                if (firstOnPawn)
                {
                    Notify(pawn, info.part, info.region);
                    if (!absorbed && !pawn.Dead && pawn.stances?.stagger != null && p is Bullet bullet) pawn.stances.stagger.Notify_BulletImpact(bullet);
                    // The game's extra damage: once per target, at its first hit, by its own chance; never a new chain.
                    if (!absorbed && p.ExtraDamages != null)
                        foreach (ExtraDamage extra in p.ExtraDamages)
                        {
                            if (pawn.Dead || pawn.Destroyed || !Rand.Chance(extra.chance)) continue;
                            DamageInfo e = new DamageInfo(extra.def, extra.amount, extra.AdjustedArmorPenetration(), Angle, p.Launcher, null, p.EquipmentDef,
                                DamageInfo.SourceCategory.ThingOrUnknown, intendedRef(p).Thing, Guilty, true, qualityRef(p), true, false);
                            pawn.TakeDamage(e)?.AssociateWithLog(entry);
                        }
                }
                return true;
            }

            public void HitObstacle(in Contact c, float penetration)
            {
                LastWasSlab = c.Kind == ContactKind.SlabCross;
                if (c.Ref < 0 || c.Ref >= infos.Count)
                {
                    LastThing = null; Light(c.T, null);
                    if (LastWasSlab && CeStructureRoof.TryContact(p.Map, a, b, c.T, out var roof)) CeStructureRoof.Hit(p.Map, roof, RoofHealth.ProjectileDamage(p));
                    return;
                }
                if (infos[c.Ref].roof is CeStructureRoof.Contact roofContact)
                { LastThing = null; Light(c.T, null); CeStructureRoof.Hit(p.Map, roofContact, RoofHealth.ProjectileDamage(p)); return; }
                Thing thing = infos[c.Ref].thing;
                LastThing = thing;
                Light(c.T, thing);
                if (thing == null || thing.Destroyed || !thing.Spawned) return;
                BattleLogEntry_RangedImpact entry = LogFor(thing);
                float wear = thing is Pawn ? 1f : CoverWear.Factor(ObstacleOf(thing));
                thing.TakeDamage(Damage(p.DamageDef, shot.pen.D0 * wear, penetration, null))?.AssociateWithLog(entry);
            }
        }
    }
}
