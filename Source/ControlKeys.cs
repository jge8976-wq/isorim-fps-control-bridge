using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // The game owns storage and conflict resolution. Input and mod settings use the SAME current KeyPrefs slots.
    internal static partial class ControlKeys
    {
        internal static readonly ControlAction[] Actions = (ControlAction[])Enum.GetValues(typeof(ControlAction));
        private static readonly KeyBindingDef[] defs = new KeyBindingDef[Actions.Length];
        private static bool waitingForRelease;
        private static int gateFrame = -1;
        private static bool gate;
        internal static bool Editing { get; set; }

        internal static KeyBindingDef Def(ControlAction action) => defs[(int)action] ??=
            DefDatabase<KeyBindingDef>.GetNamedSilentFail("FPS_" + action);
        internal static bool IsBinding(KeyBindingDef def) => def?.category?.defName == "FPS_ControlSkills" || def?.category?.defName == "FPS_ControlKeys" || def?.category?.defName == "FPS_FlightKeys" || def?.category?.defName == "FPS_VehicleKeys";
        private static bool FlightAction(ControlAction action) => action==ControlAction.AirAscend || action==ControlAction.AirDescend || action==ControlAction.AirLand || action==ControlAction.AirOrbit || action==ControlAction.AirParachute;
        private static bool Context(ControlAction action) => action==ControlAction.AirParachute ? VehicleControl.On && !VehicleControl.ExternalFollow && AerialFlight.Airborne(VehicleControl.Vehicle) : action==ControlAction.VehicleMachineGun ? VehicleControl.On : !FlightAction(action) || (VehicleControl.On && AerialFlight.Supports(VehicleControl.Vehicle) || AerialOrders.OwnKeys);
        internal static bool Keys(ControlAction action, out KeyBindingData data)
        {
            data = null;
            var def = Def(action);
            return def != null && KeyPrefs.KeyPrefsData != null && KeyPrefs.KeyPrefsData.keyPrefs.TryGetValue(def, out data);
        }
        internal static bool Matches(ControlAction action, KeyCode key) => key != KeyCode.None
            && Keys(action, out var keys) && (key == keys.keyBindingA || key == keys.keyBindingB);
        internal static bool RawHeld(KeyCode key) => key != KeyCode.None && (IsMouse(key)
            ? IsSide(key) ? SideMouse.Held(key) : Input.GetMouseButton(key - KeyCode.Mouse0) : Input.GetKey(key));
        private static bool IsMouse(KeyCode key) => key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6;
        private static bool IsSide(KeyCode key) => key == KeyCode.Mouse3 || key == KeyCode.Mouse4;
        internal static bool RawDown(KeyCode key) => !Suppressed && key != KeyCode.None && (IsMouse(key)
            ? IsSide(key) ? SideMouse.Down(key) : Input.GetMouseButtonDown(key - KeyCode.Mouse0) : Input.GetKeyDown(key));
        private static bool RawUp(KeyCode key) => key != KeyCode.None && (IsMouse(key)
            ? IsSide(key) ? SideMouse.Up(key) : Input.GetMouseButtonUp(key - KeyCode.Mouse0) : Input.GetKeyUp(key));

        // Binding a held key in a dialog must never fire it or turn its release into an E interaction.
        private static bool Suppressed
        {
            get
            {
                if (Editing) return true;
                if (gateFrame != Time.frameCount)
                {
                    gateFrame = Time.frameCount; gate = waitingForRelease;
                    if (waitingForRelease)
                    {
                        bool held = false;
                        foreach (ControlAction a in Actions)
                            if (Keys(a, out var k) && (RawHeld(k.keyBindingA) || RawHeld(k.keyBindingB))) { held = true; break; }
                        if (!held) waitingForRelease = false; // this release frame stays suppressed, too
                    }
                }
                return gate;
            }
        }
        internal static void Changed() { waitingForRelease = true; gateFrame = -1; KeyPrefs.Save(); }
        // Only an explicit edit resolves conflicts across the legacy skill and new control groups. At startup they
        // stay separate so native ErrorCheck cannot reset a user's pre-existing skill shortcut to its default.
        internal static void ResolveConflicts(KeyBindingDef chosen, KeyCode code)
        {
            if (code == KeyCode.None || KeyPrefs.KeyPrefsData == null) return;
            foreach (ControlAction a in Actions)
            {
                if (Def(a) == chosen || !Keys(a, out var k)) continue;
                if ((chosen?.category?.defName=="FPS_FlightKeys") != FlightAction(a)) continue;
                if ((chosen?.category?.defName=="FPS_VehicleKeys") != (a==ControlAction.VehicleMachineGun)) continue;
                if (k.keyBindingA == code) KeyPrefs.KeyPrefsData.SetBinding(Def(a), KeyPrefs.BindingSlot.A, KeyCode.None);
                if (k.keyBindingB == code) KeyPrefs.KeyPrefsData.SetBinding(Def(a), KeyPrefs.BindingSlot.B, KeyCode.None);
            }
        }
        internal static KeyCode PressedKey(ControlAction action)
        {
            if (!Context(action) || !Keys(action, out var k)) return KeyCode.None;
            return RawDown(k.keyBindingA) ? k.keyBindingA : RawDown(k.keyBindingB) ? k.keyBindingB : KeyCode.None;
        }
        internal static bool Down(ControlAction action) => PressedKey(action) != KeyCode.None;
        // Vehicle mouse presses are dispatched before the native map GUI; keyboard bindings stay in Update.
        internal static bool FrameDown(ControlAction action) => Context(action) && !Suppressed && Keys(action, out var k)
            && ((FrameKey(k.keyBindingA) && RawDown(k.keyBindingA)) || (FrameKey(k.keyBindingB) && RawDown(k.keyBindingB)));
        private static bool FrameKey(KeyCode key) => key < KeyCode.Mouse0 || key > KeyCode.Mouse2;
        internal static bool Held(ControlAction action) => Context(action) && !Suppressed && Keys(action, out var k)
            && (RawHeld(k.keyBindingA) || RawHeld(k.keyBindingB));
        internal static bool Up(ControlAction action) => Context(action) && !Suppressed && Keys(action, out var k)
            && (RawUp(k.keyBindingA) || RawUp(k.keyBindingB));
        internal static string Label(ControlAction action)
        {
            if (!Keys(action, out var k)) return L.T("未绑定", "Unbound");
            string a = k.keyBindingA == KeyCode.None ? "" : k.keyBindingA.ToStringReadable();
            string b = k.keyBindingB == KeyCode.None ? "" : k.keyBindingB.ToStringReadable();
            return a.Length == 0 ? b.Length == 0 ? L.T("未绑定", "Unbound") : b : b.Length == 0 ? a : a + "/" + b;
        }
        internal static bool Owns(KeyCode key, bool remote = false)
        {
            if (key == KeyCode.None) return false;
            if(key==KeyCode.LeftControl&&VehicleControl.On&&AerialFlight.Supports(VehicleControl.Vehicle))return true;
            foreach (ControlAction a in Actions)
            {
                if (remote && a != ControlAction.ViewCycle && a != ControlAction.ExitRecall && a != ControlAction.Capture
                    && a != ControlAction.LeanLeft && a != ControlAction.InteractLeanRight && a != ControlAction.ManualWork
                    && a != ControlAction.FireMode && a != ControlAction.Fire && a != ControlAction.Aim && a != ControlAction.Overlay) continue;
                if (a == ControlAction.Sandevistan || a == ControlAction.DeathEye) continue; // skill ownership checks requirements
                if (!Context(a)) continue;
                if (Matches(a, key)) return true;
            }
            return false;
        }
        internal static void ResetDefaults()
        {
            foreach (ControlAction a in Actions)
            {
                var def = Def(a);
                if (def == null || KeyPrefs.KeyPrefsData == null) continue;
                KeyPrefs.KeyPrefsData.SetBinding(def, KeyPrefs.BindingSlot.A, def.defaultKeyCodeA);
                KeyPrefs.KeyPrefsData.SetBinding(def, KeyPrefs.BindingSlot.B, def.defaultKeyCodeB);
            }
            Changed();
        }

    }
}
