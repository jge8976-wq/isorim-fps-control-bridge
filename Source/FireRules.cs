using System;
using System.Collections.Generic;

namespace HSK.FpsBridge
{
    // Engine-independent combat rules (R4); exercised by the offline contracts.
    internal enum FireMode { Single, Burst3, Automatic }
    internal enum HeatClass { None, Smg, Rifle, MachineGun, Gatling }

    internal static class HeatRules
    {
        public static int Capacity(HeatClass c)
        {
            switch (c)
            {
                case HeatClass.Smg: return 50;
                case HeatClass.Rifle: return 75;
                case HeatClass.MachineGun: return 100;
                case HeatClass.Gatling: return 200;
                default: return 0;
            }
        }

        private static bool Has(string text, params string[] terms)
        {
            foreach (string t in terms) if (text.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // Only burst weapons have fire modes and a heat bar. The class comes from where the weapon belongs (its defName,
        // graphic folder, weapon tags and weapon classes); unknown burst weapons fall back on their burst length.
        public static HeatClass Classify(string defName, string texPath, IEnumerable<string> tags, int burst)
        {
            if (burst <= 1) return HeatClass.None;
            string id = (defName ?? "") + "|" + (texPath ?? "");
            string traits = tags == null ? "" : string.Join("|", tags);
            // A submachine gun name also contains "machinegun": decide the specific classes first.
            if (Has(id, "minigun", "gatling", "rotary", "chaingun")) return HeatClass.Gatling;
            if (Has(id, "smg", "submachine", "machinepistol")) return HeatClass.Smg;
            if (Has(id, "lmg", "hmg", "machinegun", "machine_gun")) return HeatClass.MachineGun;
            if (Has(id, "assaultrifle", "chargerifle", "rifle", "carbine", "battlerifle")) return HeatClass.Rifle;
            if (Has(traits, "Minigun", "Gatling")) return HeatClass.Gatling;
            if (Has(traits, "MachineGun", "LMG")) return HeatClass.MachineGun;
            if (Has(traits, "SMG", "ShortShots")) return HeatClass.Smg;
            if (burst > 10) return HeatClass.Gatling;
            if (burst >= 6) return HeatClass.MachineGun;
            if (burst >= 4) return HeatClass.Rifle;
            return HeatClass.Smg;
        }
    }

    // One physical weapon's trigger state. Only game ticks advance it (paused = frozen).
    internal sealed class HeatState
    {
        public FireMode Mode = FireMode.Burst3;
        public double Heat { get; private set; }
        public bool Locked { get; private set; }
        public int Queued { get; private set; }      // shots still owed to a three-round burst
        private int lastTick, lastShotTick = int.MinValue / 2;

        public HeatState(int tick) { lastTick = tick; }

        // Not firing (no shot within the shot interval + 2 ticks), the bar falls; a full bar drains in coolTicks.
        public void Advance(int tick, int capacity, int coolTicks, int interval)
        {
            if (tick <= lastTick) return;
            if (capacity > 0)
            {
                int from = Math.Max(lastTick, lastShotTick + interval + 2);
                if (tick > from)
                {
                    Heat = Math.Max(0, Heat - (tick - from) * capacity / (double)Math.Max(1, coolTicks));
                    if (Heat <= 1e-9) { Heat = 0; Locked = false; }
                }
            }
            lastTick = tick;
        }

        public bool CanFire(int capacity) => capacity <= 0 || !Locked;

        public void Shot(int tick, int capacity)
        {
            lastShotTick = tick;
            if (capacity <= 0) return;
            Heat = Math.Min(capacity, Heat + 1);
            if (Heat >= capacity - 1e-9) { Heat = capacity; Locked = true; Queued = 0; }
        }

        public void QueueBurst() { Queued = 3; }
        public void Consume() { if (Queued > 0) Queued--; }
        public void ClearQueue() { Queued = 0; }
        public void Cycle() { Mode = (FireMode)(((int)Mode + 1) % 3); Queued = 0; }

        public static string Label(FireMode m) => m == FireMode.Single ? L.T("单发", "Single") : m == FireMode.Burst3 ? L.T("三连发", "Burst x3") : L.T("全自动", "Full auto");
    }

    // Trigger logic per frame: which shots a held/pressed trigger asks for (engine-free for the tests).
    internal static class Trigger
    {
        // Never schedule an overdue shot for the same tick as the shot just fired.
        public static int NextShotTick(int now, int interval) => now + Math.Max(1, interval);
        // pressed = went down this frame; held = is down.
        public static bool Wants(HeatState s, bool burstWeapon, bool pressed, bool held)
        {
            if (!burstWeapon) return pressed;
            switch (s.Mode)
            {
                case FireMode.Single: return pressed;
                case FireMode.Burst3:
                    if (pressed && s.Queued == 0) s.QueueBurst();
                    return s.Queued > 0;
                default: return held;
            }
        }
    }

    // Thrown grenades: the view's elevation picks the throw angle; 45 degrees reaches farthest (the weapon's range).
    internal static class ThrowMath
    {
        public const int FlightTicksAtMax = 60;

        public static void Launch(float range, float elevationDeg, out float speed, out float gravity)
        {
            range = Math.Max(2f, range);
            gravity = 2f * range / (FlightTicksAtMax * FlightTicksAtMax);
            speed = (float)Math.Sqrt(range * gravity);
        }

        // Ideal flat-ground distance for an elevation (degrees above the horizon), for previews and tests.
        public static float FlatDistance(float range, float elevationDeg)
        {
            Launch(range, elevationDeg, out float v, out float g);
            double a = elevationDeg * Math.PI / 180.0;
            return (float)(v * v * Math.Sin(2 * a) / g);
        }
    }

    // Heavy slash: double damage, shared equally by every body part the blade passed through.
    internal static class SlashMath
    {
        public static float PerPart(float baseDamage, int parts) => parts <= 0 ? 0f : baseDamage * 2f / parts;
    }

    // Shared constants of the R4 flight model.
    internal static class FlightRules
    {
        // Game gravity for rounds, cells/tick^2: about a third of a cell of drop at a rifle's listed range.
        public const float BulletGravity = 0.00033f;
    }
}
