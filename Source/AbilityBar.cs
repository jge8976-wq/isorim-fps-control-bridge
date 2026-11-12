using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AbilityBar
    {
        // Mod abilities keep their own pages. The two first-person skills have a separate, fixed row and key bindings.
        private static readonly List<Ability> abilities = new List<Ability>();
        private static readonly KeyCode[] numbers = { KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9 };
        private static readonly KeyCode[] letters = { KeyCode.Z, KeyCode.X, KeyCode.C, KeyCode.V };
        private static Pawn owner;
        private static int page, nextRead;
        private static KeyCode[] Keys => FpsMod.Settings.abilityNumberKeys ? numbers : letters;
        public static void Reset() { owner = null; abilities.Clear(); page = 0; nextRead = 0; }
        public static bool Owns(KeyCode key)
        {
            if (SkillHotkeys.Owns(key) || ControlKeys.Matches(ControlAction.AbilityPage, key)) return true;
            if (ControlKeys.Owns(key) && !ControlKeys.Matches(ControlAction.Prone, key)) return false;
            foreach (KeyCode k in Keys) if (k == key) return true;
            return false;
        }

        private static void Refresh(Pawn pawn)
        {
            if (owner == pawn && Time.frameCount < nextRead) return;
            owner = pawn; nextRead = Time.frameCount + 15;
            abilities.Clear();
            var own = new List<Ability>();
            if (pawn.abilities?.abilities != null)
                foreach (Ability ability in pawn.abilities.abilities)
                    if (ability?.def != null && !own.Contains(ability)) own.Add(ability);
            own.Sort((a, b) => string.CompareOrdinal(a.def.defName, b.def.defName));
            abilities.AddRange(own);
            page = Math.Min(page, Math.Max(0, (abilities.Count - 1) / Keys.Length));
        }

        public static bool Frame(Pawn pawn)
        {
            if (SkillHotkeys.Frame(pawn)) return true;
            Refresh(pawn);
            if (ControlKeys.Down(ControlAction.AbilityPage)) { page = (page + 1) % Math.Max(1, (abilities.Count + Keys.Length - 1) / Keys.Length); return true; }
            for (int i = 0; i < Keys.Length; i++)
                if (Input.GetKeyDown(Keys[i]) && Owns(Keys[i]))
                {
                    int index = page * Keys.Length + i;
                    if (index < abilities.Count)
                    {
                        Cast(pawn, abilities[index]);
                    }
                    return true;
                }
            return false;
        }

        private static void Cast(Pawn pawn, Ability ability)
        {
            if (pawn.CurJob?.ability != null || pawn.stances.stunner.Stunned) { Reject(L.T("当前动作尚未结束", "The current action has not finished")); return; }
            if (ability.GizmoDisabled(out string reason)) { Reject(reason ?? L.T("能力不可用", "Ability unavailable")); return; }
            if (!ability.CanQueueCast) { Reject(L.T("能力正在使用或无法排队", "Ability in use or cannot be queued")); return; }
            // World targets and two-stage destinations retain their native selector/confirmation, with cursor released
            // by the existing targeter capture gate. Never invent a destination or skip a resource/confirmation check.
            if (ability.def.targetWorldCell || ability.verb.DestinationSelector != null)
            {
                new Command_Ability(ability, pawn).ProcessInput(new Event { type = EventType.MouseDown, button = 0 });
                return;
            }
            LocalTargetInfo target = ability.def.targetRequired ? AimPicker.Target(pawn, ability.verb) : new LocalTargetInfo(pawn);
            if (ability.def.targetRequired && !target.IsValid) target = Ballistics.FallbackTarget(pawn, ability.verb);
            if (!target.IsValid || !ability.CanApplyOn(target) || (ability.def.targetRequired
                && (!ability.verb.ValidateTarget(target, false) || !ability.verb.CanHitTarget(target))))
            { Reject(L.T("准心处没有有效目标，或超出范围／被遮挡", "No valid target at the crosshair, or out of range / blocked")); return; }
            ability.QueueCastingJob(target, LocalTargetInfo.Invalid);
        }
        private static void Reject(string text) => Messages.Message(text, MessageTypeDefOf.RejectInput, false);

        public static void Draw(Pawn pawn)
        {
            if (!FpsMod.Settings.showAbilityBar || Bridge.MainTabBlocks || UI.screenWidth < 680) return;
            DrawFixedSkills(pawn);
            Refresh(pawn);
            if (abilities.Count == 0) return;
            int first = page * Keys.Length, count = Math.Min(Keys.Length, abilities.Count - first);
            float width = Math.Min(68f, (UI.screenWidth - 400f) / Math.Max(1, count));
            float x = UI.screenWidth - count * width - 16f, y = UI.screenHeight - 218f;
            GameFont font = Text.Font; TextAnchor anchor = Text.Anchor; Color color = GUI.color; bool wrap = Text.WordWrap;
            try
            {
                Text.Font = GameFont.Tiny; Text.Anchor = TextAnchor.UpperLeft; Text.WordWrap = false;
                for (int i = 0; i < count; i++)
                {
                    Rect rect = new Rect(x + width * i, y, width - 3f, 72f);
                    string key = FpsMod.Settings.abilityNumberKeys ? (i + 1).ToString() : Keys[i].ToString();
                    Ability ability = abilities[first + i];
                    bool disabled = ability.GizmoDisabled(out _);
                    Widgets.DrawBoxSolid(rect, new Color(.025f, .04f, .06f, .93f));
                    GUI.color = disabled ? new Color(.5f, .5f, .5f) : Color.white;
                    if (ability.def.uiIcon != null) GUI.DrawTexture(new Rect(rect.x + 5, y + 15, 28, 28), ability.def.uiIcon);
                    Widgets.Label(new Rect(rect.x + 3, y, width - 5, 17), key);
                    Widgets.Label(new Rect(rect.x + 3, y + 44, width - 5, 18), ability.def.LabelCap);
                    if (ability.CooldownTicksRemaining > 0) Widgets.Label(new Rect(rect.x + 3, y + 58, width - 5, 16), (ability.CooldownTicksRemaining / 60f).ToString("0.0") + "s");
                }
                GUI.color = Color.white;
                if (FpsMod.Settings.showHints) Widgets.Label(new Rect(x, y - 22, count * width, 20), L.T("主动能力 · 第 ", "Abilities · page ") + (page + 1) + L.T(" 页 · 0 翻页", " · 0 next page"));
            }
            finally { Text.Font = font; Text.Anchor = anchor; GUI.color = color; Text.WordWrap = wrap; }
        }

        private static void DrawFixedSkills(Pawn pawn)
        {
            // Above the ordinary paged bar, including when the pawn has no mod abilities or does not yet qualify.
            const float width = 138f;
            float x = UI.screenWidth - 2f * width - 16f, y = UI.screenHeight - 316f;
            GameFont font = Text.Font; TextAnchor anchor = Text.Anchor; Color color = GUI.color; bool wrap = Text.WordWrap;
            try
            {
                Text.Font = GameFont.Tiny; Text.Anchor = TextAnchor.UpperLeft; Text.WordWrap = false;
                DrawSkill(pawn, CyberSkills.Kind.Sandevistan, new Rect(x, y, width - 3f, 72f), SkillHotkeys.Label(CyberSkills.Kind.Sandevistan), width);
                DrawSkill(pawn, CyberSkills.Kind.DeathEye, new Rect(x + width, y, width - 3f, 72f), SkillHotkeys.Label(CyberSkills.Kind.DeathEye), width);
            }
            finally { Text.Font = font; Text.Anchor = anchor; GUI.color = color; Text.WordWrap = wrap; }
        }

        // A skill's slot: a coloured badge (cyan Sandevistan, red Death Eye), lit while on, its recharge time below.
        private static void DrawSkill(Pawn pawn, CyberSkills.Kind k, Rect rect, string key, float width)
        {
            bool on = CyberSkills.Active(pawn, k);
            bool available = CyberSkills.Has(pawn, k);
            int left = CyberSkills.CooldownLeft(pawn, k);
            Color tint = k == CyberSkills.Kind.Sandevistan ? new Color(.2f, .9f, 1f) : new Color(1f, .25f, .2f);
            Widgets.DrawBoxSolid(rect, on ? new Color(tint.r * .35f, tint.g * .35f, tint.b * .35f, .95f) : new Color(.025f, .04f, .06f, .93f));
            Rect badge = new Rect(rect.x + 5, rect.y + 15, 28, 28);
            Widgets.DrawBoxSolid(badge, left > 0 || !available ? new Color(.3f, .3f, .3f) : tint);
            Color c0 = GUI.color; TextAnchor a0 = Text.Anchor; GameFont f0 = Text.Font;
            GUI.color = Color.black; Text.Font = GameFont.Medium; Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(badge, k == CyberSkills.Kind.Sandevistan ? "S" : "E");
            Text.Font = f0; Text.Anchor = a0; GUI.color = left > 0 || !available ? new Color(.6f, .6f, .6f) : Color.white;
            Widgets.Label(new Rect(rect.x + 3, rect.y, width - 5, 17), key);
            Widgets.Label(new Rect(rect.x + 3, rect.y + 44, width - 5, 18), CyberSkills.Label(k));
            if (!available) Widgets.Label(new Rect(rect.x + 3, rect.y + 58, width - 5, 16), k == CyberSkills.Kind.Sandevistan
                ? L.T("需智力/格斗 10 级", "Intellectual/Melee 10") : L.T("需射击 10 级", "Shooting 10"));
            else if (left > 0) Widgets.Label(new Rect(rect.x + 3, rect.y + 58, width - 5, 16), (left / 60f).ToString("0") + "s");
            else if (on) { GUI.color = tint; Widgets.Label(new Rect(rect.x + 3, rect.y + 58, width - 5, 16), L.T("开启中", "on")); }
            GUI.color = c0;
        }
    }
}
