using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class MinimapOrientation
    {
        private static readonly string[] directions = { "N", "E", "S", "W" };

        // IsoRim sets worldToCameraMatrix directly, so Camera.transform.rotation can stay north-facing.
        internal static float CameraYaw => View3D.Active ? Mathf.Repeat(FirstPersonTransition.RenderYaw(), 360f) : 0f;

        internal static float Fit(Vector2 viewport, Vector2 map, float yaw)
        {
            if (map.x <= 0f || map.y <= 0f || viewport.x <= 0f || viewport.y <= 0f) return 1f;
            float baseScale = Mathf.Min(viewport.x / map.x, viewport.y / map.y);
            Vector2 baseSize = map * baseScale;
            float radians = yaw * Mathf.Deg2Rad;
            float sin = Mathf.Abs(Mathf.Sin(radians)), cos = Mathf.Abs(Mathf.Cos(radians));
            return Mathf.Min(1f, Mathf.Min(viewport.x / (baseSize.x * cos + baseSize.y * sin),
                viewport.y / (baseSize.x * sin + baseSize.y * cos)));
        }

        internal static Vector2 UnrotatePointer(Vector2 pointer, Rect viewport, Vector2 map, float yaw)
        {
            Vector2 fromCenter = (pointer - viewport.center) / Fit(viewport.size, map, yaw);
            float radians = yaw * Mathf.Deg2Rad;
            float sin = Mathf.Sin(radians), cos = Mathf.Cos(radians);
            return viewport.center + new Vector2(cos * fromCenter.x - sin * fromCenter.y, sin * fromCenter.x + cos * fromCenter.y);
        }

        internal static Matrix4x4 BeginMapTransform(Vector2 pivot, float yaw, float scale)
        {
            Matrix4x4 saved = GUI.matrix;
            if (Mathf.Abs(Mathf.DeltaAngle(0f, yaw)) < .01f && Mathf.Abs(scale - 1f) < .0001f) return saved;
            // Waylith transforms the group offset together with the draw coordinates.
            // Resolve the pivot with the untransformed group offset before rotating the whole layer.
            GUI.matrix = Matrix4x4.identity;
            try
            {
                Vector3 center = saved.MultiplyPoint3x4(GUIUtility.GUIToScreenPoint(pivot));
                GUI.matrix = Matrix4x4.TRS(center, Quaternion.Euler(0f, 0f, -yaw), new Vector3(scale, scale, 1f))
                    * Matrix4x4.Translate(-center) * saved;
                return saved;
            }
            catch
            {
                GUI.matrix = saved;
                throw;
            }
        }

        internal static void DrawCompass(Rect viewport, float yaw)
        {
            if (viewport.width < 64f || viewport.height < 64f) return;
            var center = new Vector2(viewport.xMax - 24f, viewport.yMin + 24f);
            Color previousColor = GUI.color;
            GameFont previousFont = Text.Font;
            TextAnchor previousAnchor = Text.Anchor;
            try
            {
                GUI.color = Color.white;
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.DrawBoxSolid(new Rect(center.x - 22f, center.y - 22f, 44f, 44f), new Color(.02f, .035f, .05f, .85f));
                for (int i = 0; i < directions.Length; i++)
                {
                    var (x, y) = MinimapRules.Heading(i * 90f - yaw);
                    Vector2 at = center + new Vector2(x, y) * 13f;
                    GUI.color = i == 0 ? new Color(.45f, 1f, .94f) : new Color(.9f, .92f, .95f);
                    Widgets.Label(new Rect(at.x - 7f, at.y - 7f, 14f, 14f), directions[i]);
                }
            }
            finally
            {
                GUI.color = previousColor;
                Text.Font = previousFont;
                Text.Anchor = previousAnchor;
            }
        }
    }
}
