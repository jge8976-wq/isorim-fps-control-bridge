using System.Collections.Generic;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // The five melee swings: left button cycles thrust, sweep, overhead, diagonal; right button is the heavy slash.
    internal enum SwingKind { Thrust, Sweep, Overhead, Diagonal, Heavy }

    // Where each swing's edge travels, as offsets from the view (yaw right +, pitch down +, degrees) over the swing
    // (s 0..1). Shared by the hit test (Melee), the held weapon's motion (WeaponView) and the blade's trail (MeleeFx).
    internal static class SwingPath
    {
        public static float Seconds(SwingKind k) => k == SwingKind.Thrust ? .22f : k == SwingKind.Sweep ? .26f : k == SwingKind.Overhead ? .30f : k == SwingKind.Diagonal ? .28f : .36f;

        public static void At(SwingKind k, float s, out float yaw, out float pitch)
        {
            float arc = Mathf.Sin(Mathf.PI * s);
            switch (k)
            {
                case SwingKind.Thrust: yaw = 0f; pitch = 0f; return;
                case SwingKind.Sweep: yaw = Mathf.Lerp(55f, -55f, s); pitch = 6f * arc - 3f; return;
                case SwingKind.Overhead: yaw = 3f * arc; pitch = Mathf.Lerp(-50f, 45f, s); return;
                case SwingKind.Diagonal: yaw = Mathf.Lerp(45f, -40f, s); pitch = Mathf.Lerp(-40f, 40f, s); return;
                default: yaw = Mathf.Lerp(55f, -55f, s); pitch = 8f * arc - 4f; return;
            }
        }

        public static string Name(SwingKind k) => k == SwingKind.Thrust ? L.T("前刺", "Thrust") : k == SwingKind.Sweep ? L.T("横扫", "Sweep") : k == SwingKind.Overhead ? L.T("下劈", "Overhead") : k == SwingKind.Diagonal ? L.T("斜劈", "Diagonal") : L.T("重劈", "Heavy slash");
    }

    // What a swing shows: the blade's trail along its path (drawn in step with the swing, then fading), and over every
    // part struck a short label (the body part, red for the head) that rises and fades, with the game's blood flash.
    internal static class MeleeFx
    {
        private const int TrailSteps = 16;
        private const float Fade = .18f, LabelSeconds = .9f;
        private static readonly List<Vector3> tip = new List<Vector3>(), root = new List<Vector3>();
        private static float trailStart = -10f, trailSeconds;
        private static Color trailColor;

        private sealed class Label { public Vector3 at; public string text; public Color color; public float start; }
        private static readonly List<Label> labels = new List<Label>();

        // eye in true space; yaw/pitch the view at the swing's start.
        public static void Trail(V3 eye, float yaw, float pitch, SwingKind kind, float reach)
        {
            tip.Clear(); root.Clear();
            Vector3 e = new Vector3(eye.X, eye.Y, eye.Z);
            if (kind == SwingKind.Thrust)
            {
                V3 d = HitMath.Direction(yaw, pitch);
                Vector3 dir = new Vector3(d.X, d.Y, d.Z);
                // A straight streak a little under and right of the view's centre, out along the thrust.
                Vector3 right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
                Vector3 start = e + dir * .55f + right * .06f - Vector3.up * .08f;
                for (int i = 0; i <= TrailSteps; i++) { Vector3 p = start + dir * (reach * .55f * i / TrailSteps); tip.Add(p); root.Add(p); }
            }
            else
            {
                float outer = Mathf.Min(reach * .45f, 1.6f), inner = outer * .55f;
                for (int i = 0; i <= TrailSteps; i++)
                {
                    SwingPath.At(kind, i / (float)TrailSteps, out float dy, out float dp);
                    V3 d = HitMath.Direction(yaw + dy, pitch + dp);
                    Vector3 dir = new Vector3(d.X, d.Y, d.Z);
                    tip.Add(e + dir * outer); root.Add(e + dir * inner);
                }
            }
            trailStart = Time.unscaledTime;
            trailSeconds = SwingPath.Seconds(kind);
            trailColor = kind == SwingKind.Heavy ? new Color(1f, .62f, .3f, .85f) : new Color(.8f, .92f, 1f, .75f);
        }

        // A struck part: its name at the point of contact (true space).
        public static void Hit(V3 at, string text, HitRegion region)
        {
            Color c = region == HitRegion.Head ? new Color(1f, .3f, .25f) : region == HitRegion.Leg ? new Color(.45f, .85f, 1f) : new Color(1f, .9f, .35f);
            if (labels.Count > 24) labels.RemoveAt(0);
            labels.Add(new Label { at = new Vector3(at.X, at.Y, at.Z), text = text, color = c, start = Time.unscaledTime });
        }

        public static void Miss(V3 at, string text)
        {
            if (labels.Count > 24) labels.RemoveAt(0);
            labels.Add(new Label { at = new Vector3(at.X, at.Y, at.Z), text = text, color = new Color(.8f, .8f, .8f), start = Time.unscaledTime });
        }

        public static void Reset() { trailStart = -10f; labels.Clear(); }

        // Every frame (3D): the trail, revealed with the swing and fading after it.
        public static void Draw()
        {
            float t = Time.unscaledTime - trailStart;
            if (t < 0f || t > trailSeconds + Fade || tip.Count < 2) return;
            float shown = Mathf.Clamp01(t / trailSeconds), alpha = t <= trailSeconds ? 1f : 1f - (t - trailSeconds) / Fade;
            int last = Mathf.Clamp(Mathf.CeilToInt(shown * (tip.Count - 1)), 1, tip.Count - 1);
            for (int i = 1; i <= last; i++)
            {
                // Older parts of the trail fade first; the newest edge is the brightest.
                float age = (last - i) / (float)Mathf.Max(1, last);
                Color c = trailColor; c.a *= alpha * (1f - .75f * age);
                Visuals.Line(tip[i - 1], tip[i], c, .05f);
                if ((root[i] - tip[i]).sqrMagnitude > 1e-4f) Visuals.Line(Vector3.Lerp(root[i - 1], tip[i - 1], .5f), Vector3.Lerp(root[i], tip[i], .5f), new Color(c.r, c.g, c.b, c.a * .45f), .12f);
            }
        }

        // The whirlwind (every frame while spinning): two bright rings round the pawn at the blade's height, the outer one
        // at the reach that strikes, pulsing with each turn.
        public static void Whirl(V3 spot, V3 eye, float pitchDeg, float angle, float radius)
        {
            float y = Mathf.Clamp(eye.Y - Mathf.Tan(pitchDeg * Mathf.Deg2Rad) * radius * .6f, spot.Y + .15f, eye.Y + .3f);
            float pulse = .6f + .4f * Mathf.Abs(Mathf.Sin(angle * Mathf.Deg2Rad * .5f));
            Vector3 c = new Vector3(spot.X, y, spot.Z);
            Visuals.GroundRing(c - Vector3.up * .03f, radius, new Color(.8f, .92f, 1f, .55f * pulse));
            Visuals.GroundRing(c - Vector3.up * .03f, radius * .62f, new Color(1f, 1f, 1f, .35f * pulse));
        }

        // Every repaint (HUD): the part labels over the points struck.
        public static void DrawLabels()
        {
            if (labels.Count == 0) return;
            GameFont f0 = Text.Font; TextAnchor a0 = Text.Anchor; Color c0 = GUI.color;
            try
            {
                Text.Font = GameFont.Medium; Text.Anchor = TextAnchor.MiddleCenter;
                for (int i = labels.Count - 1; i >= 0; i--)
                {
                    Label l = labels[i];
                    float t = Time.unscaledTime - l.start;
                    if (t > LabelSeconds) { labels.RemoveAt(i); continue; }
                    if (!IffMarkers.Project(l.at + Vector3.up * (.15f + .35f * t), out Vector2 s, out _)) continue;
                    float a = t < .6f ? 1f : 1f - (t - .6f) / (LabelSeconds - .6f);
                    Rect r = new Rect(s.x - 70f, s.y - 14f, 140f, 28f);
                    GUI.color = new Color(0f, 0f, 0f, .8f * a);
                    Widgets.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), l.text);
                    GUI.color = new Color(l.color.r, l.color.g, l.color.b, a);
                    Widgets.Label(r, l.text);
                }
            }
            finally { Text.Font = f0; Text.Anchor = a0; GUI.color = c0; }
        }
    }
}
