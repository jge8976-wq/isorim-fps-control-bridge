using System;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // A read-only HUD, using the game's terrain/fog/edifice grids. Terrain is cached and rebuilt in small slices;
    // the pawn and continuous heading are drawn every frame, independently of terrain texture uploads.
    internal static class PawnMinimap
    {
        private static Map map;
        private static Texture2D texture;
        private static Color32[] pixels;
        private static int row, lastFrame = -1;
        private static bool ready;
        private static float nextRefresh;
        private static int floorShift, floorRows, floorLevel;

        public static void Reset()
        {
            if (texture != null) UnityEngine.Object.Destroy(texture);
            texture = null; pixels = null; map = null; row = 0; ready = false; nextRefresh = 0f;
        }

        private static void Refresh(Map current, FloorBand band)
        {
            if (map != current || texture == null || floorShift != band.Shift || floorRows != band.Rows)
            {
                Reset(); map = current;
                floorShift = band.Shift; floorRows = band.Rows; floorLevel = band.Level;
                texture = new Texture2D(Math.Min(512, map.Size.x), Math.Min(512, floorRows), TextureFormat.RGBA32, false);
                texture.filterMode = FilterMode.Point;
                pixels = new Color32[texture.width * texture.height];
            }
            if (lastFrame == Time.frameCount || Time.unscaledTime < nextRefresh) return;
            lastFrame = Time.frameCount;
            int stop = Math.Min(texture.height, row + 8);
            for (; row < stop; row++) for (int x = 0; x < texture.width; x++)
            {
                int gx = Math.Min(map.Size.x - 1, (int)((x + .5f) * map.Size.x / texture.width));
                int gz = floorShift + Math.Min(floorRows - 1, (int)((row + .5f) * floorRows / texture.height));
                var c = new IntVec3(gx, 0, gz);
                Color color;
                if (map.fogGrid.IsFogged(c)) color = new Color(.035f, .04f, .055f);
                else if (c.GetEdifice(map) is Building building)
                    color = building.def.Fillage == FillCategory.Full ? new Color(.7f, .72f, .75f) : new Color(.48f, .43f, .34f);
                else
                {
                    TerrainDef terrain = map.terrainGrid.TerrainAt(c);
                    color = terrain?.IsWater == true ? new Color(.15f, .36f, .55f)
                        : Color.Lerp(new Color(.28f, .32f, .23f), terrain?.DrawColor ?? Color.gray, .25f);
                }
                pixels[row * texture.width + x] = color;
            }
            if (row == texture.height)
            { texture.SetPixels32(pixels); texture.Apply(false, false); ready = true; row = 0; nextRefresh = Time.unscaledTime + 1f; }
        }

        public static void Draw(Pawn pawn)
        {
            if (!FpsMod.Settings.showMinimap) { if (texture != null) Reset(); return; }
            if (pawn?.Map == null || UI.screenWidth < 600 || UI.screenHeight < 400) return;
            GameFont font = Text.Font; TextAnchor anchor = Text.Anchor; Color color = GUI.color;
            try
            {
                Refresh(pawn.Map, FloorSpace.Band(pawn.Map, pawn.Position));
                float size = Mathf.Clamp(FpsMod.Settings.minimapSize, 160f, 300f);
                float scale = size / Math.Max(map.Size.x, floorRows);
                float w = map.Size.x * scale, h = floorRows * scale;
                Rect panel = new Rect(UI.screenWidth - w - 46f, 90f, w + 12f, h + 51f);
                Rect area = new Rect(panel.x + 6f, panel.y + 24f, w, h);
                GUI.color = Color.white; Text.Font = GameFont.Tiny; Text.Anchor = TextAnchor.UpperLeft;
                Widgets.DrawBoxSolid(panel, new Color(.025f, .035f, .05f, .92f));
                float yaw = MinimapOrientation.CameraYaw;
                Widgets.Label(new Rect(panel.x + 7f, panel.y + 3f, w, 20f), L.T("楼层 ", "Floor ") + (floorLevel + 1) + " · " + ControlCamera.Label);
                DrawMap(pawn, area, yaw);
                MinimapOrientation.DrawCompass(area, yaw);
                Widgets.Label(new Rect(panel.x + 7f, area.yMax + 3f, w, 20f), "X " + pawn.Position.x + "  Z " + (pawn.Position.z - floorShift) + "  ·  " + yaw.ToString("0") + "°");
            }
            catch (Exception e) { Log.ErrorOnce("[FpsBridge] Minimap unavailable (control continues): " + e, 19271710); }
            finally { Text.Font = font; Text.Anchor = anchor; GUI.color = color; }
        }

        private static void DrawMap(Pawn pawn, Rect area, float yaw)
        {
            Rect local = new Rect(Vector2.zero, area.size);
            GUI.BeginClip(area);
            Matrix4x4 saved = GUI.matrix;
            try
            {
                if (!ready) Widgets.Label(local, L.T("正在绘制地图…", "Drawing the map…"));
                MinimapOrientation.BeginMapTransform(local.center, yaw, MinimapOrientation.Fit(local.size, local.size, yaw));
                if (ready) GUI.DrawTexture(local, texture);
                if (VehicleControl.Owns(pawn)) VehiclePrediction.DrawMinimap(local, false);
                // DrawPos can include an IsoRim floor display offset. The minimap uses the current floor's logical grid.
                V3 pos = FloorSpace.Spot(pawn);
                var p = MinimapRules.Position(pos.X, pos.Z, map.Size.x, floorRows);
                var d = MinimapRules.Heading(yaw);
                Vector2 center = new Vector2(p.x * local.width, p.y * local.height);
                Vector2 forward = new Vector2(d.x, d.y), side = new Vector2(-d.y, d.x);
                Vector2 tip = center + forward * 10f, back = center - forward * 5f;
                Color cyan = new Color(.3f, 1f, .95f);
                Widgets.DrawLine(back - side * 5f, tip, cyan, 2.5f);
                Widgets.DrawLine(tip, back + side * 5f, cyan, 2.5f);
                Widgets.DrawLine(back + side * 5f, back - side * 5f, cyan, 2.5f);
                Widgets.DrawBoxSolid(new Rect(center.x - 2, center.y - 2, 4, 4), Color.white);
            }
            finally
            {
                GUI.matrix = saved;
                GUI.EndClip();
            }
        }
    }
}
