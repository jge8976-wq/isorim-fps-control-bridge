using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class MountInteraction
    {
        private static MethodInfo options;
        private static FieldInfo battle, travel;
        private static bool looked;
        private static Pawn cachedRider, animal;
        private static float sampled = -10f;
        private static FloatMenuOption choice;
        public static bool Mounting(Pawn p) => p?.CurJobDef?.defName == "Mount" && GiddyMounts.Mount(p) == null;
        private static bool Enabled(Pawn p)
        {
            if (!looked)
            {
                looked = true;
                Type api = AccessTools.TypeByName("GiddyUp.Harmony.FloatMenuUtility");
                options = api == null ? null : AccessTools.DeclaredMethod(api, "AddMountingOptions",
                    new[] { typeof(Pawn), typeof(Pawn), typeof(List<FloatMenuOption>) });
                Type settings = AccessTools.TypeByName("GiddyUp.ModSettings_GiddyUp");
                if (settings != null) { battle = AccessTools.Field(settings, "battleMountsEnabled"); travel = AccessTools.Field(settings, "rideAndRollEnabled"); }
            }
            return options != null && (p.Drafted ? battle : travel)?.GetValue(null) is bool on && on;
        }
        internal static FloatMenuOption Option(Pawn rider, Pawn mount)
        {
            if (rider == null || mount == null || !Enabled(rider)) return null;
            var list = new List<FloatMenuOption>();
            options.Invoke(null, new object[] { mount, rider, list });
            return list.Find(o => !o.Disabled && o.action != null);
        }
        private static FloatMenuOption Find(Pawn p, bool fresh)
        {
            float now = Time.unscaledTime;
            if (!fresh && p == cachedRider && now - sampled < .15f) return choice;
            cachedRider = p; sampled = now; choice = null; animal = null;
            if (p?.Spawned != true || MountedControl.For(p) || Mounting(p) || !Enabled(p)) return null;
            Vector3 forward = Quaternion.Euler(0, ControlCamera.AttackYaw(p), 0) * Vector3.forward;
            float best = float.PositiveInfinity;
            foreach (Pawn a in p.Map.mapPawns.AllPawnsSpawned)
            {
                if (a == p || a.RaceProps?.Animal != true || a.Faction != p.Faction || a.Dead || a.Downed
                    || FloorSpace.Band(p.Map, a.Position).Level != FloorSpace.Band(p.Map, p.Position).Level) continue;
                Vector3 d = a.DrawPos - p.DrawPos; d.y = 0;
                if (d.sqrMagnitude > 2.6f * 2.6f || d.sqrMagnitude >= best || Vector3.Dot(d.normalized, forward) < .65f
                    || !GenSight.LineOfSight(p.Position, a.Position, p.Map, true)) continue;
                FloatMenuOption next = Option(p, a);
                if (next == null) continue;
                best = d.sqrMagnitude; animal = a; choice = next;
            }
            return choice;
        }
        public static bool TryMount(Pawn p)
        {
            FloatMenuOption o = Find(p, true);
            if (o == null) return false;
            Stances.State.CancelSprint(); FireControl.CancelInput();
            if (Bindings.Avatar != null) Bindings.PhysicsPosition.SetValue(Bindings.Avatar, null);
            o.action(); choice = null; sampled = -10f;
            return true;
        }
        public static void DrawHud(Pawn p)
        {
            if (Find(p, false) == null || animal == null) return;
            Color old = GUI.color; TextAnchor anchor = Text.Anchor; GameFont font = Text.Font;
            try
            {
                Text.Font = GameFont.Small; Text.Anchor = TextAnchor.UpperCenter; GUI.color = new Color(.65f, 1f, .8f);
                Widgets.Label(new Rect(UI.screenWidth * .5f - 240, UI.screenHeight * .5f + 82, 480, 26),
                    ControlKeys.Label(ControlAction.InteractLeanRight) + L.T(" 上马 · ", " mount · ") + animal.LabelShortCap);
            }
            finally { GUI.color = old; Text.Anchor = anchor; Text.Font = font; }
        }
    }
}
