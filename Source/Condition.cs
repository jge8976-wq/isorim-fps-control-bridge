using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace HSK.FpsBridge
{
    // 0.9.0 the possessed body in the view, from the game's own health numbers:
    //   - moving capacity down (leg wounds): a limp in the eye while walking (the game already slows the walk);
    //   - manipulation down (arm/hand wounds): a shakier aim;
    //   - bleeding: red at the edges of the view, pulsing with a heartbeat that quickens and grows louder;
    //   - consciousness down: the view greys and darkens at the edges.
    // Suppression: hostile rounds passing within 1.5 cells darken the edges and shake the aim (up to double), fading
    // two seconds after the last one.
    internal static class Condition
    {
        private const float NearMiss = 1.5f, PerMiss = .35f, HoldSeconds = 2f, Fade = .4f;
        private static Pawn pawnRead;
        private static int readFrame = -100;
        private static float moving = 1f, manipulation = 1f, consciousness = 1f, bleed;
        private static float limpPhase, limp, heartNext, pulseAt = -10f;
        private static float suppression, lastMiss = -10f, lastReal;
        private static readonly HashSet<int> seenRounds = new HashSet<int>();
        private static Texture2D vignette;
        private static SoundDef heartbeat;
        private static bool heartLooked;

        public static Vector3 EyeOffset { get; private set; }
        public static float Suppression => suppression;

        public static void Reset()
        {
            pawnRead = null; readFrame = -100; moving = manipulation = consciousness = 1f; bleed = 0f;
            limp = 0f; EyeOffset = Vector3.zero; suppression = 0f; lastMiss = -10f; seenRounds.Clear();
        }

        // Aim sway multiplier: hurt hands, then being shot at.
        public static float SwayFactor(Pawn pawn) => pawn == null || pawn != pawnRead ? 1f
            : (1f + 1.5f * Mathf.Clamp01(1f - manipulation)) * (1f + suppression);

        // Hurt hands change weapons slowly: game ticks a swap takes (0 with sound hands).
        public static int SwapDelayTicks(Pawn pawn) => pawn != pawnRead || manipulation >= .95f ? 0 : Mathf.RoundToInt(90f * Mathf.Clamp01(1f - manipulation));

        // Once per input frame while possessed. walking: the body is moving under the player's input.
        public static void Frame(Pawn pawn, bool walking, bool paused)
        {
            if (pawn?.health == null || pawn.Map == null) return;
            float now = Time.unscaledTime, real = Mathf.Clamp(now - lastReal, 0f, .1f);
            lastReal = now;
            float game = paused ? 0f : real * (Find.TickManager?.TickRateMultiplier ?? 1f);
            if (pawn != pawnRead || Time.frameCount - readFrame >= 30)
            {
                pawnRead = pawn; readFrame = Time.frameCount;
                PawnCapacitiesHandler caps = pawn.health.capacities;
                moving = caps.GetLevel(PawnCapacityDefOf.Moving);
                manipulation = caps.GetLevel(PawnCapacityDefOf.Manipulation);
                consciousness = caps.GetLevel(PawnCapacityDefOf.Consciousness);
                bleed = pawn.health.hediffSet.BleedRateTotal;
            }
            // A limp: the eye dips on every other step, deeper the worse the legs.
            float target = walking && moving < .9f ? Mathf.Clamp01((.9f - moving) / .6f) : 0f;
            limp = Mathf.MoveTowards(limp, target, real * 3f);
            limpPhase += game * 8f;
            EyeOffset = limp > .001f ? new Vector3(0f, -Mathf.Abs(Mathf.Sin(limpPhase)) * .16f * limp, 0f) : Vector3.zero;
            // The heartbeat while bleeding: faster and louder the worse the bleeding.
            if (bleed > .05f && !paused && now >= heartNext)
            {
                float b = Mathf.Clamp01(bleed / 1.5f);
                heartNext = now + Mathf.Lerp(1.1f, .42f, b);
                pulseAt = now;
                if (!heartLooked) { heartLooked = true; heartbeat = DefDatabase<SoundDef>.GetNamedSilentFail("FleshmassHeart_Throb"); }
                if (heartbeat != null)
                {
                    var info = SoundInfo.InMap(new TargetInfo(pawn.Position, pawn.Map));
                    info.volumeFactor = Mathf.Lerp(.7f, 1.8f, b);
                    heartbeat.PlayOneShot(info);
                }
            }
            Suppress(pawn, now, real);
        }

        // Hostile rounds in flight passing close by (each counted once).
        private static void Suppress(Pawn pawn, float now, float real)
        {
            List<Thing> rounds = pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile);
            Vector3 at = pawn.DrawPos;
            for (int i = 0; i < rounds.Count; i++)
            {
                if (!(rounds[i] is Projectile p) || seenRounds.Contains(p.thingIDNumber)) continue;
                Thing launcher = p.Launcher;
                if (launcher == null || launcher == pawn || !launcher.HostileTo(pawn)) continue;
                Vector3 d = p.ExactPosition - at; d.y = 0f;
                if (d.sqrMagnitude > NearMiss * NearMiss) continue;
                seenRounds.Add(p.thingIDNumber);
                suppression = Mathf.Min(1f, suppression + PerMiss);
                lastMiss = now;
            }
            if (seenRounds.Count > 600) seenRounds.Clear();
            if (now - lastMiss > HoldSeconds) suppression = Mathf.Max(0f, suppression - Fade * real);
        }

        // ---- the view's edges

        private static Texture2D Vignette()
        {
            if (vignette != null) return vignette;
            const int n = 64;
            vignette = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + .5f) / n * 2f - 1f, v = (y + .5f) / n * 2f - 1f;
                    float r = Mathf.Sqrt(u * u * .8f + v * v);
                    float a = Mathf.Clamp01((r - .45f) / .6f);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(255f * a * a * (3f - 2f * a)));
                }
            vignette.SetPixels32(px);
            vignette.Apply();
            return vignette;
        }

        public static void DrawOverlay(Pawn pawn)
        {
            if (pawn != pawnRead) return;
            Rect screen = new Rect(0f, 0f, UI.screenWidth, UI.screenHeight);
            Color c0 = GUI.color;
            try
            {
                if (consciousness < .85f)
                {
                    float k = Mathf.Clamp01((.85f - consciousness) / .5f);
                    Widgets.DrawBoxSolid(screen, new Color(.35f, .35f, .35f, .35f * k));
                    GUI.color = new Color(0f, 0f, 0f, .6f * k); GUI.DrawTexture(screen, Vignette());
                }
                if (suppression > .01f) { GUI.color = new Color(0f, 0f, 0f, .6f * suppression); GUI.DrawTexture(screen, Vignette()); }
                if (bleed > .05f)
                {
                    float b = Mathf.Clamp01(bleed / 1.2f), since = Time.unscaledTime - pulseAt;
                    float pulse = since >= 0f && since < .35f ? 1f - since / .35f : 0f;
                    GUI.color = new Color(.75f, 0f, 0f, (.3f + .3f * pulse) * b + .1f);
                    GUI.DrawTexture(screen, Vignette());
                }
            }
            finally { GUI.color = c0; }
        }
    }
}
