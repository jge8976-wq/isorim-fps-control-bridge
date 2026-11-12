using System;
using System.Collections.Generic;

namespace HSK.FpsBridge
{
    // 0.6.0 penetration: engine-free rules and the ordered contact walker, exercised by the offline contracts.
    // Units are RimWorld's own (damage amount, armour penetration and armour rating as fractions, 1 = 100%).

    // Basic (2026-10-23): every other ordinary round (burst guns): one obstacle it out-penetrates, then it stops; it stops
    // in the first body part it hits, as before.
    internal enum PenetrationGroup { Legacy, Single, Charge, Basic }

    internal enum ObstacleClass { Wood, Cover, StoneChunk, Slag, MetalWall, StoneWall, NaturalRock, Slab, Barrier }

    internal enum StopReason { None, Armor, Shield, Credits, PawnCap, Ground, World, Barrier, Incompatible, EventLimit, Invalid }

    // The eight obstacle armour values (Mod settings), snapshotted per projectile when it is fired.
    internal struct ObstacleArmor
    {
        public float Wood, Cover, StoneChunk, Slag, MetalWall, StoneWall, NaturalRock, Slab;

        public static ObstacleArmor Default => new ObstacleArmor
        { Wood = .10f, Cover = .15f, StoneChunk = .20f, Slag = .25f, MetalWall = .30f, StoneWall = .35f, NaturalRock = .40f, Slab = .35f };

        public const float Minimum = 0f, Maximum = 2f;

        public static float Safe(float value, float fallback) => !float.IsFinite(value) ? fallback : Math.Clamp(value, Minimum, Maximum);

        public ObstacleArmor Normalized()
        {
            ObstacleArmor d = Default;
            return new ObstacleArmor
            {
                Wood = Safe(Wood, d.Wood), Cover = Safe(Cover, d.Cover), StoneChunk = Safe(StoneChunk, d.StoneChunk), Slag = Safe(Slag, d.Slag),
                MetalWall = Safe(MetalWall, d.MetalWall), StoneWall = Safe(StoneWall, d.StoneWall), NaturalRock = Safe(NaturalRock, d.NaturalRock), Slab = Safe(Slab, d.Slab)
            };
        }

        public float Get(ObstacleClass c)
        {
            switch (c)
            {
                case ObstacleClass.Wood: return Wood;
                case ObstacleClass.Cover: return Cover;
                case ObstacleClass.StoneChunk: return StoneChunk;
                case ObstacleClass.Slag: return Slag;
                case ObstacleClass.MetalWall: return MetalWall;
                case ObstacleClass.StoneWall: return StoneWall;
                case ObstacleClass.NaturalRock: return NaturalRock;
                case ObstacleClass.Slab: return Slab;
                default: return float.PositiveInfinity;
            }
        }
    }

    internal static class PenetrationRules
    {
        public const float FollowUpPartFactor = .6f;   // later, different parts of the same pawn
        public const float ExitRetain = .8f;           // armour penetration kept after leaving a pawn or an obstacle
        public const int MaxPawns = 3, ShotgunMaxPawns = 2;
        public const int MaxEventsPerSegment = 128;

        // Charge and pulse weapons penetrate in every fire mode (2 obstacle credits).
        public static readonly string[] ChargeWeapons =
        {
            "Gun_ChargeRifle", "Gun_ChargeRifle_Unique", "Gun_ChargeLance", "Gun_ChargeLance_Unique", "Gun_BeamRepeater", "Gun_BeamRepeater_Unique"
        };

        // nativeBurst: the weapon verb's own burstShotCount (never the current B mode). standardImpact: the projectile
        // uses the game's own Bullet/Beam impact (custom Impact classes, explosives and special shells keep the old flow).
        public static PenetrationGroup Classify(string weaponDefName, int nativeBurst, bool standardImpact)
        {
            if (!standardImpact) return PenetrationGroup.Legacy;
            if (weaponDefName != null && Array.IndexOf(ChargeWeapons, weaponDefName) >= 0) return PenetrationGroup.Charge;
            return nativeBurst <= 1 ? PenetrationGroup.Single : PenetrationGroup.Basic;
        }

        public static int Credits(PenetrationGroup g) => g == PenetrationGroup.Charge ? 2 : g == PenetrationGroup.Single ? 1 : 0;

