using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class VehicleCrewDisplay
    {
        private sealed class SeatCells
        {
            internal readonly IntVec2[] Cells;
            public SeatCells(object hitbox) => Cells = VehicleApi.List(hitbox, "Hitbox").OfType<IntVec2>().Distinct().ToArray();
        }

        private static readonly ConditionalWeakTable<object, SeatCells> nativeCells = new();

        internal static IEnumerable<VehicleCrewVolume> Volumes(Pawn vehicle)
        {
            var explicitVolumes = VehicleBallisticDamage.ExplicitCrewProfile(vehicle)?.volumes;
            var handlers = VehicleApi.Handlers(vehicle);
            var grid = (CellRect)VehicleApi.Get(vehicle, "Hitbox");
            float height = Mathf.Max(.5f, VehicleModels.Profile(vehicle).hullHeight);
            for (int index = 0; index < handlers.Count; index++)
            {
                object role = VehicleApi.Role(handlers[index]);
                if (role == null) continue;
                int slots = VehicleApi.Int(role, "Slots");
                if (slots <= 0) continue;
                string key = VehicleApi.RoleKey(handlers[index]);
                bool configured = false;
                if (explicitVolumes != null)
                {
                    foreach (var volume in explicitVolumes)
                    {
                        if (volume.roleKey != key) continue;
                        configured = true;
                        yield return volume;
                    }
                }
                if (!configured) yield return Estimate(role, key, slots, index, grid, height);
            }
        }

        private static VehicleCrewVolume Estimate(object role, string key, int slots, int index, CellRect grid, float height)
        {
            object hitbox = VehicleApi.Get(role, "Hitbox");
            var cells = hitbox == null ? Array.Empty<IntVec2>() : nativeCells.GetValue(hitbox, value => new SeatCells(value)).Cells;
            float width = Mathf.Max(1f, grid.Width), length = Mathf.Max(1f, grid.Height);
            bool exposed = VehicleApi.Bool(role, "Exposed");
            float x = (grid.minX + grid.maxX) * .5f;
            float z = (grid.minZ + grid.maxZ) * .5f;
            float xSize = Mathf.Min(width * .8f, .65f + (Math.Min(slots, 6) - 1) * .24f);
            float zSize = Mathf.Min(length * .8f, .72f + (Math.Min(slots, 8) - 1) * .18f);
            if (cells.Length > 0 && cells.Length < width * length)
            {
                int minX = cells.Min(cell => cell.x), maxX = cells.Max(cell => cell.x);
                int minZ = cells.Min(cell => cell.z), maxZ = cells.Max(cell => cell.z);
                x = Mathf.Clamp((minX + maxX) * .5f, grid.minX, grid.maxX);
                z = Mathf.Clamp((minZ + maxZ) * .5f, grid.minZ, grid.maxZ);
                xSize = Mathf.Clamp(maxX - minX + .72f, .5f, width);
                zSize = Mathf.Clamp(maxZ - minZ + .72f, .5f, length);
            }
            else
            {
                int handling = VehicleApi.Int(role, "HandlingTypes");
                z += Mathf.Max(0, length - 1f) * ((handling & 1) != 0 ? .28f : handling != 0 ? .03f : -.24f);
                float side = index == 0 ? 0f : index % 2 == 0 ? .16f : -.16f;
                x += Mathf.Max(0, width - 1f) * side;
            }
            return new VehicleCrewVolume
            {
                roleKey = key,
                center = new Vector3(x, height * (exposed ? .92f : .53f), z),
                size = new Vector3(xSize, Mathf.Clamp(height * .48f, .36f, .78f), zSize),
            };
        }
    }
}

