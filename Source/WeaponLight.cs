using System;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Four small, overlapping native glow sources form a forward beam. Native flood lighting reaches both
    // terrain and IsoRim's glow texture; handles are transient, with no spawned things or save data.
    internal static class WeaponLight
    {
        private static readonly float[] distances = { 2f, 6f, 11f, 18f };
        private static readonly int[] handles = new int[4];
        private static readonly IntVec3[] cells = new IntVec3[4];
        private static readonly float[] radii = new float[4];
        private static Map map;
        private static float nextUpdate;

        internal static void Frame()
        {
            try
            {
                Pawn pawn = Bridge.ShootingPawn;
                Thing source = TurretControl.Manned ? TurretControl.Current?.Thing : pawn;
                bool equipped = TurretControl.Manned || pawn?.equipment?.Primary?.def.IsWeapon == true
                    || pawn != null && MechControl.IsMech(pawn);
                if (!View3D.Active || !equipped || source?.Spawned != true || source.Map != Find.CurrentMap
                    || Artillery.MapView || source.Map.skyManager.CurSkyGlow > .35f)
                { Release(); return; }
                if (map != source.Map) { Release(); map = source.Map; }
                if (Time.unscaledTime < nextUpdate) return;
                nextUpdate = Time.unscaledTime + .15f;
                Vector3 eye = TurretControl.Manned ? TurretControl.Eye() : FirstPerson.Eye;
                V3 start = new V3(eye.x, eye.y, eye.z);
                V3 direction = HitMath.Direction(FirstPerson.Yaw, FirstPerson.Pitch);
                bool stopped = false;
                for (int i = 0; i < distances.Length; i++)
                {
                    if (stopped) { Remove(i); continue; }
                    V3 end = start + direction * distances[i];
                    bool blocked = Ballistics.SightContact(source, start, end, out V3 contact, false);
                    if (blocked) end = contact - direction * .12f;
                    float distance = (end - start).Length;
                    IntVec3 cell = FloorSpace.Cell(map, end);
                    // A light on another storey would leak through its floor in the game's 2D glow flood.
                    if (distance < .2f || !cell.InBounds(map) || cell.Fogged(map)
                        || FloorSpace.Band(map, cell).Level != FloorSpace.Band(map, source.Position).Level)
                    { Remove(i); stopped = true; continue; }
                    float radius = Mathf.Clamp(1.5f + distance * .18f, 1.5f, 5f);
                    if (handles[i] == 0 || cells[i] != cell || Mathf.Abs(radii[i] - radius) > .3f)
                    {
                        Remove(i);
                        // White with a little warmth; no overlight radius (does not count as sunlight).
                        handles[i] = map.glowGrid.RegisterCustomGlower(cell, radius, new ColorInt(235, 229, 208, 0));
                        cells[i] = cell; radii[i] = radius;
                    }
                    stopped = blocked;
                }
            }
            catch (Exception e) { Release(); Log.ErrorOnce("[FpsBridge] Weapon flashlight stopped: " + e, 19271730); }
        }

        private static void Remove(int index)
        {
            if (handles[index] == 0) return;
            map?.glowGrid.DeregisterCustomGlower(handles[index]);
            handles[index] = 0;
        }

        internal static void Release()
        {
            for (int i = 0; i < handles.Length; i++) Remove(i);
            map = null; nextUpdate = 0f;
        }
    }
}
