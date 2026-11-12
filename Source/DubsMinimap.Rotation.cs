using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static partial class DubsMinimap
    {
        private static Rect mapViewport, previousMapRect;
        private static float mapYaw;
        private static bool rotatingMap;
        private static Matrix4x4 mapMatrix;

        internal static void InstallRotation()
        {
            var window = AccessTools.TypeByName("DubsMintMinimap.MainTabWindow_MiniMap");
            if (window == null) return;
            var harmony = new Harmony(Bootstrap.Id + ".dubs.rotation");
            try
            {
                var draw = AccessTools.Method(window, "Minimap", new[] { typeof(Rect) })
                    ?? throw new MissingMethodException(window.FullName, "Minimap");
                harmony.Patch(draw, prefix: new HarmonyMethod(typeof(DubsMinimap), nameof(BeforeMinimap)),
                    postfix: new HarmonyMethod(typeof(DubsMinimap), nameof(AfterMinimap)),
                    transpiler: new HarmonyMethod(typeof(DubsMinimap), nameof(RotateMinimap)) { priority = Priority.Last });
            }
            catch (Exception e)
            {
                harmony.UnpatchAll(harmony.Id);
                Log.Warning("[FpsBridge] Dubs minimap rotation unavailable: " + e.Message);
            }
        }

        private static void BeforeMinimap(Rect ___MapRect)
        {
            previousMapRect = ___MapRect;
            rotatingMap = false;
        }

        private static Rect ObserveViewport(Rect viewport)
        {
            mapViewport = viewport;
            rotatingMap = View3D.Active;
            mapYaw = MinimapOrientation.CameraYaw;
            return viewport;
        }

        private static Vector2 MapPointer(Event input) => rotatingMap
            ? MinimapOrientation.UnrotatePointer(input.mousePosition, mapViewport, previousMapRect.size, mapYaw)
            : input.mousePosition;

        private static void BeginMapGroup(Rect mapRect)
        {
            mapMatrix = GUI.matrix;
            if (rotatingMap)
                MinimapOrientation.BeginMapTransform(mapViewport.size * .5f, mapYaw,
                    MinimapOrientation.Fit(mapViewport.size, mapRect.size, mapYaw));
            try { GUI.BeginGroup(mapRect); }
            catch { GUI.matrix = mapMatrix; throw; }
        }

        private static void EndMapGroup()
        {
            try { GUI.EndGroup(); }
            finally { GUI.matrix = mapMatrix; }
        }

        private static void AfterMinimap()
        {
            if (rotatingMap && Event.current?.type == EventType.Repaint)
                MinimapOrientation.DrawCompass(mapViewport, mapYaw);
            rotatingMap = false;
        }

        private static IEnumerable<CodeInstruction> RotateMinimap(IEnumerable<CodeInstruction> instructions)
        {
            var viewport = AccessTools.Method(typeof(GenUI), nameof(GenUI.ContractedBy), new[] { typeof(Rect), typeof(float) });
            var pointer = AccessTools.PropertyGetter(typeof(Event), nameof(Event.mousePosition));
            var begin = AccessTools.Method(typeof(GUI), nameof(GUI.BeginGroup), new[] { typeof(Rect) });
            var end = AccessTools.Method(typeof(GUI), nameof(GUI.EndGroup));
            int viewports = 0, pointers = 0, starts = 0, ends = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(viewport))
                {
                    viewports++;
                    yield return instruction;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DubsMinimap), nameof(ObserveViewport)));
                    continue;
                }
                if (instruction.Calls(pointer))
                {
                    pointers++;
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(DubsMinimap), nameof(MapPointer));
                }
                else if (instruction.Calls(begin))
                {
                    starts++;
                    instruction.operand = AccessTools.Method(typeof(DubsMinimap), nameof(BeginMapGroup));
                }
                else if (instruction.Calls(end))
                {
                    ends++;
                    instruction.operand = AccessTools.Method(typeof(DubsMinimap), nameof(EndMapGroup));
                }
                yield return instruction;
            }
            if (viewports != 1 || pointers != 4 || starts != 1 || ends != 1)
                throw new InvalidOperationException($"Unexpected Dubs draw layout ({viewports}, {pointers}, {starts}, {ends}).");
        }
    }
}
