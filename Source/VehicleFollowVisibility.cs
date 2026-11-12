using System;
using System.Linq;
using HarmonyLib;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class VehicleFollowVisibility
    {
        internal struct DrawState { internal bool Active, ThroughRoof; }
        [ThreadStatic] private static int drawing;
        internal static int ProtectedDraws;
        private static int lastDrawFrame = -1;
        private static readonly System.Collections.Generic.Dictionary<int, MaterialPropertyBlock> blocks = new();
        internal static bool Protect(Pawn pawn) => VehicleControl.Owns(pawn) && VehicleControl.ExternalFollow && pawn.Spawned;
        internal static float Ceiling(Pawn vehicle)
        {
            var at = VehicleGeometry.Pose(vehicle).Position;
            float bottom = at.Y + (AerialFlight.Supports(vehicle) ? AerialGeometry.LocalBounds(vehicle).min.y : 0);
            float nearest = float.PositiveInfinity;
            var bands = FloorSpace.Bands(vehicle.Map);
            foreach (var band in bands)
            {
                if (bottom < band.Height - .05f) continue;
                var cell = new IntVec3(Mathf.FloorToInt(at.X), 0, Mathf.FloorToInt(at.Z) + band.Shift);
                if (!cell.InBounds(vehicle.Map)) continue;
                float height = band.Height + Api.WallHeight - .012f;
                bool solid = vehicle.Map.roofGrid.RoofAt(cell) != null;
                foreach (var upper in bands)
                    if (upper.Level == band.Level + 1 && FloorSpace.HasFloorAt(vehicle.Map,
                        new IntVec3(cell.x, 0, Mathf.FloorToInt(at.Z) + upper.Shift)))
                    { solid = true; height = upper.Height - .12f; break; }
                if (solid && bottom < height - .05f) nearest = Math.Min(nearest, height);
            }
            return nearest;
        }

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Cull), "ThingMayShow"), prefix: new HarmonyMethod(typeof(VehicleFollowVisibility), nameof(ThingVisible)));
            harmony.Patch(AccessTools.Method(typeof(Cull), "MeshVisible"), prefix: new HarmonyMethod(typeof(VehicleFollowVisibility), nameof(MeshVisible)));
            harmony.Patch(AccessTools.Method(typeof(DynamicDrawManager), "DrawDynamicThings"), postfix: new HarmonyMethod(typeof(VehicleFollowVisibility), nameof(EnsureControlledDraw)));
            harmony.Patch(AccessTools.Method(typeof(RoofClip), "FloorBlock"), prefix: new HarmonyMethod(typeof(VehicleFollowVisibility), nameof(RenderBlock)));
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("IsoRim.PartBatches"), "Add"), prefix: new HarmonyMethod(typeof(VehicleFollowVisibility), nameof(Batch)));
            foreach (var method in typeof(Api).GetMethods().Where(m => m.Name == "DrawModel"))
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(VehicleFollowVisibility), nameof(BeforeModel)),
                    finalizer: new HarmonyMethod(typeof(VehicleFollowVisibility), nameof(AfterDraw)));
            harmony.Patch(AccessTools.Method(VehicleApi.PawnType, "DynamicDrawPhaseAt", new[] { typeof(DrawPhase), typeof(Vector3), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(VehicleFollowVisibility), nameof(BeforeDraw)) { priority = Priority.First + 1000 },
                finalizer: new HarmonyMethod(typeof(VehicleFollowVisibility), nameof(AfterDraw)));
        }

        public static bool ThingVisible(Thing t, ref bool __result)
        { if (t is not Pawn pawn || !Protect(pawn)) return true; __result = true; return false; }
        public static bool MeshVisible(ref bool __result)
        { if (drawing == 0) return true; __result = true; return false; }
        public static bool Batch(ref bool __result)
        { if (drawing == 0) return true; __result = false; return false; }
        public static bool RenderBlock(int floor, ref MaterialPropertyBlock __result)
        {
            if (drawing == 0) return true;
            if (!blocks.TryGetValue(floor, out var block)) blocks[floor] = block = new MaterialPropertyBlock();
            block.SetFloat(Shader.PropertyToID("_IsoOwnerFloor"), Floors.On ? floor + 1 : 0);
            Vector4 globals = RoofClip.Globals;
            // A flying model can be above its native storage floor's slab.
            // Keep fog and ordinary depth testing, but don't slice this free
            // moving follow target as though it were a ground-anchored prop.
            globals.x = ((int)globals.x) & 2;
            block.SetVector(Shader.PropertyToID("_IsoRoof"), globals);
            __result = block; return false;
        }
        public static void BeforeDraw(Pawn __instance, DrawPhase phase, out DrawState __state)
        {
            __state = default;
            if (phase != DrawPhase.Draw || !Protect(__instance)) return;
            ProtectedDraws++; lastDrawFrame = Time.frameCount;
        }
        internal static DrawState BeginModels(Pawn pawn)
        {
            if (pawn == null || !Protect(pawn)) return default;
            var state = new DrawState { Active = true, ThroughRoof = DynamicModels.ThroughRoof };
            drawing++; DynamicModels.ThroughRoof = true; return state;
        }
        public static void BeforeModel(out DrawState __state) => __state = BeginModels(VehicleModelPicking.Drawing);
        public static Exception AfterDraw(Exception __exception, DrawState __state)
        {
            if (__state.Active) { drawing--; DynamicModels.ThroughRoof = __state.ThroughRoof; }
            return __exception;
        }
        public static void EnsureControlledDraw()
        {
            var vehicle = VehicleControl.Vehicle;
            if (lastDrawFrame == Time.frameCount || vehicle == null || !Protect(vehicle) || vehicle.Map != Find.CurrentMap || !Api.ViewReady) return;
            // A close follow camera can put the native 2D coarse draw rectangle
            // in front of the very vehicle it follows, especially at a roof.
            // Submit the real renderer once when that coarse pass omitted it.
            vehicle.DynamicDrawPhaseAt(DrawPhase.EnsureInitialized, vehicle.DrawPos);
            vehicle.DynamicDrawPhaseAt(DrawPhase.ParallelPreDraw, vehicle.DrawPos);
            vehicle.DynamicDrawPhaseAt(DrawPhase.Draw, vehicle.DrawPos);
        }
    }
}