        // Every penetrating round gets through the first obstacle it out-penetrates whatever that obstacle costs;
        // later ones need the credits (so single-shot and charge weapons still pass several, others just the one).
        public static bool Affordable(int credits, int cost, int passed) => credits >= cost || passed == 0;

        // Shotgun pellets each stop after two pawns (user rule); every other penetrating round after three.
        public static int PawnCap(bool shotgun) => shotgun ? ShotgunMaxPawns : MaxPawns;

        public static bool IsShotgun(string defName, string where) =>
            (defName ?? "").IndexOf("shotgun", StringComparison.OrdinalIgnoreCase) >= 0 || (where ?? "").IndexOf("shotgun", StringComparison.OrdinalIgnoreCase) >= 0;

        public static bool Heavy(ObstacleClass c) => c == ObstacleClass.MetalWall || c == ObstacleClass.StoneWall || c == ObstacleClass.NaturalRock || c == ObstacleClass.Slab;

        public static int Cost(ObstacleClass c) => c == ObstacleClass.Barrier ? int.MaxValue : Heavy(c) ? 2 : 1;

        // Strictly greater: equal armour stops the round (after its normal hit).
        public static bool Passes(float penetration, float armor) => float.IsFinite(armor) && penetration > armor;

        public static float PartDamage(float d0, bool firstOnPawn) => firstOnPawn ? d0 : d0 * FollowUpPartFactor;

        // A body part's armour for the continue test: the largest of the natural armour and every covering layer
        // (each layer compared on its own, never summed).
        public static float EffectiveArmor(float natural, IEnumerable<float> layers)
        {
            float a = float.IsFinite(natural) ? Math.Max(0f, natural) : 0f;
            if (layers != null) foreach (float l in layers) if (float.IsFinite(l)) a = Math.Max(a, l);
            return a;
        }
    }

    // One projectile's penetration state: originals from launch, everything else accumulated along its flight.
    internal sealed class PenetrationState
    {
        public readonly float D0, P0;
        public readonly int PawnCap;
        public readonly ObstacleArmor Armor;
        public readonly PenetrationGroup Group;
        public float Retain = 1f;
        public int Credits;
        public int Passed;   // obstacles gone through
        public StopReason Stop;
        public readonly List<long> PawnOrder = new List<long>();
        public readonly HashSet<long> Pawns = new HashSet<long>(), ExitedPawns = new HashSet<long>();
        public readonly HashSet<long> Parts = new HashSet<long>();
        public readonly HashSet<long> Obstacles = new HashSet<long>(), InsideObstacles = new HashSet<long>();

        public PenetrationState(PenetrationGroup group, float d0, float p0, bool shotgun, ObstacleArmor armor)
        {
            Group = group; D0 = d0; P0 = p0; Credits = PenetrationRules.Credits(group); PawnCap = PenetrationRules.PawnCap(shotgun); Armor = armor.Normalized();
        }

        public float P => P0 * Retain;
        public bool Open(long pawn) => Pawns.Contains(pawn) && !ExitedPawns.Contains(pawn);
        public static long PartKey(long pawn, int part) => (pawn << 16) ^ (part & 0xFFFF);
    }

    internal enum ContactKind { PawnExit = 0, ObstacleExit = 1, PartEnter = 2, ObstacleEnter = 3, SlabCross = 4, Terminal = 5 }

    internal struct Contact
    {
        public float T;               // fraction along the segment
        public ContactKind Kind;
        public long Entity;           // pawn / thing / slab identity
        public int Part;              // body part index (PartEnter); stable order for equal T
        public ObstacleClass Obstacle;
        public StopReason Reason;     // Terminal
#pragma warning disable CS0649 // assigned by the game-side collector, not by the offline contracts
        public int Ref;               // caller's index for the engine data behind this contact
#pragma warning restore CS0649
    }

    internal interface IPenetrationSink
    {
        // Damage one body part with the given base damage and penetration. armor: the part's armour (max of natural and
        // every covering layer) read before the damage. applied false: the part or pawn is gone; nothing happened.
        bool HitPart(in Contact c, float damage, float penetration, bool firstOnPawn, out float armor, out bool absorbed);
        // The obstacle's own native damage, once.
        void HitObstacle(in Contact c, float penetration);
    }

