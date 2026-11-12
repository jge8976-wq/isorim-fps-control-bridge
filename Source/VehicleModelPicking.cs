using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class VehicleModelPicking
    {
        private sealed class DrawnVehicle
        {
            internal int Frame = -1;
            internal readonly List<(ModelHandle Mesh, System.Numerics.Matrix4x4 World)> Parts = new();
        }

        private static readonly Dictionary<Pawn, DrawnVehicle> Drawn = new();
        private static Map drawMap;
        [ThreadStatic] private static Pawn drawing;
        internal static Pawn Drawing => drawing;
        private static int nextPrune;

        internal static void Install(Harmony harmony)
        {
            MethodInfo draw = AccessTools.DeclaredMethod(VehicleApi.PawnType, "DynamicDrawPhaseAt",
                new[] { typeof(DrawPhase), typeof(Vector3), typeof(bool) }) ?? throw new MissingMethodException("VehiclePawn.DynamicDrawPhaseAt");
            MethodInfo select = AccessTools.DeclaredMethod(typeof(Selector), "SelectableObjectsUnderMouse")
                ?? throw new MissingMethodException("Selector.SelectableObjectsUnderMouse");
            harmony.Patch(draw,
                prefix: new HarmonyMethod(typeof(VehicleModelPicking), nameof(BeginDraw)) { priority = Priority.First + 500 },
                finalizer: new HarmonyMethod(typeof(VehicleModelPicking), nameof(EndDraw)));
            foreach (Type shape in new[] { typeof(Vector3), typeof(Matrix4x4) })
            {
                MethodInfo submit = AccessTools.DeclaredMethod(typeof(Api), nameof(Api.DrawModel),
                    new[] { typeof(ModelHandle), typeof(Vector3), typeof(float), shape, typeof(Color), typeof(string) })
                    ?? throw new MissingMethodException("IsoRim.Api.DrawModel " + shape.Name);
                harmony.Patch(submit, postfix: new HarmonyMethod(typeof(VehicleModelPicking),
                    shape == typeof(Vector3) ? nameof(AfterScale) : nameof(AfterMatrix)));
            }
            harmony.Patch(select, postfix: new HarmonyMethod(typeof(VehicleModelPicking), nameof(AfterSelectable)));
            Log.Message("[FpsBridge] VEHICLE_MODEL_PICK READY");
        }

        public static void BeginDraw(Pawn __instance, DrawPhase __0, out Pawn __state)
        {
            __state = drawing;
            if (__0 != DrawPhase.Draw || !Api.ViewReady || !__instance.Spawned) return;
            if (drawMap != __instance.Map)
            {
                Drawn.Clear();
                drawMap = __instance.Map;
            }
            drawing = __instance;
        }

        public static void EndDraw(Pawn __state) => drawing = __state;

        // These entries come from completed Api.DrawModel calls, including the
        // standalone renderer, rather than an unsubmitted geometry plan.
        internal static List<(ModelHandle Mesh, System.Numerics.Matrix4x4 World)> Submitted(Pawn pawn)
            => Drawn.TryGetValue(pawn, out var entry) && Time.frameCount-entry.Frame<=1
                ? new List<(ModelHandle Mesh, System.Numerics.Matrix4x4 World)>(entry.Parts)
                : new List<(ModelHandle Mesh, System.Numerics.Matrix4x4 World)>();

        internal static void ClearSubmitted(Pawn pawn)
        {
            if (Drawn.TryGetValue(pawn,out var entry)) entry.Parts.Clear();
        }

        public static void AfterScale(ModelHandle __0, Vector3 __1, float __2, Vector3 __3)
        {
            if (drawing != null) Record(__0, __1, __2, Matrix4x4.Scale(__3));
        }

        public static void AfterMatrix(ModelHandle __0, Vector3 __1, float __2, Matrix4x4 __3)
        {
            if (drawing != null) Record(__0, __1, __2, __3);
        }

        private static void Record(ModelHandle model, Vector3 position, float yaw, Matrix4x4 local)
        {
            if (model == null || !Api.FloorShown(position)) return;
            try
            {
                int frame = Time.frameCount;
                if (!Drawn.TryGetValue(drawing, out DrawnVehicle entry)) Drawn[drawing] = entry = new DrawnVehicle();
                if (entry.Frame != frame) { entry.Frame = frame; entry.Parts.Clear(); }
                float squash = View3D.Squash;
                Matrix4x4 raw = Matrix4x4.TRS(position, Quaternion.Euler(0f, yaw, 0f), Vector3.one) * local;
                Matrix4x4 world = Matrix4x4.Scale(new Vector3(1f, squash, 1f))
                    * Api.DrawnMatrix(Matrix4x4.Scale(new Vector3(1f, 1f / squash, 1f)) * raw);
                entry.Parts.Add((model, VehicleMeshContact.Matrix(world)));
                if (frame >= nextPrune)
                {
                    nextPrune = frame + 120;
                    var stale = new List<Pawn>();
                    foreach (var pair in Drawn)
                        if (pair.Key.Destroyed || frame - pair.Value.Frame > 120) stale.Add(pair.Key);
                    foreach (Pawn pawn in stale) Drawn.Remove(pawn);
                }
            }
            catch (Exception e) { Log.ErrorOnce("[FpsBridge] Vehicle model pick registration: " + e, 19271821); }
        }

        // Optic clearance queries the current physical placement of the shared model parts.
        internal static bool TryOwnMeshTop(Pawn pawn, Vector3 point, out float height)
        {
            height = float.NegativeInfinity;
            if (!Api.ModelsOn || pawn == null || !pawn.Spawned || pawn.Map == null) return false;
            var parts = new List<VehicleGeometry.Part>();
            if (!VehicleGeometry.Parts(pawn, VehicleGeometry.Pose(pawn), parts)) return false;

            foreach (var part in parts)
            {
                var world = VehicleMeshContact.Matrix(part.World);
                var bounds = new MeshContactMath.Box(VehicleMeshContact.N3(part.Mesh.Min), VehicleMeshContact.N3(part.Mesh.Max)).Transform(world);
                if (point.x < bounds.Min.X || point.x > bounds.Max.X || point.z < bounds.Min.Z || point.z > bounds.Max.Z) continue;
                var origin = new System.Numerics.Vector3(point.x, bounds.Max.Y + .05f, point.z);
                var mesh = new MeshContactMath.Instance(VehicleMeshContact.Mesh(part.Mesh), world);
                float distance = MeshContactMath.Raycast(mesh, origin, -System.Numerics.Vector3.UnitY, 0f, float.PositiveInfinity);
                if (float.IsFinite(distance)) height = Math.Max(height, origin.Y - distance);
            }
            return float.IsFinite(height);
        }

        internal static bool TryOwnMeshTop(Pawn pawn, Vector3 from, Vector3 to, out float height)
        {
            height = float.NegativeInfinity;
            if (!Api.ModelsOn || pawn == null || !pawn.Spawned || pawn.Map == null) return false;
            Vector3 delta = to - from;
            float length = delta.magnitude;
            if (length <= .0001f) return false;
            var parts = new List<VehicleGeometry.Part>();
            if (!VehicleGeometry.Parts(pawn, VehicleGeometry.Pose(pawn), parts)) return false;
            foreach (var part in parts)
            {
                var mesh = new MeshContactMath.Instance(VehicleMeshContact.Mesh(part.Mesh), VehicleMeshContact.Matrix(part.World));
                float distance = MeshContactMath.Raycast(mesh, VehicleMeshContact.N3(from), VehicleMeshContact.N3(delta / length), 0, length);
                if (distance < length) height = Math.Max(height, mesh.Bounds.Max.Y);
            }
            return float.IsFinite(height);
        }

        public static void AfterSelectable(ref IEnumerable<object> __result)
        {
            if (!Api.ViewReady || !Api.ModelsOn || Drawn.Count == 0 || drawMap != Find.CurrentMap
                || FirstPerson.On || Bridge.Active || VehicleControl.On || RemoteTurret.On) return;
            if (Find.ColonistBar.ColonistOrCorpseAt(UI.MousePositionOnUIInverted) != null) return;
            try
            {
                Pawn picked = Pick();
                if (picked != null) __result = new object[] { picked };
            }
            catch (Exception e) { Log.ErrorOnce("[FpsBridge] Vehicle model picking: " + e, 19271822); }
        }

        internal static void MouseRay(out Vector3 origin, out Vector3 direction)
        {
            Vector2 mouse = Input.mousePosition;
            float x = mouse.x / Screen.width * 2f - 1f, y = mouse.y / Screen.height * 2f - 1f;
            origin = View3D.EyePosition;
            direction = View3D.Forward;
            Vector3 offset = View3D.Right * (x * View3D.Aspect) + View3D.Up * y;
            if (View3D.Perspective) direction += offset * Mathf.Tan(View3D.FieldOfView * .5f * Mathf.Deg2Rad);
            else origin += offset * View3D.OrthoSize;
        }

        internal static Pawn Pick()
        {
            MouseRay(out Vector3 origin, out Vector3 direction);
            var start = VehicleMeshContact.N3(origin);
            var ray = VehicleMeshContact.N3(direction);
            float best = float.PositiveInfinity;
            Pawn selected = null;
            int frame = Time.frameCount;
            foreach (var pair in Drawn)
            {
                Pawn pawn = pair.Key;
                if (frame - pair.Value.Frame > 1 || pawn.Destroyed || !pawn.Spawned || pawn.Map != drawMap
                    || !ThingSelectionUtility.SelectableByMapClick(pawn) || pawn.Position.Fogged(drawMap)) continue;
                foreach (var part in pair.Value.Parts)
                {
                    float near = View3D.NearCard * .5f;
                    var bounds = new MeshContactMath.Box(VehicleMeshContact.N3(part.Mesh.Min), VehicleMeshContact.N3(part.Mesh.Max));
                    if (!MeshContactMath.RayBox(start, ray, bounds.Transform(part.World), near, best)) continue;
                    var instance = new MeshContactMath.Instance(VehicleMeshContact.Mesh(part.Mesh), part.World);
                    float distance = MeshContactMath.Raycast(instance, start, ray, near, best);
                    if (distance >= best) continue;
                    best = distance;
                    selected = pawn;
                }
            }
            return selected;
        }
    }
}
