using System;
using System.Collections.Generic;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // R4 trigger for ordinary rounds. The game's warm-up, burst and cool-down are not used for the first-person pawn:
    //  - burst weapons (burst > 1) have three modes (single, three-round burst, full auto; B cycles) and a heat bar
    //    (SMG 50, rifle 75, machine gun 100, gatling 200 rounds). They fire at their own burst rhythm while the bar is not
    //    full; a full bar locks the trigger until it has drained. Not firing, a full bar drains in the weapon's native
    //    cool-down time;
    //  - single-shot weapons fire once per press, at most once per native cycle (warm-up + cool-down), no heat.
    // Heat follows the weapon (by thing id), so switching with Tab does not cool a gun.
    internal static class FireControl
    {
        private sealed class Gun
        {
            public HeatState heat;
            public HeatClass cls;
            public int capacity, interval, coolTicks, nextTick;
            public bool burst;
            public ThingDef def;
            public readonly SemiAutoBuffer trigger = new SemiAutoBuffer();
        }

        private static readonly Dictionary<int, Gun> guns = new Dictionary<int, Gun>();
        private static readonly List<string> traits = new List<string>();

        public static void Reset() => guns.Clear();
        public static void CancelInput() { foreach (Gun gun in guns.Values) { gun.trigger.Clear(); gun.heat.ClearQueue(); } }

        private static Gun For(Pawn pawn, Verb_LaunchProjectile verb)
        {
            Thing weapon = verb.EquipmentSource;
            int id = weapon?.thingIDNumber ?? -pawn.thingIDNumber;
            ThingDef def = weapon?.def ?? pawn.def;
            if (guns.TryGetValue(id, out Gun g) && g.def == def) return g;
            int now = Find.TickManager.TicksGame;
            VerbProperties props = verb.verbProps;
            int burst = Math.Max(1, props.burstShotCount);
            traits.Clear();
            if (def.weaponTags != null) traits.AddRange(def.weaponTags);
            if (def.weaponClasses != null) foreach (WeaponClassDef c in def.weaponClasses) traits.Add(c.defName);
            // Where the weapon belongs: its graphic folder and its thing categories (the game's own sorting of weapons).
            string where = def.graphicData?.texPath ?? "";
            if (def.thingCategories != null) foreach (ThingCategoryDef c in def.thingCategories) where += "|" + c.defName;
            HeatClass cls = HeatRules.Classify(def.defName, where, traits, burst);
            g = new Gun { heat = new HeatState(now), cls = cls, capacity = HeatRules.Capacity(cls), burst = cls != HeatClass.None, def = def };
            int cool = Math.Max(1, props.AdjustedCooldownTicks(verb, pawn));
            g.coolTicks = cool;
            g.interval = g.burst ? Math.Max(1, props.ticksBetweenBurstShots) : Math.Max(4, cool + Mathf.RoundToInt(props.warmupTime * 60f));
            guns[id] = g;
            return g;
        }

        // B: next fire mode for the gun in hand (burst weapons only).
        public static void CycleMode(Pawn pawn)
        {
            if (CeCompat.Owns(pawn.equipment?.PrimaryEq?.PrimaryVerb)) { CeCompat.CycleMode(pawn); return; }
            if (SpecialWeapons.Uses(pawn.equipment?.PrimaryEq?.PrimaryVerb)) { SpecialWeapons.ModeMessage(); return; }
            if (!(pawn.equipment?.PrimaryEq?.PrimaryVerb is Verb_LaunchProjectile verb) || !Ballistics.IsRound(verb)) return;
            Gun g = For(pawn, verb);
            if (!g.burst) { Messages.Message(L.T("这把武器只能单发。", "This weapon fires single shots only."), MessageTypeDefOf.RejectInput, false); return; }
            g.heat.Cycle();
            g.trigger.Clear();
            Messages.Message(L.T("射击模式：", "Fire mode: ") + HeatState.Label(g.heat.Mode), MessageTypeDefOf.SilentInput, false);
        }

        // Once per frame while the pawn can shoot (unpaused and captured; drafting is optional).
        public static void Frame(Pawn pawn, Verb_LaunchProjectile verb, bool held, bool pressed)
        {
            Gun g = For(pawn, verb);
            int now = Find.TickManager.TicksGame;
            // Sandevistan (the slowed world) and Death Eye (ten times the rate) shorten the interval in game ticks.
            int interval = Math.Max(1, Mathf.RoundToInt(CooldownRules.ShotInterval(g.interval, g.burst, g.heat.Mode, FpsMod.Settings.cooldownMultiplier) * CyberSkills.FireFactor(pawn)));
            g.heat.Advance(now, g.capacity, CooldownRules.Scale(g.coolTicks, FpsMod.Settings.cooldownMultiplier), interval);
            if (!g.heat.CanFire(g.capacity)) { g.heat.ClearQueue(); g.trigger.Clear(); return; }
            bool single = !g.burst || g.heat.Mode == FireMode.Single;
            // Death Eye: a single-shot gun keeps firing while the button is held.
            bool wants = single ? (CyberSkills.Eye(pawn) ? held || pressed : g.trigger.Wants(now, g.nextTick, pressed)) : Trigger.Wants(g.heat, g.burst, pressed, held);
            int fired = 0;
            while (wants && now >= g.nextTick && g.heat.CanFire(g.capacity) && fired < 4)
            {
                if (!Magazines.Ready(pawn, verb) || !verb.Available() || !Ballistics.FireRound(pawn, verb, AimPicker.Target(pawn, verb))) { g.heat.ClearQueue(); g.trigger.Clear(); return; }
                Magazines.Shot(pawn, verb);
                g.heat.Shot(now, g.capacity);
                g.heat.Consume();
                g.trigger.Clear();
                float damage = verb.Projectile?.projectile != null ? verb.Projectile.projectile.GetDamageAmount(verb.EquipmentSource, null) : 10f;
                AimMotion.Kick(pawn, damage);
                WeaponView.Kick(damage);
                // A frame stall or idle trigger must not compress missed shots into an instant double shot.
                g.nextTick = Trigger.NextShotTick(now, interval);
                fired++;
                wants = g.burst && (g.heat.Mode == FireMode.Automatic ? held : g.heat.Mode == FireMode.Burst3 && g.heat.Queued > 0);
            }
        }

        // ---- HUD: mode and heat bar under the crosshair

        public static void DrawHud(Pawn pawn, bool aiming)
        {
            Verb current = pawn.equipment?.PrimaryEq?.PrimaryVerb;
            if (CeCompat.Owns(current)) { CeCompat.DrawHud(pawn); return; }
            if (SpecialWeapons.Uses(current)) { SpecialWeapons.DrawHud(pawn, current); return; }
            if (!(pawn.equipment?.PrimaryEq?.PrimaryVerb is Verb_LaunchProjectile verb) || !Ballistics.IsRound(verb)) return;
            Gun g = For(pawn, verb);
            g.heat.Advance(Find.TickManager.TicksGame, g.capacity, CooldownRules.Scale(g.coolTicks, FpsMod.Settings.cooldownMultiplier),
                CooldownRules.ShotInterval(g.interval, g.burst, g.heat.Mode, FpsMod.Settings.cooldownMultiplier));
            float cx = UI.screenWidth * 0.5f, y = UI.screenHeight * 0.5f + 46f;
            if (WeaponView.ScopedAiming) y = UI.screenHeight * .5f + ScopeRules.Viewport(UI.screenWidth, UI.screenHeight).size * .35f;
            GameFont font = Text.Font; TextAnchor anchor = Text.Anchor; Color color = GUI.color;
            try
            {
                Text.Font = GameFont.Tiny; Text.Anchor = TextAnchor.UpperCenter;
                string mode = g.burst ? HeatState.Label(g.heat.Mode) + (FpsMod.Settings.showHints ? "  [B]" : "") : L.T("单发", "Single");
                if (aiming) mode += L.T("  · 机瞄", "  · iron sights");
                GUI.color = new Color(1f, 1f, 1f, 0.85f);
                Widgets.Label(new Rect(cx - 120f, y, 240f, 20f), mode);
                if (g.capacity > 0)
                {
                    float w = 160f, h = 6f;
                    Rect bar = new Rect(cx - w * 0.5f, y + 20f, w, h);
                    float fill = (float)(g.heat.Heat / g.capacity);
                    Widgets.DrawBoxSolid(bar, new Color(0f, 0f, 0f, 0.55f));
                    Color heat = g.heat.Locked ? new Color(1f, 0.2f, 0.15f, 0.95f) : Color.Lerp(new Color(1f, 0.85f, 0.3f, 0.9f), new Color(1f, 0.35f, 0.1f, 0.95f), fill);
                    Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, w * Mathf.Clamp01(fill), h), heat);
                    if (g.heat.Locked)
                    {
                        GUI.color = new Color(1f, 0.4f, 0.3f);
                        Widgets.Label(new Rect(cx - 120f, y + 28f, 240f, 20f), L.T("过热 · 冷却中", "Overheated · cooling"));
                    }
                }
            }
            finally { Text.Font = font; Text.Anchor = anchor; GUI.color = color; }
        }
    }

    // Hand grenades: the view's elevation sets the throw (45 degrees farthest); a dashed arc and the blast ring show
    // where it will go; LMB throws. The game's own cool-down stance paces throws.
    internal static class Grenades
    {
        private static readonly List<V3> path = new List<V3>();
        private static int previewFrame = -1;

        public static void Frame(Pawn pawn, Verb_LaunchProjectile verb, bool pressed)
        {
            if (!pressed || pawn.stances.curStance is Stance_Busy || !Magazines.Ready(pawn, verb) || !verb.Available()) return;
            Ballistics.PredictThrow(pawn, verb, path, out V3 landing);
            if (!Ballistics.ThrowGrenade(pawn, verb)) return;
            Magazines.Shot(pawn, verb);
            IntVec3 cell = FloorSpace.Cell(pawn.Map, landing).ClampInsideMap(pawn.Map);
            int cool = Math.Max(10, verb.verbProps.AdjustedCooldownTicks(verb, pawn));
            pawn.stances.SetStance(new Stance_Cooldown(cool, new LocalTargetInfo(cell), verb));
            WeaponView.Throw();
        }

        // Drawn each frame while a grenade is in hand during possession.
        public static void Preview(Pawn pawn, Verb_LaunchProjectile verb)
        {
            if (previewFrame == Time.frameCount) return;
            previewFrame = Time.frameCount;
            Ballistics.PredictThrow(pawn, verb, path, out V3 landing);
            float radius = verb.Projectile?.projectile?.explosionRadius ?? 0f;
            Visuals.ThrowPreview(path, landing, radius, Vector3.zero);
        }
    }
}
