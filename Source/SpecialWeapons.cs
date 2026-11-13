using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Special weapons retain their actual Verb, WarmupComplete, TryCastShot and native resource checks.
    // Never manufacture generic bullets for a custom spell or truncate it to three rounds.
    internal static class SpecialWeapons
    {
        private sealed class Cast
        {
            internal readonly SpecialFireClock clock = new SpecialFireClock();
            internal Pawn pawn;
        }
        private static readonly Dictionary<Verb, Cast> casts = new Dictionary<Verb, Cast>();
        private static readonly Dictionary<Verb, bool> classified = new Dictionary<Verb, bool>();

        internal static bool Uses(Verb verb)
        {
            if (CeCompat.Owns(verb)) return false;
            if (verb?.verbProps == null || verb.verbProps.IsMeleeAttack || verb.EquipmentSource == null) return false;
            if (classified.TryGetValue(verb, out bool result)) return result;
            ThingDef def = verb.EquipmentSource.def;
            SpecialWeaponExtension custom = def.GetModExtension<SpecialWeaponExtension>();
            if (custom != null) result = custom.enabled;
            else
            {
                string model = WeaponKeywordRules.Pick(def.defName, def.label, def.graphicData?.texPath, false);
                // Native nonstandard casts and third-party Verb subclasses must keep their own attack implementation.
                Type type = verb.GetType();
                ThingDef projectile = (verb as Verb_LaunchProjectile)?.Projectile;
                bool round = projectile?.projectile != null && !projectile.projectile.flyOverhead
                    && typeof(Bullet).IsAssignableFrom(projectile.thingClass);
                bool grenade = type == typeof(Verb_LaunchProjectile) && projectile?.projectile != null;
                result = !WeaponCompatibility.Ordinary(verb) && (model == "MeleeWeapon_PsyfocusStaff"
                    || type != typeof(Verb_Shoot) && type != typeof(Verb_LaunchProjectile)
                    || !round && !grenade);
            }
            classified[verb] = result;
            return result;
        }

        private static Cast For(Pawn pawn, Verb verb)
        {
            if (!casts.TryGetValue(verb, out Cast c)) casts[verb] = c = new Cast();
            c.pawn = pawn;
            return c;
        }

        internal static void Reset() { casts.Clear(); classified.Clear(); }

        internal static void Frame(Pawn pawn, Verb verb, bool held, bool pressed)
        {
            Cast c = For(pawn, verb);
            int now = Find.TickManager.TicksGame;
            Observe(pawn, verb, c, now);
            if (!(held || pressed) || !c.clock.Ready(now, verb.Bursting) || pawn.stances.curStance is Stance_Busy
                || !Magazines.Ready(pawn, verb) || !verb.Available()) return;
            LocalTargetInfo target = Bridge.NativeAimTarget(pawn, verb);
            if (target.IsValid && verb.CanHitTarget(target)) StartCast(pawn, verb, target, now);
        }

        // Separate from input/raycasting so CPU integration checks execute the real cast and native burst scheduler.
        internal static bool StartCast(Pawn pawn, Verb verb, LocalTargetInfo target, int now)
        {
            Cast c = For(pawn, verb);
            if (!c.clock.Ready(now, verb.Bursting)) return false;
            c.clock.Begin(verb.verbProps.AdjustedCooldownTicks(verb, pawn));
            try
            {
                if (!verb.TryStartCastOn(target, false, true)) { c.clock.Failed(); return false; }
                CompleteWarmup(pawn, verb, c);
                Observe(pawn, verb, c, now);
                return true;
            }
            catch
            {
                // A partial custom cast must not grant an immediately reusable weapon after an exception.
                if (c.clock.Casting) c.clock.CompleteFallback(now);
                throw;
            }
        }

        private static void CompleteWarmup(Pawn pawn, Verb verb, Cast c)
        {
            // Some custom verbs add an aiming stance in WarmupComplete. Transfer each real stance once, with a
            // bounded loop so an incompatible verb cannot freeze the render thread.
            for (int i = 0; i < 4 && pawn.stances.curStance is Stance_Warmup warmup && warmup.verb == verb; i++)
            {
                c.clock.TransferWarmup(warmup.ticksLeft);
                warmup.Interrupt(); // native effect cleanup and mobile stance, without firing twice
                verb.WarmupComplete();
            }
        }

        private static void Observe(Pawn pawn, Verb verb, Cast c, int now)
        {
            if (!c.clock.Casting) return;
            CompleteWarmup(pawn, verb, c);
            if (!verb.Bursting && !(pawn.stances.curStance is Stance_Warmup w && w.verb == verb))
            {
                // Covers a native Reset, failed shot, or a custom cast which intentionally creates no cooldown stance.
                int native = pawn.stances.curStance is Stance_Cooldown s && s.verb == verb ? s.ticksLeft : -1;
                if (native >= 0) c.clock.Complete(now, native); else c.clock.CompleteFallback(now);
            }
        }

        // Called before the ordinary FPS cooldown multiplier. Burst spacing stays native; only the final recovery
        // receives the removed warmup. No Sandevistan, Death Eye or generic cooldown scaling is imposed here.
        internal static bool AdjustCooldown(Verb verb, ref int ticks)
        {
            if (verb == null || !casts.TryGetValue(verb, out Cast c) || c.pawn != verb.CasterPawn) return false;
            if (c.clock.Casting && !verb.Bursting) ticks = c.clock.Complete(Find.TickManager.TicksGame, ticks);
            return true;
        }

        internal static void Release(Pawn pawn)
        {
            int now = Find.TickManager.TicksGame;
            foreach (var pair in casts)
            {
                Cast c = pair.Value;
                if (c.pawn != pawn || pair.Key.CasterPawn != pawn || !c.clock.Casting) continue;
                if (pawn.stances.curStance is Stance_Warmup w && w.verb == pair.Key) w.Interrupt();
                pair.Key.Reset();
                int ticks = c.clock.CompleteFallback(now);
                pawn.stances.SetStance(new Stance_Cooldown(ticks, LocalTargetInfo.Invalid, pair.Key));
            }
        }

        internal static void ModeMessage() => Messages.Message(L.T("特殊武器模式 · 按武器原设定施放", "Special weapon · native attack sequence"), MessageTypeDefOf.SilentInput, false);

        internal static void DrawHud(Pawn pawn, Verb verb)
        {
            Cast c = For(pawn, verb);
            int now = Find.TickManager.TicksGame;
            float x = UI.screenWidth * .5f, y = UI.screenHeight * .5f + 46f;
            if (WeaponView.ScopedAiming) y = UI.screenHeight * .5f + ScopeRules.Viewport(UI.screenWidth, UI.screenHeight).size * .35f;
            GameFont font = Text.Font; TextAnchor anchor = Text.Anchor; Color color = GUI.color;
            try
            {
                Text.Font = GameFont.Tiny; Text.Anchor = TextAnchor.UpperCenter;
                GUI.color = new Color(.75f, .9f, 1f, .95f);
                string status = WeaponCompatibility.Native3D(verb) ? L.T("3D弹道 · 原生射击序列", "3D ballistics · native fire sequence") : L.T("特殊武器", "Special weapon");
                if (verb.Bursting || c.clock.Casting) status += L.T(" · 施放中", " · casting");
                else if (c.clock.Remaining(now) > 0) status += L.T(" · 恢复 ", " · recovery ") + (c.clock.Remaining(now) / 60f).ToString("F1") + "s";
                else status += L.T(" · 就绪", " · ready");
                Widgets.Label(new Rect(x - 180f, y, 360f, 20f), status);
                if (c.clock.Casting || c.clock.Remaining(now) > 0)
                {
                    Vector2 center = new Vector2(x, y + 37f);
                    float fill = c.clock.Casting ? 0f : c.clock.Progress(now);
                    for (int i = 0; i < 40; i++)
                    {
                        float a = i * Mathf.PI * 2f / 40f - Mathf.PI / 2f, b = (i + 1) * Mathf.PI * 2f / 40f - Mathf.PI / 2f;
                        Widgets.DrawLine(center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 11f,
                            center + new Vector2(Mathf.Cos(b), Mathf.Sin(b)) * 11f,
                            i / 40f < fill ? new Color(.45f, .85f, 1f) : new Color(.25f, .3f, .35f), 2f);
                    }
                }
            }
            finally { Text.Font = font; Text.Anchor = anchor; GUI.color = color; }
        }
    }
}
