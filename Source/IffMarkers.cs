using System;
using System.Collections.Generic;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // 0.8.1 friend-or-foe marks over everyone in sight, projected the way IsoRim's first-person camera draws (eye, view
    // yaw/pitch, first-person field of view): red hostile (with distance up close), green own side, blue friendly or
    // guest, a small yellow dot for neutral creatures. Hidden behind walls: no mark (sight rechecked a few times a second).
    internal static class IffMarkers
    {
        private const float Range = 60f, DistanceLabel = 30f;
        private const int SightEvery = 12;
        internal static int LastDrawCount;
        private static readonly Dictionary<Pawn, (int frame, bool seen)> sight = new Dictionary<Pawn, (int, bool)>();
        internal static readonly Color Hostile = new Color(1f, .22f, .18f), Own = new Color(.35f, 1f, .45f), Friendly = new Color(.35f, .7f, 1f), Neutral = new Color(1f, .9f, .3f);

        public static void Reset() => sight.Clear();

        // A world point (true space) on the screen, as the first-person camera shows it; false behind the eye.
        public static bool Project(Vector3 world, out Vector2 screen, out float distance)
        {
            Vector3 eye = ControlCamera.RenderEye();
            Vector3 rel = Quaternion.Inverse(Quaternion.Euler(FirstPerson.Pitch, FirstPerson.Yaw, 0f)) * (world - eye);
            distance = (world - eye).magnitude;
            float w = UI.screenWidth, h = UI.screenHeight;
            float tanHalf = Mathf.Tan(Mathf.Clamp(IsoRimMod.Settings.fpFov, 15f, 100f) * .5f * Mathf.Deg2Rad), aspect = w / Math.Max(1f, h);
            screen = new Vector2(w * .5f + rel.x / (rel.z * tanHalf * aspect) * w * .5f, h * .5f - rel.y / (rel.z * tanHalf) * h * .5f);
            return rel.z > .3f;
        }

        public static void Draw(Pawn self)
        {
            LastDrawCount=0;
            if (!FpsMod.Settings.showIff || self?.Map == null || !View3D.Active) return;
            Map map = self.Map;
            Vector3 eye = ControlCamera.RenderEye();
            Quaternion inv = Quaternion.Inverse(Quaternion.Euler(FirstPerson.Pitch, FirstPerson.Yaw, 0f));
            float w = UI.screenWidth, h = UI.screenHeight;
            float tanHalf = Mathf.Tan(Mathf.Clamp(IsoRimMod.Settings.fpFov, 15f, 100f) * .5f * Mathf.Deg2Rad), aspect = w / Math.Max(1f, h);
            GameFont f0 = Text.Font; TextAnchor a0 = Text.Anchor; Color c0 = GUI.color;
            try
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn p = pawns[i];
                    if (p == self || p.Dead || p.Position.Fogged(map)) continue;
                    V3 spot = FloorSpace.Spot(p);
                    Vector3 head = new Vector3(spot.X, spot.Y + Bindings.PickHeight(p) + .28f, spot.Z);
                    Vector3 rel = inv * (head - eye);
                    float dist = (head - eye).magnitude;
                    if (rel.z < .3f || dist > Range) continue;
                    float sx = w * .5f + rel.x / (rel.z * tanHalf * aspect) * w * .5f, sy = h * .5f - rel.y / (rel.z * tanHalf) * h * .5f;
                    if (sx < -20f || sx > w + 20f || sy < -20f || sy > h + 20f) continue;
                    if (!Seen(self, p, eye, head)) continue;
                    LastDrawCount++;
                    Color c = Kind(p, out bool dot);
                    float alpha = p.Downed ? .45f : 1f;
                    if (dot)
                    {
                        Widgets.DrawBoxSolid(new Rect(sx - 4f, sy - 4f, 8f, 8f), new Color(0f, 0f, 0f, .7f * alpha));
                        Widgets.DrawBoxSolid(new Rect(sx - 3f, sy - 3f, 6f, 6f), new Color(c.r, c.g, c.b, alpha));
                        continue;
                    }
                    // 0.9.x: brighter marks, a dark rim round them so they read against any background.
                    Text.Font = dist < 25f ? GameFont.Medium : GameFont.Small;
                    Rect mark = new Rect(sx - 18f, sy - 18f, 36f, 36f);
                    GUI.color = new Color(0f, 0f, 0f, .8f * alpha);
                    for (int k = 0; k < 4; k++) Widgets.Label(new Rect(mark.x + (k < 2 ? (k == 0 ? -2f : 2f) : 0f), mark.y + (k >= 2 ? (k == 2 ? -2f : 2f) : 0f), mark.width, mark.height), "▼");
                    GUI.color = new Color(Mathf.Min(1f, c.r * 1.15f + .1f), Mathf.Min(1f, c.g * 1.15f + .1f), Mathf.Min(1f, c.b * 1.15f + .1f), alpha);
                    Widgets.Label(mark, "▼");
                    if (c == Hostile && dist < DistanceLabel)
                    {
                        Text.Font = GameFont.Small;
                        Rect label = new Rect(sx - 32f, sy - 38f, 64f, 20f);
                        GUI.color = new Color(0f, 0f, 0f, .8f * alpha);
                        Widgets.Label(new Rect(label.x + 1f, label.y + 1f, label.width, label.height), dist.ToString("F0") + "m");
                        GUI.color = new Color(1f, .55f, .5f, alpha);
                        Widgets.Label(label, dist.ToString("F0") + "m");
                    }
                }
            }
            finally { Text.Font = f0; Text.Anchor = a0; GUI.color = c0; }
        }

        internal static Color Kind(Pawn p, out bool dot)
        {
            dot = false;
            if (p.HostileTo(Faction.OfPlayer)) return Hostile;
            if (p.Faction == Faction.OfPlayer) return Own;
            if (p.Faction != null || p.IsPrisonerOfColony || p.guest != null && p.guest.HostFaction == Faction.OfPlayer) return Friendly;
            dot = true;
            return Neutral;
        }

        // Line of sight from the eye to the mark's point (walls, roofs, floors; not other creatures), cached a few frames.
        // At most this many sight traces a frame: on a crowded map the rest keep their last answer a frame or two longer.
        private const int SightBudget = 8;
        private static int budgetFrame = -1, budgetUsed;

        private static bool Seen(Pawn self, Pawn p, Vector3 eye, Vector3 head)
        {
            bool known = sight.TryGetValue(p, out var s);
            if (known && Time.frameCount - s.frame < SightEvery) return s.seen;
            if (budgetFrame != Time.frameCount) { budgetFrame = Time.frameCount; budgetUsed = 0; }
            if (budgetUsed >= SightBudget) return known && s.seen;   // checked on a later frame; unknown ones stay hidden meanwhile
            budgetUsed++;
            Vector3 aim = head - Vector3.up * .3f;
            bool seen = !Ballistics.SightContact(self, new V3(eye.x, eye.y, eye.z), new V3(aim.x, aim.y, aim.z), out V3 hit, false)
                || (new Vector3(hit.X, hit.Y, hit.Z) - aim).magnitude < .6f;
            if (sight.Count > 400) sight.Clear();
            sight[p] = (Time.frameCount, seen);
            return seen;
        }
    }
}
