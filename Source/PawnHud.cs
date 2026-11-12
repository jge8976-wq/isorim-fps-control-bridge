using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class PawnHud
    {
        private static readonly Color background = new Color(.035f, .045f, .06f, .9f);
        private static readonly Color barColor = new Color(.3f, .7f, .85f);
        private static float nextInventoryRead;
        private static Pawn lastPawn;
        private static string inventoryText;

        public static void Draw(Pawn pawn, string controlState)
        {
            // Native main tabs keep the entire left edge while open.
            if (Bridge.MainTabBlocks || InventoryPanel.Blocking) return;
            float width = Math.Min(354f, UI.screenWidth - 24f);
            float height = 238f;
            if (width < 180 || UI.screenHeight < 330) return;
            // Leave the bottom command/gizmo rows available; the new HUD is read-only.
            Rect rect = new Rect(12, Math.Max(12, UI.screenHeight - height - 144), width, height);
            GameFont font = Text.Font; TextAnchor anchor = Text.Anchor;
            bool wrap = Text.WordWrap; Color color = GUI.color;
            try
            {
                GUI.color = Color.white; Text.Font = GameFont.Tiny; Text.Anchor = TextAnchor.UpperLeft; Text.WordWrap = false;
                Widgets.DrawBoxSolid(rect, background);
                GUI.BeginGroup(rect);
                try
                {
                    Label(new Rect(9, 5, width - 18, 21), pawn.LabelShortCap + L.T("  ·  健康 ", "  ·  health ") + pawn.health.summaryHealth.SummaryHealthPercent.ToString("P0")
                        + (pawn.Drafted ? L.T("  ·  已征召", "  ·  drafted") : L.T("  ·  未征召", "  ·  undrafted")));
                    DrawBody(pawn, new Rect(10, 31, 60, 86));
                    DrawNeeds(pawn, new Rect(84, 28, width - 94, 91));
                    // 0.9.0 stamina, under the needs: green, amber when low, red while out of breath.
                    if (Stances.For(pawn))
                    {
                        StanceState st = Stances.State;
                        Color c = st.Exhausted ? new Color(.95f, .25f, .2f) : st.Fill < .35f ? new Color(1f, .75f, .25f) : new Color(.4f, .9f, .45f);
                        Label(new Rect(10, 115, 74, 14), st.Exhausted ? L.T("气喘吁吁", "Out of breath") : L.T("耐力 ", "Stamina ") + Mathf.RoundToInt(st.Capacity * 100f) + "%");
                        Widgets.DrawBoxSolid(new Rect(84, 120, width - 94, 4), new Color(.2f, .23f, .28f));
                        Widgets.DrawBoxSolid(new Rect(84, 120, (width - 94) * Mathf.Clamp01(st.Fill), 4), c);
                    }
                    Widgets.DrawBoxSolid(new Rect(9, 125, width - 18, 1), new Color(.25f, .3f, .35f));
                    ThingWithComps equipped = pawn.equipment?.Primary;
                    if (equipped != null) Widgets.ThingIcon(new Rect(10, 134, 34, 34), equipped);
                    Label(new Rect(51, 131, width - 60, 20), equipped?.LabelCap.ToString() ?? L.T("未装备武器", "No weapon"));
                    string attack = pawn.stances?.curStance is Stance_Warmup ? L.T("瞄准中", "Aiming") : pawn.stances?.curStance is Stance_Cooldown ? L.T("射击 / 冷却中", "Firing / cooling") : L.T("就绪", "Ready");
                    Label(new Rect(51, 150, width - 60, 20), (FpsMod.Settings.showHints ? ControlKeys.Label(ControlAction.NextWeapon)
                        + L.T(" 换武器 · ", " weapon · ") + ControlKeys.Label(ControlAction.Inventory) + L.T(" 背包 | ", " inventory | ") : "") + attack);
                    if (lastPawn != pawn || Time.unscaledTime >= nextInventoryRead)
                    {
                        lastPawn = pawn; nextInventoryRead = Time.unscaledTime + .2f;
                        var names = new List<string>();
                        foreach (var item in WeaponCycle.Weapons(pawn)) names.Add(item.LabelShortCap + (item == equipped ? L.T(" [当前]", " [current]") : ""));
                        inventoryText = names.Count == 0 ? L.T("武器库存：空", "Weapons carried: none") : L.T("武器库存：", "Weapons carried: ") + string.Join(" / ", names);
                    }
                    Label(new Rect(9, 174, width - 18, 20), FpsMod.Settings.showHints ? WeaponCycle.VisibleNotice ?? inventoryText : inventoryText);
                    if (FpsMod.Settings.showHints)
                    {
                        Label(new Rect(9, 194, width - 18, 20), ControlHints.For(controlState));
                        Label(new Rect(9, 214, width - 18, 20), ControlKeys.Label(ControlAction.Capture) + L.T(" 释放鼠标 · ", " release mouse · ")
                            + ControlKeys.Label(ControlAction.ViewCycle) + L.T(" 切换视角", " cycle view"));
                    }
                }
                finally { GUI.EndGroup(); }
            }
            finally { Text.Font = font; Text.Anchor = anchor; Text.WordWrap = wrap; GUI.color = color; }
        }

        private static void DrawBody(Pawn pawn, Rect area)
        {
            var arms = new List<BodyPartRecord>(); var legs = new List<BodyPartRecord>(); BodyPartRecord head = null;
            foreach (var part in pawn.RaceProps.body.AllParts)
            {
                if (part.depth != BodyPartDepth.Outside) continue;
                if (part.def.defName == "Head") head = part;
                if (part.def.defName == "Arm") arms.Add(part);
                if (part.def.defName == "Leg") legs.Add(part);
            }
            Part(pawn, head, area, .36f, 0, .28f, .16f);
            Part(pawn, pawn.RaceProps.body.corePart, area, .29f, .19f, .42f, .38f);
            Part(pawn, arms.Count > 0 ? arms[0] : null, area, .08f, .2f, .15f, .39f);
            Part(pawn, arms.Count > 1 ? arms[1] : null, area, .77f, .2f, .15f, .39f);
            Part(pawn, legs.Count > 0 ? legs[0] : null, area, .3f, .61f, .17f, .39f);
            Part(pawn, legs.Count > 1 ? legs[1] : null, area, .53f, .61f, .17f, .39f);
        }

        private static void Part(Pawn pawn, BodyPartRecord part, Rect area, float x, float y, float w, float h)
        {
            if (part == null) return;
            var set = pawn.health.hediffSet;
            bool missing = set.PartIsMissing(part);
            float health = 1f;
            foreach (BodyPartRecord child in pawn.RaceProps.body.AllParts)
            {
                BodyPartRecord root = child;
                while (root != null && root != part) root = root.parent;
                if (root == null) continue;
                // A torso should not duplicate limb/head injury colors.
                if (part == pawn.RaceProps.body.corePart && child != part && child.depth == BodyPartDepth.Outside) continue;
                float max = child.def.GetMaxHealth(pawn);
                if (max > 0) health = Math.Min(health, set.PartIsMissing(child) ? 0 : Mathf.Clamp01(set.GetPartHealth(child) / max));
            }
            Rect box = new Rect(area.x + x * area.width, area.y + y * area.height, w * area.width, h * area.height);
            Widgets.DrawBoxSolid(box, missing ? new Color(.35f, .35f, .4f) : Color.Lerp(new Color(.95f, .2f, .16f), new Color(.28f, .85f, .68f), health));
        }

        private static void DrawNeeds(Pawn pawn, Rect area)
        {
            if (pawn.needs?.AllNeeds == null) return;
            var needs = pawn.needs.AllNeeds.FindAll(n => n.ShowOnNeedList);
            int rows = 4, count = Math.Min(needs.Count, 8);
            float width = (area.width - 10f) / 2f;
            for (int i = 0; i < count; i++)
            {
                Need n = needs[i]; float value = Mathf.Clamp01(n.CurLevelPercentage);
                float x = area.x + (i / rows) * (width + 10), y = area.y + (i % rows) * 23;
                Label(new Rect(x, y, width, 18), n.LabelCap + " " + value.ToString("P0"));
                Widgets.DrawBoxSolid(new Rect(x, y + 18, width, 3), new Color(.2f, .23f, .28f));
                Widgets.DrawBoxSolid(new Rect(x, y + 18, width * value, 3), barColor);
            }
        }

        private static void Label(Rect rect, string value)
        {
            value ??= "";
            if (Text.CalcSize(value).x > rect.width)
            {
                int count = value.Length;
                while (count > 0 && Text.CalcSize(value.Substring(0, count) + "…").x > rect.width) count--;
                value = value.Substring(0, count) + "…";
            }
            Widgets.Label(rect, value);
        }
    }
}
