using System;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // 0.8.0 possessing the colony's own mechanoids. IsoRim's first person only takes a person, so an own mech is put
    // into it here the same way (Perspective Shift's avatar takes any pawn). The link holds inside the mechanitor's
    // command range: outside it the picture breaks up and after a few seconds the link drops. G is the mech's own
    // move: Scyther dash-slash, Lancer charge (aiming builds a charge for the next shot), Centipede chassis lock.
    internal static class MechControl
    {
        private const float CommandRange = 24.9f, DropAfter = 3f;
        private enum Kind { Other, Scyther, Lancer, Centipede }
        private static AccessTools.FieldRef<Pawn> isoPawn;
        private static AccessTools.FieldRef<float> isoPitch;
        private static Pawn owner;
        private static Kind kind;
        private static float signal = 1f, outSince = -1f, charge, armedFactor;
        private static bool locked, wasCooldown;
        private static int readyTick, armedUntil;
        private static ThingDef armedWeapon;
        public static bool Enabled { get; private set; }

        public static bool IsMech(Pawn p) => p?.RaceProps != null && p.RaceProps.IsMechanoid;
        public static bool Mine(Pawn p) => owner != null && p == owner && owner == Bridge.ShootingPawn;
        public static bool Locked => locked && Mine(owner);

        public static bool CanPossess(Pawn p) => Enabled && ModsConfig.BiotechActive && IsMech(p) && p.Faction == Faction.OfPlayer && p.IsColonyMech
            && (!p.IsColonyMechRequiringMechanitor() || p.GetOverseer() != null);

        public static void Install(Harmony harmony)
        {
            isoPawn = AccessTools.StaticFieldRefAccess<Pawn>(AccessTools.Field(typeof(FirstPerson), "pawn") ?? throw new MissingFieldException("FirstPerson.pawn"));
            isoPitch = AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(typeof(FirstPerson), "lookPitch") ?? throw new MissingFieldException("FirstPerson.lookPitch"));
            harmony.Patch(Bindings.Method(typeof(FirstPerson), "Toggle", true), prefix: new HarmonyMethod(typeof(MechControl), nameof(BeforeToggle)) { priority = Priority.First });
            harmony.Patch(AccessTools.PropertyGetter(typeof(FirstPerson), nameof(FirstPerson.Eye)) ?? throw new MissingMethodException("FirstPerson.Eye"),
                postfix: new HarmonyMethod(typeof(MechControl), nameof(AfterEye)) { priority = Priority.Last });
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Thing), "TakeDamage", new[] { typeof(DamageInfo) }) ?? throw new MissingMethodException("Thing.TakeDamage"),
                prefix: new HarmonyMethod(typeof(MechControl), nameof(BeforeDamage)));
            Enabled = true;
        }

        // IsoRim refuses anything but a person; an own mech enters first person here with the same steps, and the
        // bridge's own Toggle postfix then opens the session as for a colonist.
        public static bool BeforeToggle()
        {
            if (!Enabled || FirstPerson.On) return true;
            Pawn p = Find.Selector?.NumSelected == 1 ? Find.Selector.SingleSelectedThing as Pawn : null;
            if (!CanPossess(p) || !p.Spawned || p.Map != Find.CurrentMap) return true;
            if (!MechanitorUtility.InMechanitorCommandRange(p, p))
            { Messages.Message(p.LabelShort + L.T(" 不在机械师指挥范围内，无法接入。", " is out of the mechanitor's command range and cannot be linked."), MessageTypeDefOf.RejectInput, false); return false; }
            View3D.Active = true;
            isoPawn() = p;
            Bindings.SetHeadingReady(false); Bindings.SetLookYaw(0f); isoPitch() = 8f;
            return false;
        }

        // Eye height follows the machine's size (a centipede looks from higher, a militor from lower).
        public static void AfterEye(ref Vector3 __result)
        {
            Pawn p = FirstPerson.Pawn;
            if (!IsMech(p)) return;
            float standing = Mathf.Clamp(IsoRimMod.Settings.fpEyeHeight, .3f, 3f);
            __result.y += (Mathf.Clamp(.55f + .45f * p.BodySize, .6f, 1.7f) - 1f) * standing;
        }

        // The Lancer's charged shot: the first hit of that weapon soon after the charged shot left.
        public static void BeforeDamage(ref DamageInfo dinfo)
        {
            if (armedFactor <= 0f || owner == null || dinfo.Instigator != owner) return;
            if (Find.TickManager.TicksGame > armedUntil) { armedFactor = 0f; return; }
            if (dinfo.Weapon != armedWeapon) return;
            dinfo.SetAmount(dinfo.Amount * armedFactor);
            armedFactor = 0f;
        }

        // ---- session

        public static void Begin(Pawn pawn)
        {
            Reset();
            if (!IsMech(pawn)) return;
            owner = pawn;
            string race = pawn.def.defName;
            kind = race.Contains("Scyther") ? Kind.Scyther : race.Contains("Lancer") ? Kind.Lancer : race.Contains("Centipede") ? Kind.Centipede : Kind.Other;
            Log.Message("[FpsBridge] MECH_LINK pawn=" + pawn.thingIDNumber + "; race=" + race + "; kind=" + kind);
        }

        public static void Reset()
        {
            owner = null; kind = Kind.Other; signal = 1f; outSince = -1f; charge = 0f; locked = false; wasCooldown = false;
            readyTick = 0;
        }

        // Once per input frame. Returns a reason when the link drops.
        public static string Frame(Pawn pawn, bool drive, bool paused)
        {
            if (!Mine(pawn)) return null;
            bool inRange = MechanitorUtility.InMechanitorCommandRange(pawn, pawn);
            Pawn overseer = pawn.GetOverseer();
            signal = !inRange ? 0f : overseer != null && overseer.Spawned && overseer.Map == pawn.Map
                ? Mathf.Clamp01(1.15f - pawn.Position.DistanceTo(overseer.Position) / CommandRange) : 1f;
            if (inRange) outSince = -1f;
            else if (outSince < 0f) outSince = Time.unscaledTime;
            else if (Time.unscaledTime - outSince > DropAfter) return L.T("信号中断：", "Signal lost: ") + pawn.LabelShort + L.T(" 离开了机械师指挥范围。", " left the mechanitor's command range.");
            if (drive && !paused && ControlKeys.Down(ControlAction.MechAbility)) Special(pawn);
            return null;
        }

        // ---- G

        private static void Special(Pawn pawn)
        {
            int now = Find.TickManager.TicksGame;
            if (now < readyTick) { Messages.Message(L.T("技能冷却中 ", "Ability cooling down ") + (readyTick - now).ToStringSecondsFromTicks(), MessageTypeDefOf.RejectInput, false); return; }
            switch (kind)
            {
                case Kind.Scyther: Dash(pawn); break;
                case Kind.Centipede:
                    locked = !locked; readyTick = now + 60;
                    Stances.State.CancelSprint();
                    Messages.Message(locked ? L.T("底盘锁定：不能移动，射击更稳", "Chassis locked: cannot move, steadier aim") : L.T("底盘解锁", "Chassis unlocked"), MessageTypeDefOf.SilentInput, false);
                    break;
                case Kind.Lancer: Messages.Message(L.T("按住右键瞄准即可蓄能，下一发充能射击伤害提高。", "Hold RMB to aim and charge: the next charged shot deals more damage."), MessageTypeDefOf.SilentInput, false); break;
                default: Messages.Message(pawn.LabelShort + L.T(" 没有专属技能。", " has no special ability."), MessageTypeDefOf.RejectInput, false); break;
            }
        }

        // Scyther: up to four cells along the view in an instant, then the blades sweep everything hostile in front.
        private static void Dash(Pawn pawn)
        {
            Map map = pawn.Map;
            float yaw = FirstPerson.Yaw * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
            IntVec3 start = pawn.Position, end = start;
            for (int i = 1; i <= 4; i++)
            {
                IntVec3 c = start + new Vector3(dir.x * i, 0f, dir.z * i).ToIntVec3();
                if (c == end) continue;
                if (!c.InBounds(map) || !c.Standable(map) || c.GetFirstPawn(map) != null || !GenSight.LineOfSight(start, c, map, true)) break;
                end = c;
            }
            if (end != start)
            {
                FleckMaker.ThrowDustPuff(start, map, 1.2f);
                pawn.Position = end;
                pawn.Notify_Teleported(true, false);
                Bindings.PhysicsPosition.SetValue(Bindings.Avatar, null);
            }
            Verb blade = null; float best = 0f;
            foreach (Verb v in pawn.VerbTracker.AllVerbs)
                if (v is Verb_MeleeAttack && v.Available())
                {
                    float d = v.verbProps.AdjustedMeleeDamageAmount(v, pawn);
                    if (d > best) { best = d; blade = v; }
                }
            int hits = 0;
            if (blade != null)
                foreach (Pawn t in new System.Collections.Generic.List<Pawn>(map.mapPawns.AllPawnsSpawned))
                {
                    if (t == pawn || t.Dead || !t.HostileTo(pawn) || (t.Position - pawn.Position).LengthHorizontal > 1.9f) continue;
                    Vector3 to = (t.DrawPos - pawn.DrawPos).Yto0();
                    if (to.sqrMagnitude > .01f && Vector3.Angle(dir, to) > 75f) continue;
                    var dinfo = new DamageInfo(DamageDefOf.Cut, blade.verbProps.AdjustedMeleeDamageAmount(blade, pawn),
                        blade.verbProps.AdjustedArmorPenetration(blade, pawn), FirstPerson.Yaw, pawn);
                    t.TakeDamage(dinfo);
                    hits++;
                }
            WeaponView.Melee(true);
            readyTick = Find.TickManager.TicksGame + 360;
            Messages.Message(L.T("冲刺斩", "Dash slash") + (hits > 0 ? L.T("：命中 ", ": hits ") + hits : ""), MessageTypeDefOf.SilentInput, false);
        }

        // ---- combat (once per frame from the combat dispatch)

        public static void Combat(Pawn pawn, bool aimHeld)
        {
            if (!Mine(pawn) || kind != Kind.Lancer) return;
            float dt = Time.unscaledDeltaTime * (Find.TickManager?.TickRateMultiplier ?? 1f);
            bool busy = pawn.stances?.curStance is Stance_Busy;
            charge = aimHeld && !busy ? Mathf.Clamp01(charge + dt / 2f) : aimHeld ? charge : Mathf.Max(0f, charge - dt * 2f);
            bool cd = pawn.stances?.curStance is Stance_Cooldown;
            if (cd && !wasCooldown && charge > .05f)
            {
                armedFactor = 1f + 1.5f * charge;
                armedUntil = Find.TickManager.TicksGame + 90;
                armedWeapon = pawn.equipment?.Primary?.def;
                charge = 0f;
            }
            wasCooldown = cd;
        }

        // Bladed mechs (Scyther) show their two arm blades in the view.
        public static bool BladeArms(Pawn pawn) => Mine(pawn) && kind == Kind.Scyther;

        // View sway while firing: steady when charged or with the chassis locked.
        public static float SwayFactor(Pawn pawn) => !Mine(pawn) ? 1f : kind == Kind.Centipede && locked ? .2f
            : kind == Kind.Lancer ? 1f - .85f * charge : 1f;

        // ---- HUD

        private static readonly Color Cyan = new Color(.35f, .95f, 1f, .9f), Dim = new Color(.35f, .95f, 1f, .25f);

        public static void DrawHud(Pawn pawn, string status)
        {
            if (!Mine(pawn)) return;
            float w = UI.screenWidth, h = UI.screenHeight;
            bool heavy = kind == Kind.Centipede || pawn.BodySize >= 2f, light = pawn.BodySize < .9f;
            GameFont f0 = Text.Font; TextAnchor a0 = Text.Anchor; Color c0 = GUI.color;
            try
            {
                // Armoured cockpit for heavy machines: dark bars at top and bottom.
                if (heavy)
                {
                    Widgets.DrawBoxSolid(new Rect(0, 0, w, h * .07f), new Color(.02f, .03f, .04f, .85f));
                    Widgets.DrawBoxSolid(new Rect(0, h * .93f, w, h * .07f), new Color(.02f, .03f, .04f, .85f));
                }
                // Scan lines (combat and heavy frames), then corner brackets.
                if (!light) for (float y = 0; y < h; y += 4f) Widgets.DrawBoxSolid(new Rect(0, y, w, 1), new Color(.3f, .9f, 1f, .025f));
                float m = 22f, l = 46f;
                Bracket(m, m, l, 1, 1); Bracket(w - m, m, l, -1, 1); Bracket(m, h - m, l, 1, -1); Bracket(w - m, h - m, l, -1, -1);

                // Weak signal: static, then the countdown to the drop.
                float noise = 1f - signal;
                if (noise > .25f)
                {
                    int n = (int)(noise * noise * 260f);
                    for (int i = 0; i < n; i++)
                    {
                        float g = UnityEngine.Random.value;
                        Widgets.DrawBoxSolid(new Rect(UnityEngine.Random.value * w, UnityEngine.Random.value * h, 2 + UnityEngine.Random.value * 30f, 2),
                            new Color(g, g, g, .25f + .4f * noise));
                    }
                }
                if (outSince >= 0f)
                {
                    float left = Mathf.Max(0f, DropAfter - (Time.unscaledTime - outSince));
                    Text.Font = GameFont.Medium; Text.Anchor = TextAnchor.MiddleCenter; GUI.color = new Color(1f, .35f, .3f);
                    Widgets.Label(new Rect(0, h * .3f, w, 40), L.T("信号丢失 · ", "Signal lost · disconnect in ") + left.ToString("F1") + L.T(" 秒后断开", " s"));
                }

                // Low energy: the frame flickers.
                float energy = pawn.needs?.energy?.CurLevelPercentage ?? 1f;
                if (energy < .15f && Mathf.Repeat(Time.unscaledTime, .6f) < .12f) Widgets.DrawBoxSolid(new Rect(0, 0, w, h), new Color(0f, 0f, 0f, .35f));

                // Status panel, lower left.
                float pw = Mathf.Min(360f, w - 24f), ph = 176f, px = 14f, py = h - ph - 150f;
                Widgets.DrawBoxSolid(new Rect(px, py, pw, ph), new Color(.01f, .05f, .07f, .78f));
                Widgets.DrawBox(new Rect(px, py, pw, ph), 1);
                Text.Font = GameFont.Small; Text.Anchor = TextAnchor.UpperLeft; GUI.color = Cyan;
                Widgets.Label(new Rect(px + 10, py + 6, pw - 20, 24), "◆ " + pawn.LabelShortCap.ToString().ToUpper() + "  //  " + pawn.KindLabel);
                Text.Font = GameFont.Tiny;
                float y0 = py + 32;
                Bar(px + 10, y0, pw - 20, L.T("结构", "Hull"), pawn.health.summaryHealth.SummaryHealthPercent);
                Bar(px + 10, y0 + 24, pw - 20, L.T("能量", "Energy"), energy);
                Bar(px + 10, y0 + 48, pw - 20, L.T("信号", "Signal"), signal);
                GUI.color = Cyan;
                ThingWithComps gun = pawn.equipment?.Primary;
                Widgets.Label(new Rect(px + 10, y0 + 72, pw - 20, 20), L.T("武装：", "Arms: ") + (gun != null ? gun.LabelCap.ToString() : L.T("近战刃具", "Melee blades")));
                Widgets.Label(new Rect(px + 10, y0 + 92, pw - 20, 20), L.T("G：", "G: ") + SpecialLabel(pawn));
                GUI.color = new Color(.6f, .8f, .85f, .85f);
                Widgets.Label(new Rect(px + 10, y0 + 112, pw - 20, 20), status ?? "");
                if (kind == Kind.Lancer && charge > 0f)
                {
                    float cx = w * .5f, cy = h * .5f + 26f;
                    Widgets.DrawBoxSolid(new Rect(cx - 50, cy, 100, 4), Dim);
                    Widgets.DrawBoxSolid(new Rect(cx - 50, cy, 100 * charge, 4), charge >= 1f ? new Color(1f, .85f, .3f) : Cyan);
                }
            }
            finally { Text.Font = f0; Text.Anchor = a0; GUI.color = c0; }
        }

        private static string SpecialLabel(Pawn pawn)
        {
            int left = readyTick - Find.TickManager.TicksGame;
            string cd = left > 0 ? L.T("（冷却 ", " (cooldown ") + left.ToStringSecondsFromTicks() + L.T("）", ")") : "";
            switch (kind)
            {
                case Kind.Scyther: return L.T("冲刺斩", "Dash slash") + cd;
                case Kind.Lancer: return L.T("充能狙击 · 右键蓄能 ", "Charged shot · RMB to charge ") + (charge * 100f).ToString("F0") + "%";
                case Kind.Centipede: return (locked ? L.T("底盘已锁定", "Chassis locked") : L.T("锁定底盘", "Lock chassis")) + cd;
                default: return L.T("无", "None");
            }
        }

        private static void Bar(float x, float y, float width, string label, float value)
        {
            GUI.color = Cyan;
            Widgets.Label(new Rect(x, y, 44, 20), label);
            Widgets.DrawBoxSolid(new Rect(x + 46, y + 7, width - 96, 5), Dim);
            Color c = value < .2f ? new Color(1f, .35f, .3f) : value < .5f ? new Color(1f, .8f, .3f) : Cyan;
            Widgets.DrawBoxSolid(new Rect(x + 46, y + 7, (width - 96) * Mathf.Clamp01(value), 5), c);
            Widgets.Label(new Rect(x + width - 44, y, 44, 20), (value * 100f).ToString("F0") + "%");
        }

        private static void Bracket(float x, float y, float len, int sx, int sy)
        {
            Widgets.DrawBoxSolid(new Rect(sx > 0 ? x : x - len, sy > 0 ? y : y - 2, len, 2), Cyan);
            Widgets.DrawBoxSolid(new Rect(sx > 0 ? x : x - 2, sy > 0 ? y : y - len, 2, len), Cyan);
        }
    }
}
