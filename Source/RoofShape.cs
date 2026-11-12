using System;
using System.Collections.Generic;
using HarmonyLib;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // The outer surface of the roofs IsoRim draws, so a pawn walking on a roof stands on what is shown (not on a flat
    // plane at wall height inside a pitched roof). Mirrors SectionLayer_IsoRimRoofs / FlatRoofs:
    //   - a cell is roofed in the drawing when IsoRim's own LidKind says 1 (built roof, or a wall under its eaves);
    //   - pitched: each corner sits at wall + 0.03 + 0.55 x (cells to the roof's edge, at most 4), the cell split in two
    //     triangles along its steeper diagonal;
    //   - flat styles: a slab whose top is wall + FlatRoofs.Depth.
    // Without IsoRim's roofs shown, nothing is drawn and the caller keeps its wall-height plane.
    internal static class RoofShape
    {
        private const float Rise = .03f, Pitch = .55f;
        private const int MaxLevel = 4;
        private static Func<Map, IntVec3, int> lidKind;
        private static bool looked;
        private static readonly Dictionary<int, int> levels = new Dictionary<int, int>();
        private static Map levelsMap;
        private static int levelsFrame = -1000;

        private static bool Ready()
        {
            if (looked) return lidKind != null;
            looked = true;
            Type roofLayer=typeof(SectionLayer_IsoRimRoofs);
            var method=AccessTools.Method(roofLayer,"LidKind",new[]{typeof(Map),typeof(IntVec3),typeof(bool)});
            if(method!=null&&method.IsStatic&&method.ReturnType==typeof(int))
            {
                var current=method.CreateDelegate<Func<Map,IntVec3,bool,int>>();
                // Cutaway visibility never removes the physical roof surface.
                lidKind=(map,cell)=>current(map,cell,false);
            }
            else
            {
                method=AccessTools.Method(roofLayer,"LidKind",new[]{typeof(Map),typeof(IntVec3)});
                if(method!=null&&method.IsStatic&&method.ReturnType==typeof(int))
                    lidKind=method.CreateDelegate<Func<Map,IntVec3,int>>();
                else Log.Warning("[FpsBridge] IsoRim roof shape not found: roof tops stay at wall height.");
            }
            return lidKind != null;
        }

        private static bool Shown => View3D.Active && IsoRimMod.Settings.showRoofs;

        // A built roof IsoRim draws over this cell (roof cells and the walls under their eaves).
        public static bool Covers(Map map, IntVec3 c) => Shown && Ready() && c.InBounds(map) && lidKind(map, c) == 1;

        public static void Forget() { levels.Clear(); levelsFrame = -1000; }

        private static int Level(Map map, int x, int z)
        {
            if (map != levelsMap || Time.frameCount - levelsFrame > 30) { levels.Clear(); levelsMap = map; levelsFrame = Time.frameCount; }
            IntVec3 c = new IntVec3(x, 0, z);
            if (!c.InBounds(map)) return 0;
            int key = map.cellIndices.CellToIndex(c);
            if (levels.TryGetValue(key, out int level)) return level;
            level = 0;
            if (lidKind(map, c) == 1)
            {
                level = MaxLevel + 1;
                for (int m = 1; m <= MaxLevel && level > MaxLevel; m++)
                    for (int n = -m; n <= m; n++)
                        if (Kind(map, x + n, z - m) != 1 || Kind(map, x + n, z + m) != 1 || Kind(map, x - m, z + n) != 1 || Kind(map, x + m, z + n) != 1)
                        { level = m; break; }
            }
            levels[key] = level;
            return level;
        }

        private static int Kind(Map map, int x, int z)
        {
            IntVec3 c = new IntVec3(x, 0, z);
            return c.InBounds(map) ? lidKind(map, c) : 0;
        }

        private static float Corner(Map map, int cx, int cz)
        {
            int level = Mathf.Min(Mathf.Min(Mathf.Min(Level(map, cx - 1, cz - 1), Level(map, cx, cz - 1)), Mathf.Min(Level(map, cx - 1, cz), Level(map, cx, cz))), MaxLevel);
            return View3D.WallHeight + Rise + Pitch * level;
        }

        // The roof's top over a point of a covered cell (true cells over the ground).
        public static float HeightAt(Map map, float x, float z)
        {
            if(!Ready())return View3D.WallHeight;
            if (SectionLayer_IsoRimRoofs.Flat) return View3D.WallHeight + FlatRoofs.Depth;
            int ix = Mathf.FloorToInt(x), iz = Mathf.FloorToInt(z);
            float u = Mathf.Clamp01(x - ix), v = Mathf.Clamp01(z - iz);
            // IsoRim's corner order: 0 (x,z), 1 (x,z+1), 2 (x+1,z+1), 3 (x+1,z).
            float h0 = Corner(map, ix, iz), h1 = Corner(map, ix, iz + 1), h2 = Corner(map, ix + 1, iz + 1), h3 = Corner(map, ix + 1, iz);
            if (Mathf.Abs(h0 - h2) >= Mathf.Abs(h1 - h3))
                return v >= u ? h0 + (h2 - h1) * u + (h1 - h0) * v : h0 + (h3 - h0) * u + (h2 - h3) * v;
            return u + v <= 1f ? h0 + (h3 - h0) * u + (h1 - h0) * v : h2 + (h2 - h1) * (u - 1f) + (h2 - h3) * (v - 1f);
        }
    }
}
