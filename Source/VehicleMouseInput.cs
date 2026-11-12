using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Reserve input before both native map selection and Perspective Shift's GUI prefixes can act on it.
    internal static class VehicleMouseInput
    {
        public static void BeforeUIRoot()
        {
            if (!VehicleControl.OwnsMapInput) return;
            if (Find.Selector?.dragBox != null) Find.Selector.dragBox.active = false;
            Event e = Event.current;
            if (e == null || (!e.isMouse && e.type != EventType.ScrollWheel)) return;
            if (e.type == EventType.MouseDown && e.button >= 0 && e.button <= 6)
                VehicleControl.MouseDown(KeyCode.Mouse0 + e.button);
            // The frame input retains real button states for firing; Use only consumes the legacy GUI event.
            e.Use();
        }
        public static bool BeforeSelector(Selector __instance)
        {
            if (!VehicleControl.OwnsMapInput) return true;
            if (__instance.dragBox != null) __instance.dragBox.active = false;
            return false;
        }
        public static bool BeforePerspectiveGUI() => !VehicleControl.On;
        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(UIRoot_Play), "UIRootOnGUI"),
                prefix: First(nameof(BeforeUIRoot)));
            foreach (string name in new[] { "SelectorOnGUI", "HandleMapClicks", "SelectorOnGUI_BeforeMainTabs" })
                harmony.Patch(AccessTools.Method(typeof(Selector), name) ?? throw new MissingMethodException("Selector." + name),
                    prefix: First(nameof(BeforeSelector)));
            // A boarded crew avatar belongs to the vehicle camera; PS must not draw or process its old pawn HUD.
            harmony.Patch(Bindings.StateOnGUI, prefix: First(nameof(BeforePerspectiveGUI)));
        }
        private static HarmonyMethod First(string name) => new HarmonyMethod(typeof(VehicleMouseInput), name) { priority = Priority.First };
    }
}
