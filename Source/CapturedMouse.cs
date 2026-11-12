using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static partial class Bridge
    {
        internal static void InstallCapturedMouse(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(UIRoot_Play), "UIRootOnGUI"),
                prefix: new HarmonyMethod(typeof(Bridge), nameof(BeforeCapturedRootGUI)) { priority = Priority.First + 400 });
            harmony.Patch(Bindings.StateOnGUI,
                prefix: new HarmonyMethod(typeof(Bridge), nameof(BeforeCapturedPerspectiveGUI)) { priority = Priority.First + 400 });
            var update = AccessTools.DeclaredMethod(Bindings.StateType, "Update");
            if (update != null) harmony.Patch(update,
                prefix: new HarmonyMethod(typeof(Bridge), nameof(ObservePerspectiveUpdate)) { priority = Priority.First + 400 });
            Log.Message("[FpsBridge] MOUSE_GUARD READY; captured mouse reserved before map/PS GUI; polled aim/fire unchanged; diagnostics=Logs/FpsBridge-control-diagnostics.json");
        }

        public static void BeforeCapturedRootGUI()
        {
            if (!Active) return;
            diagGuiCalls++;
            Event e = Event.current;
            if (e?.type == EventType.Repaint) diagGuiRepaints++;
            if (!CaptureOwnsSelection() || e == null) return;
            if (Find.Selector?.dragBox != null) Find.Selector.dragBox.active = false;
            if (e.isMouse || e.type == EventType.ScrollWheel || e.type == EventType.ContextClick)
            {
                diagMouseConsumed++;
                // Only the legacy GUI event is consumed; Input.GetMouseButton/Down remain FPS's aim/fire input.
                e.Use();
            }
        }

        public static bool BeforeCapturedPerspectiveGUI()
        {
            if (Active) diagPsGuiAttempts++;
            // FPS draws its own HUD from UIRoot_Play. Do not run the upstream context menus/gizmos
            // under a captured pointer; PS's normal GUI returns when the pointer is released.
            bool allow = !CaptureOwnsSelection();
            if (!allow) diagPsGuiSuppressed++;
            return allow;
        }

        public static void ObservePerspectiveUpdate() { if (Active) diagPsUpdates++; }
    }
}
