using System;

namespace HSK.FpsBridge
{
    internal readonly struct MagazineSpec
    {
        public readonly int Capacity;
        public readonly float Seconds;
        public readonly bool PerRound;
        public MagazineSpec(int capacity, float seconds, bool perRound = false)
        { Capacity = Math.Clamp(capacity, 1, 10000); Seconds = Math.Clamp(seconds, .15f, 60f); PerRound = perRound; }
    }
    internal static class MagazineRules
    {
        private static bool Has(string text, params string[] keys)
        { foreach (string key in keys) if (text.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) return true; return false; }
        // Practical game defaults inspired by magazine/feed type. They are not manufacturer-certified reload times.
        public static MagazineSpec Classify(string identity, int burst, float range, bool grenade)
        {
            string s = identity ?? "";
            if (Has(s, "TripleLauncher", "TripleRocket")) return new MagazineSpec(3, 6f);
            if (Has(s, "GrenadeLauncher", "Rocket", "Doomsday", "TripleLauncher", "Mortar", "RPG")) return new MagazineSpec(1, 4.5f);
            if (grenade) return new MagazineSpec(1, 1.5f);
            if (Has(s, "Minigun", "Gatling", "Vulcan")) return new MagazineSpec(200, 8f);
            if (Has(s, "SMG", "Submachine", "MP5", "Uzi")) return new MagazineSpec(30, 2.2f);
            if (Has(s, "MachinePistol")) return new MagazineSpec(20, 2f);
            if (Has(s, "MachineGun", "LMG", "Lmg", "M240", "M249")) return new MagazineSpec(100, 6.5f);
            if (Has(s, "Revolver")) return new MagazineSpec(6, 3.2f);
            if (Has(s, "Autopistol", "Pistol", "Handgun", "Sidearm")) return new MagazineSpec(15, 1.8f);
            if (Has(s, "ChainShotgun", "AutoShotgun", "Saiga")) return new MagazineSpec(8, 2.8f);
            if (Has(s, "Shotgun", "Pump")) return new MagazineSpec(6, .65f, true);
            if (Has(s, "Crossbow", "Cross_Bow", "连弩", "连发弩")
                && (burst > 1 || Has(s, "AutoCross", "Repeating", "RepeatCross", "连弩", "连发弩"))) return new MagazineSpec(10, 3.2f);
            if (Has(s, "Bow", "Crossbow")) return new MagazineSpec(1, Has(s, "Crossbow") ? 2.8f : 1.2f);
            if (Has(s, "Sniper", "BoltAction", "HuntingRifle", "AntiMateriel")) return new MagazineSpec(5, 3.2f);
            if (Has(s, "Charge", "Laser", "Beam", "Energy", "Plasma")) return new MagazineSpec(30, 2.8f);
            if (Has(s, "Rifle", "Carbine", "AK47", "Assault", "AUG")) return new MagazineSpec(30, 2.5f);
            if (burst > 10) return new MagazineSpec(200, 8f);
            if (burst >= 6) return new MagazineSpec(100, 6.5f);
            if (burst > 1) return new MagazineSpec(30, 2.5f);
            return range >= 35f ? new MagazineSpec(5, 3.2f) : new MagazineSpec(12, 2.2f);
        }
    }

    // A single physical weapon's state. The caller supplies elapsed game-time seconds, so pause/save/input loss
    // cannot silently complete a reload. Partial shell loading keeps only the shells already inserted.
    internal sealed class MagazineState
    {
        public int Rounds;
        public bool Reloading;
        public float Elapsed;
        private int startRounds;
        public MagazineState(int rounds) { Rounds = Math.Max(0, rounds); }
        public bool Start(MagazineSpec spec)
        {
            if (Reloading || Rounds >= spec.Capacity) return false;
            Reloading = true; Elapsed = 0f; startRounds = Rounds; return true;
        }
        public bool Consume() { if (Reloading || Rounds <= 0) return false; Rounds--; return true; }
        public void Cancel() { Reloading = false; Elapsed = 0f; }
        public void Advance(float seconds, MagazineSpec spec)
        {
            if (!Reloading || !(seconds > 0f) || !float.IsFinite(seconds)) return;
            Elapsed += seconds;
            if (spec.PerRound)
            {
                while (Elapsed >= spec.Seconds && Rounds < spec.Capacity) { Rounds++; Elapsed -= spec.Seconds; }
                if (Rounds >= spec.Capacity) Cancel();
            }
            else if (Elapsed >= spec.Seconds) { Rounds = spec.Capacity; Cancel(); }
        }
        public float Progress(MagazineSpec spec) => Reloading ? Math.Clamp(spec.PerRound
            ? (Rounds - startRounds + Elapsed / spec.Seconds) / Math.Max(1, spec.Capacity - startRounds)
            : Elapsed / spec.Seconds, 0f, 1f) : Rounds == spec.Capacity ? 1f : 0f;
        public float Remaining(MagazineSpec spec) => Reloading ? Math.Max(0f, spec.Seconds - Elapsed)
            + (spec.PerRound ? Math.Max(0, spec.Capacity - Rounds - 1) * spec.Seconds : 0f) : 0f;
    }
}
