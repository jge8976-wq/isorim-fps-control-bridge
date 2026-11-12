using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal enum BlastStyle
    {
        Impact, HighExplosive, Heavy, Fragmentation, Airburst, Incendiary, Fuel,
        Smoke, Toxic, Emp, Flash, Dust, Water, Cryogenic
    }

    public sealed class ExplosionEffects : GameComponent
    {
        private readonly record struct Profile(Color Hot, Color Cloud, Color Ring, int Puffs, int Sparks,
            float Duration, float Lift, float Core, float Spread);
        private readonly record struct Burst(Map Map, Vector3 Origin, float Radius, int Tick, int Seed, BlastStyle Style);
        private static Color Tint(uint rgb, float alpha = 1f) =>
            new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, alpha);

        private static readonly Profile[] Profiles =
        {
            new(Tint(0xFFD788), Tint(0x77706A,.66f), Tint(0xFAC48D,.45f), 2, 5, .65f, .36f, .42f, .55f),
            new(Tint(0xFFAB38), Tint(0x534A43,.76f), Tint(0xFFD59D,.57f), 7, 11, 1.8f, .75f, .65f, .85f),
            new(Tint(0xFF9838), Tint(0x3D3939,.85f), Tint(0xFFE1B0,.65f), 12, 16, 2.8f, 1.3f, .92f, 1.05f),
            new(Tint(0xFFCA64), Tint(0x706256,.64f), Tint(0xF0BB76,.55f), 4, 18, 1.2f, .55f, .48f, 1.3f),
            new(Tint(0xFFC668), Tint(0x6D6261,.7f), Tint(0xE8DAD4,.54f), 7, 12, 1.75f, .32f, .66f, 1.2f),
            new(Tint(0xFF6820), Tint(0x50332E,.72f), Tint(0xFF8444,.3f), 8, 9, 2.45f, .95f, .85f, .8f),
            new(Tint(0xFF8027), Tint(0x302C31,.86f), Tint(0xFFA95E,.5f), 10, 10, 2.85f, 1.65f, 1.02f, .85f),
            new(Color.clear, Tint(0xB5B8B7,.77f), Color.clear, 11, 0, 3.3f, .52f, 0f, .95f),
            new(Color.clear, Tint(0x789B3A,.75f), Tint(0xA5C749,.29f), 10, 0, 3.35f, .22f, 0f, .95f),
            new(Tint(0x7FEAFF), Tint(0x6C8FC8,.40f), Tint(0x71DDFF,.82f), 3, 13, .95f, .35f, .55f, 1.23f),
            new(Tint(0xFFF7E1), Tint(0xBBB7A7,.38f), Tint(0xFFFAEE,.69f), 2, 6, .66f, .25f, .83f, .75f),
            new(Tint(0xDDA578,.42f), Tint(0xAA8868,.76f), Tint(0xCC9C73,.45f), 10, 8, 1.9f, .36f, .35f, 1.1f),
            new(Tint(0xD5F2F5,.55f), Tint(0xB5D5D8,.62f), Tint(0x8DCBDE,.66f), 7, 12, 1.45f, 1.1f, .44f, .73f),
            new(Tint(0xB9EBFF), Tint(0x9AC9E1,.58f), Tint(0xA6E5FF,.68f), 8, 11, 1.95f, .38f, .52f, .91f),
        };

        private const int ActiveLimit = 28;
        private readonly List<Burst> active = new List<Burst>(ActiveLimit);
        private readonly ConditionalWeakTable<Explosion, object> seen = new();
        private static readonly object Recorded = new object();
        private readonly BlastMesh clouds = new BlastMesh("IsoRim explosion clouds");
        private readonly BlastMesh bright = new BlastMesh("IsoRim explosion lights", luminous: true);
        internal int Submitted, Visible, DrawCalls, Vertices;
        internal readonly int[] Counts = new int[Profiles.Length];

        public ExplosionEffects(Game game) { }

        internal static void NativeStart(Explosion explosion)
        {
            if (FpsMod.Settings.explosionQuality == 0 || !View3D.Active || !explosion.Spawned
                || !explosion.doVisualEffects || explosion.damType == null || !float.IsFinite(explosion.radius)
                || explosion.radius <= 0 || explosion.Position.Fogged(explosion.Map)) return;
            var effects = Current.Game?.GetComponent<ExplosionEffects>();
            if (effects == null || effects.seen.TryGetValue(explosion, out _)) return;
            V3 at = AerialBlast.Origin(explosion);
            if (!at.Finite) return;
            float ground = FloorSpace.Band(explosion.Map, explosion.Position).Height;
            bool elevated = at.Y - ground > 1.4f;
            var style = Classify(explosion.damType.defName, explosion.projectile?.defName, explosion.weapon?.defName,
                explosion.instigator?.def?.defName, explosion.postExplosionGasType?.ToString(),
                explosion.Position.GetTerrain(explosion.Map)?.IsWater == true, elevated, explosion.radius);
            effects.seen.Add(explosion, Recorded);
            effects.Emit(explosion.Map, new Vector3(at.X, at.Y + .14f, at.Z), explosion.radius, explosion.thingIDNumber, style);
        }

        private static bool Contains(string source, string part) => source?.Contains(part, StringComparison.OrdinalIgnoreCase) == true;

        internal static BlastStyle Classify(string damage, string projectile, string weapon, string source,
            string gas, bool water, bool elevated, float radius)
        {
            if (Contains(damage, "EMP") || Contains(projectile, "_EMP")) return BlastStyle.Emp;
            if (Contains(damage, "Stun") || Contains(projectile, "Flash") || Contains(weapon, "Flashbang")) return BlastStyle.Flash;
            if (Contains(damage, "Tox") || Contains(gas, "Tox") || Contains(projectile, "Toxic")) return BlastStyle.Toxic;
            if (Contains(damage, "Smoke") || Contains(gas, "Smoke") || Contains(projectile, "Smoke")) return BlastStyle.Smoke;
            if (Contains(damage, "Frost") || Contains(damage, "Cryo") || Contains(projectile, "Cryo")) return BlastStyle.Cryogenic;
            if (!elevated && water) return BlastStyle.Water;
            if (Contains(source, "Chemfuel") || Contains(source, "FuelTank") || Contains(projectile, "Thermobaric")) return BlastStyle.Fuel;
            if (Contains(damage, "Flame") || Contains(projectile, "Incendiary") || Contains(weapon, "Molotov")) return BlastStyle.Incendiary;
            if (Contains(damage, "Mining") || Contains(source, "RockChunk")) return BlastStyle.Dust;
            if (Contains(damage, "BombSuper") || radius >= 8f) return BlastStyle.Heavy;
            if (elevated) return BlastStyle.Airburst;
            if (Contains(damage, "Frag") || Contains(projectile, "Frag")) return BlastStyle.Fragmentation;
            return radius < 1.1f ? BlastStyle.Impact : BlastStyle.HighExplosive;
        }

        internal void Emit(Map map, Vector3 origin, float radius, int seed, BlastStyle style)
        {
            var burst = new Burst(map, origin, Mathf.Clamp(radius, .35f, 16f), Find.TickManager.TicksGame, seed, style);
            if (active.Count == ActiveLimit)
            {
                int least = 0;
                for (int i = 1; i < active.Count; i++)
                    if (Priority(active[i], burst.Tick) < Priority(active[least], burst.Tick)) least = i;
                if (Priority(burst, burst.Tick) < Priority(active[least], burst.Tick)) return;
                active.RemoveAt(least);
            }
            active.Add(burst);
            Counts[(int)style]++;
            Submitted++;
        }

        private static float Priority(Burst burst, int now) => burst.Radius - (now - burst.Tick) * .05f;

        public override void GameComponentUpdate()
        {
            int quality = FpsMod.Settings.explosionQuality;
            Visible = DrawCalls = Vertices = 0;
            if (active.Count == 0) return;
            if (quality == 0 || !View3D.Active) { active.Clear(); return; }
            var map = Find.CurrentMap;
            Vector3 eye = View3D.EyePosition;
            Vector3 forward = View3D.Forward;
            int now = Find.TickManager.TicksGame;
            clouds.Clear();
            bright.Clear();
            int budget = quality == 1 ? 48 : quality == 2 ? 108 : 168;
            int maxVisible = quality == 1 ? 10 : quality == 2 ? 18 : ActiveLimit;
            int margin = Screen.width / 6;
            for (int i = active.Count - 1; i >= 0; i--)
            {
                var burst = active[i];
                var preset = Profiles[(int)burst.Style];
                float age = (now - burst.Tick) / 60f;
                if (age < 0 || age >= preset.Duration) { active.RemoveAt(i); continue; }
                if (burst.Map != map || Visible >= maxVisible) continue;
                Vector3 delta = burst.Origin - eye;
                if (Vector3.Dot(delta, forward) < -burst.Radius) continue;
                float distance = delta.magnitude / Mathf.Sqrt(Math.Max(1f, burst.Radius));
                var pixel = Api.WorldToScreen(burst.Origin);
                if (pixel.x < -margin || pixel.y < -margin || pixel.x > Screen.width + margin || pixel.y > Screen.height + margin) continue;
                int detail = quality == 1 || distance > 78f ? 0 : quality == 3 && distance < 50f ? 2 : 1;
                int quota = budget / Math.Max(1, Math.Min(i + 1, maxVisible - Visible));
                int available = quota;
                Visible++;
                DrawBurst(burst, preset, age, detail, ref quota);
                budget -= available - quota;
            }
            Vertices = clouds.VertexCount + bright.VertexCount;
            if (clouds.Draw()) DrawCalls++;
            if (bright.Draw()) DrawCalls++;
        }

        private void DrawBurst(Burst b, Profile p, float time, int detail, ref int budget)
        {
            float age = time / p.Duration;
            float expansion = 1f - (1f - age) * (1f - age);
            float radius = b.Radius;
            float hotTime = b.Style == BlastStyle.Incendiary || b.Style == BlastStyle.Fuel ? .72f
                : b.Style == BlastStyle.Flash || b.Style == BlastStyle.Emp ? .31f : .47f;
            if (p.Core > 0 && time < hotTime)
            {
                float t = time / hotTime;
                float size = radius * p.Core * Mathf.Lerp(.25f, 1.15f, Mathf.Sqrt(t));
                Color hot = Color.Lerp(Tint(0xFFFAE7), p.Hot, Mathf.Sqrt(t));
                hot.a = (1f - t * t) * p.Hot.a;
                Vector3 center = b.Origin + Vector3.up * (size * (.31f + p.Lift * t * .26f));
                bright.Ball(center, new Vector3(size, size * (.75f + p.Lift * .28f), size), Quaternion.identity, hot);
                if (detail > 0 && budget > 1)
                {
                    var lobe = BlastMesh.Direction(b.Seed);
                    bright.Ball(center + lobe * size * .55f, Vector3.one * size * .59f,
                        Quaternion.identity, Color.Lerp(hot, p.Hot, .25f));
                    budget--;
                }
            }
            if (p.Ring.a > 0 && time < .48f)
            {
                float t = time / .48f;
                Color ring = p.Ring;
                ring.a *= (1f - t) * (1f - t);
                float size = radius * Mathf.Lerp(.32f, 1.55f, t);
                bright.Ring(b.Origin, size, Quaternion.identity, ring, detail == 0 ? 12 : 24);
                if (detail > 0 && (b.Style == BlastStyle.Airburst || b.Style == BlastStyle.Emp))
                    bright.Ring(b.Origin, size * .86f, Quaternion.Euler(73, b.Seed % 180, 0), ring, 18);
            }

            int wantSparks = time >= .62f || detail == 0 ? 0 : detail == 1 ? p.Sparks / 2 : p.Sparks;
            int smokeBudget = budget - Math.Min(wantSparks, budget / 3);
            int puffs = Math.Min(smokeBudget, detail == 0 ? Math.Min(2, p.Puffs) : detail == 1 ? (p.Puffs + 1) / 2 : p.Puffs);
            budget -= puffs;
            for (int i = 0; i < puffs; i++)
            {
                Vector3 dir = BlastMesh.Direction(b.Seed + i * 5);
                float size = radius * (.19f + .33f * expansion) * (.75f + .5f * Math.Abs(dir.y));
                Vector3 drift = new Vector3(dir.x, 0, dir.z) * (radius * p.Spread * (.16f + .73f * expansion));
                drift.y = radius * (.15f + Math.Abs(dir.y) * .25f + p.Lift * expansion);
                Color cloud = p.Cloud;
                float smokeTime = p.Core == 0 ? time : Mathf.Max(0f, time - .13f);
                cloud.a *= (1f - age) * Mathf.Min(1f, .12f + smokeTime * 3.1f);
                clouds.Ball(b.Origin + drift, new Vector3(size, size * (.68f + .31f * p.Lift), size),
                    Quaternion.identity, cloud);
            }

            int sparks = Math.Min(budget, wantSparks);
            budget -= sparks;
            for (int i = 0; i < sparks; i++)
            {
                Vector3 dir = BlastMesh.Direction(b.Seed + 7 + i * 7);
                if (b.Style != BlastStyle.Airburst && b.Style != BlastStyle.Emp) dir.y = Mathf.Abs(dir.y) * .9f + .1f;
                float t = time / .62f;
                float size = radius * Mathf.Lerp(.058f, .021f, t);
                Vector3 travel = dir * (radius * p.Spread * (.32f + 1.4f * t));
                travel.y -= radius * t * t * .29f;
                Color spark = p.Hot;
                spark.a *= 1f - t;
                bright.Ball(b.Origin + travel, new Vector3(size, size * (3.6f - 1.8f * t), size),
                    Quaternion.FromToRotation(Vector3.up, dir), spark);
            }
        }
    }
}