    // 0.9.0 cover wears under fire: wood, sandbags and light cover take a round's full damage, stone and metal much less,
    // natural rock least (applied both when a round stops in the obstacle and when it goes through).
    internal static class CoverWear
    {
        public static float Factor(ObstacleClass c) => c == ObstacleClass.NaturalRock ? .15f
            : c == ObstacleClass.StoneWall || c == ObstacleClass.MetalWall || c == ObstacleClass.StoneChunk || c == ObstacleClass.Slag ? .35f : 1f;
    }

    internal static class PenetrationWalker
    {
        private static readonly Comparison<Contact> order = (x, y) =>
        {
            int c = x.T.CompareTo(y.T);
            if (c != 0) return c;
            c = ((int)x.Kind).CompareTo((int)y.Kind);   // exits first, then entries
            if (c != 0) return c;
            c = x.Entity.CompareTo(y.Entity);
            return c != 0 ? c : x.Part.CompareTo(y.Part);
        };

        public static void Sort(List<Contact> contacts) => contacts.Sort(order);

        // Processes one segment's contacts in order. True: the projectile stops at stopT (state.Stop says why).
        public static bool Process(List<Contact> contacts, PenetrationState s, IPenetrationSink sink, out float stopT)
        {
            stopT = 1f;
            Sort(contacts);
            int events = 0;
            for (int i = 0; i < contacts.Count; i++)
            {
                Contact c = contacts[i];
                if (++events > PenetrationRules.MaxEventsPerSegment) return End(s, StopReason.EventLimit, c.T, out stopT);
                switch (c.Kind)
                {
                    case ContactKind.PawnExit:
                        if (!s.Open(c.Entity)) break;
                        s.ExitedPawns.Add(c.Entity);
                        s.Retain *= PenetrationRules.ExitRetain;
                        if (s.Pawns.Count >= s.PawnCap) return End(s, StopReason.PawnCap, c.T, out stopT);
                        break;
                    case ContactKind.ObstacleExit:
                        if (s.InsideObstacles.Remove(c.Entity)) s.Retain *= PenetrationRules.ExitRetain;
                        break;
                    case ContactKind.PartEnter:
                    {
                        long key = PenetrationState.PartKey(c.Entity, c.Part);
                        if (s.Parts.Contains(key)) break;
                        bool first = !s.Pawns.Contains(c.Entity);
                        if (first && s.Pawns.Count >= s.PawnCap) break;   // beyond the cap: never damaged by this round
                        float pen = s.P;
                        if (!sink.HitPart(c, PenetrationRules.PartDamage(s.D0, first), pen, first, out float armor, out bool absorbed)) { s.Parts.Add(key); break; }
                        s.Parts.Add(key);
                        if (first) { s.Pawns.Add(c.Entity); s.PawnOrder.Add(c.Entity); }
                        if (absorbed) return End(s, StopReason.Shield, c.T, out stopT);
                        if (s.Group == PenetrationGroup.Basic) return End(s, StopReason.PawnCap, c.T, out stopT);   // one part, as before
                        if (!PenetrationRules.Passes(pen, armor)) return End(s, StopReason.Armor, c.T, out stopT);
                        break;
                    }
                    case ContactKind.ObstacleEnter:
                    case ContactKind.SlabCross:
                    {
                        if (!s.Obstacles.Add(c.Entity)) break;
                        float pen = s.P, armor = s.Armor.Get(c.Obstacle);
                        int cost = PenetrationRules.Cost(c.Obstacle);
                        // The decision uses the state before this hit; destroying the obstacle does not let the round through.
                        bool credits = PenetrationRules.Affordable(s.Credits, cost, s.Passed), through = credits && PenetrationRules.Passes(pen, armor);
                        sink.HitObstacle(c, pen);
                        if (!through)
                            return End(s, c.Obstacle == ObstacleClass.Barrier ? StopReason.Barrier : !credits ? StopReason.Credits : StopReason.Armor, c.T, out stopT);
                        s.Credits = Math.Max(0, s.Credits - cost);
                        s.Passed++;
                        if (c.Kind == ContactKind.SlabCross) s.Retain *= PenetrationRules.ExitRetain;
                        else s.InsideObstacles.Add(c.Entity);
                        break;
                    }
                    default:
                        return End(s, c.Reason == StopReason.None ? StopReason.Ground : c.Reason, c.T, out stopT);
                }
            }
            return false;
        }

        private static bool End(PenetrationState s, StopReason reason, float t, out float stopT)
        {
            s.Stop = reason;
            stopT = Math.Clamp(t, 0f, 1f);
            return true;
        }
    }
}
