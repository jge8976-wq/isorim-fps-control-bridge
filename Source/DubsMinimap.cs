using System;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // 0.8.0: with Dubs Mint Minimap installed, its minimap is the one shown while possessing (opened for the session
    // and put back as the player had it afterwards, without writing their settings); the bridge's own minimap is then
    // not drawn. The Dubs map follows the game camera, which first person keeps on the pawn.
    internal static class DubsMinimap
    {
        private static bool bound;
        private static FieldInfo settingsField, openField;
        private static bool? restore;
        private static bool viewBound;
        private static PropertyInfo projectZViewState;
        private static Func<Map, IntVec3> projectZViewSize;
        private static Func<Thing, Vector3> projectZViewPosition;

        private static void BindViewProjection()
        {
            if (viewBound) return;
            viewBound = true;
            Type adapter = AccessTools.TypeByName("ProjectZ.Compat.MintMinimap");
            if (adapter == null) return;
            projectZViewState = AccessTools.Property(adapter, "State");
            MethodInfo size = AccessTools.Method(adapter, "ViewSize", new[] { typeof(Map) });
            MethodInfo position = AccessTools.Method(adapter, "ViewPos", new[] { typeof(Thing) });
            if (projectZViewState == null || size?.ReturnType != typeof(IntVec3) || position?.ReturnType != typeof(Vector3)) return;
            projectZViewSize = size.CreateDelegate<Func<Map, IntVec3>>();
            projectZViewPosition = position.CreateDelegate<Func<Thing, Vector3>>();
        }

        internal static bool UsesProjectZView
        {
            get
            {
                BindViewProjection();
                return projectZViewSize != null && projectZViewPosition != null
                    && projectZViewState.GetValue(null)?.ToString() == "Active";
            }
        }

        // Dubs' own DrawAllPawns is transpiled by ProjectZ to these view APIs.
        // Its zoomed MapRect is already the active GUI group, so callers pass a
        // local rectangle and must not apply MapRect's pan offset a second time.
        internal static IntVec3 ViewSize(Map map) => UsesProjectZView ? projectZViewSize(map) : map.Size;

        internal static bool TryMinimapPosition(Map map, V3 world, IntVec3 nativeCell, Rect area, out Vector2 center)
        {
            center = default;
            if (map == null || !world.Finite || !nativeCell.InBounds(map)) return false;
            bool folded = UsesProjectZView;
            IntVec3 size = folded ? projectZViewSize(map) : map.Size;
            float z = folded ? world.Z : FloorSpace.Logical(map, world, nativeCell).z;
            var point = MinimapRules.PixelPosition(world.X, z, size.x, size.z, area.x, area.y, area.width, area.height);
            center = new Vector2(point.x, point.y);
            return float.IsFinite(center.x) && float.IsFinite(center.y);
        }

        internal static bool TryPawnPosition(Thing thing, Rect area, out Vector2 center)
        {
            center = default;
            if (thing?.Map == null || !thing.Spawned) return false;
            bool folded = UsesProjectZView;
            IntVec3 size = folded ? projectZViewSize(thing.Map) : thing.Map.Size;
            Vector3 at = folded ? projectZViewPosition(thing) : thing.DrawPos;
            var point = MinimapRules.PixelPosition(at.x, at.z, size.x, size.z, area.x, area.y, area.width, area.height);
            center = new Vector2(point.x, point.y);
            return float.IsFinite(center.x) && float.IsFinite(center.y);
        }

        private static object Settings
        {
            get
            {
                if (!bound)
                {
                    bound = true;
                    Type mod = AccessTools.TypeByName("DubsMintMinimap.DubsMintMinimapMod");
                    settingsField = mod == null ? null : AccessTools.Field(mod, "Settings");
                    openField = settingsField == null ? null : AccessTools.Field(settingsField.FieldType, "MinimapOpen");
                    if (openField != null && openField.FieldType != typeof(bool)) openField = null;
                    Log.Message("[FpsBridge] DUBS_MINIMAP " + (openField != null ? "LINKED" : "absent (own minimap)"));
                }
                return openField == null ? null : settingsField.GetValue(null);
            }
        }

        public static bool Active => Settings != null;

        // ---- facing arrow: over the possessed pawn's own marker, an arrow pointing where the view looks.
        private static bool patched;

        private static void Patch()
        {
            if (patched) return;
            patched = true;
            try
            {
                Type window = AccessTools.TypeByName("DubsMintMinimap.MainTabWindow_MiniMap");
                MethodInfo pawns = window == null ? null : AccessTools.Method(window, "DrawAllPawns", new[] { typeof(Rect) });
                MethodInfo marker = window == null ? null : AccessTools.Method(window, "DrawMarker");
                if (pawns == null || marker == null) { Log.Warning("[FpsBridge] Dubs minimap DrawAllPawns/DrawMarker not found: no facing arrow."); return; }
                var harmony = new Harmony(Bootstrap.Id + ".dubs");
                harmony.Patch(marker, prefix: new HarmonyMethod(typeof(DubsMinimap), nameof(BeforeDrawMarker)), postfix: new HarmonyMethod(typeof(DubsMinimap), nameof(AfterDrawMarker)));
                harmony.Patch(pawns, postfix: new HarmonyMethod(typeof(DubsMinimap), nameof(AfterDrawPawns)));
                Log.Message("[FpsBridge] DUBS_MINIMAP facing arrow ON");
            }
            catch (Exception e) { Log.Warning("[FpsBridge] Dubs minimap facing arrow off: " + e.Message); }
        }

        // The arrow goes exactly where Dubs has just drawn the possessed pawn's own marker (its centre c).
        private static int markedFrame = -1;
        // Dubs' silhouette marker multiplies icon size by vehicle drawSize. Bound just vehicle markers.
        public static bool BeforeDrawMarker(Vector2 c, Color col, Pawn pawn)
        {
            if (!VehicleApi.IsVehicle(pawn)) return true;
            if (Event.current?.type != EventType.Repaint) return false;
            Color was = GUI.color;
            try
            {
                GUI.color = col;
                GUI.DrawTexture(new Rect(c.x - 5f, c.y - 5f, 10f, 10f), Arrow(VehicleApi.Yaw(pawn)));
            }
            finally { GUI.color = was; }
            return false;
        }
        public static void AfterDrawMarker(Vector2 c, Pawn pawn)
        {
            try
            {
                Pawn own = VehicleControl.On ? VehicleControl.Vehicle : Bridge.ShootingPawn;
                if (own == null || pawn != own || Event.current.type != EventType.Repaint) return;
                markedFrame = Time.frameCount;
                DrawArrow(c, own);
            }
            catch (Exception e) { Log.ErrorOnce("[FpsBridge] Dubs facing arrow failed: " + e.Message, 19271721); }
        }

        // No marker drawn for the pawn this frame (Dubs hides colonists, or a downed icon): placed as Dubs places markers.
        public static void AfterDrawPawns(Rect radarRect)
        {
            try
            {
                if (Event.current?.type != EventType.Repaint) return;
                // Dubs is already inside the clipped, zoomed MapRect group here.
                VehiclePrediction.DrawMinimap(new Rect(Vector2.zero, radarRect.size), true);
                Pawn pawn = VehicleControl.On ? VehicleControl.Vehicle : Bridge.ShootingPawn;
                if (pawn == null || !pawn.Spawned || pawn.Map != Find.CurrentMap || Event.current.type != EventType.Repaint || markedFrame == Time.frameCount) return;
                if (TryPawnPosition(pawn, new Rect(Vector2.zero, radarRect.size), out Vector2 center)) DrawArrow(center, pawn);
            }
            catch (Exception e) { Log.ErrorOnce("[FpsBridge] Dubs facing arrow failed: " + e.Message, 19271721); }
        }

        // Pre-rotated: a GUI-matrix rotation inside Dubs' clipped group lands far from the pivot.
        private static void DrawArrow(Vector2 c, Pawn pawn)
        {
            float yaw = VehicleControl.Owns(pawn) ? FirstPerson.Yaw : FirstPerson.On && FirstPerson.Pawn == pawn ? FirstPerson.Yaw : pawn.Rotation.AsAngle;
            Texture2D tex = Arrow(yaw);
            float size = VehicleApi.IsVehicle(pawn) ? 12f : 24f * .7f;
            Color was = GUI.color;
            GUI.color = new Color(1f, .9f, .2f, 1f);
            GUI.DrawTexture(new Rect(c.x - size * .5f, c.y - size * .5f, size, size), tex);
            GUI.color = was;
        }

        // A 24-pixel arrow pointing at yaw (north up, clockwise), in 10-degree steps: white with a dark rim (tinted when drawn).
        private static readonly Texture2D[] arrows = new Texture2D[36];
        private static Texture2D Arrow(float yaw)
        {
            int k = ((Mathf.RoundToInt(yaw / 10f) % 36) + 36) % 36;
            if (arrows[k] != null) return arrows[k];
            const int n = 24;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            float s = Mathf.Sin(k * 10f * Mathf.Deg2Rad), co = Mathf.Cos(k * 10f * Mathf.Deg2Rad);
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    // Texture rows go up. Turn the pixel back by yaw about the centre and test the upright arrow there.
                    float ou = (x + .5f) / n - .5f, ow = (y + .5f) / n - .5f;
                    float u = ou * co - ow * s, v = ou * s + ow * co + .5f;
                    // Upright: a triangle with its tip at the top (v = 1) and a notch in the base.
                    float half = (1f - v) * .45f;
                    bool inside = v > .08f && v < .96f && Mathf.Abs(u) < half && !(v < .35f && Mathf.Abs(u) < (.35f - v) * .9f);
                    float halfIn = half - .07f;
                    bool core = inside && v > .14f && v < .9f && Mathf.Abs(u) < halfIn && !(v < .41f && Mathf.Abs(u) < (.41f - v) * .9f + .02f);
                    px[y * n + x] = core ? new Color32(255, 255, 255, 255) : inside ? new Color32(30, 25, 10, 255) : new Color32(0, 0, 0, 0);
                }
            tex.SetPixels32(px);
            tex.Apply();
            return arrows[k] = tex;
        }

        public static void Begin()
        {
            try
            {
                object s = Settings;
                if (s == null) return;
                Patch();
                if (restore == null) restore = (bool)openField.GetValue(s);
                openField.SetValue(s, true);
            }
            catch (Exception e) { Log.WarningOnce("[FpsBridge] Dubs minimap link failed: " + e.Message, 19271720); openField = null; }
        }

        public static void End()
        {
            try
            {
                object s = Settings;
                if (s != null && restore.HasValue) openField.SetValue(s, restore.Value);
            }
            catch { }
            restore = null;
        }
    }
}
