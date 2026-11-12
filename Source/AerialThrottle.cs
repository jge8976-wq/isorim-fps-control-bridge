using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialThrottle
    {
        internal static float Clamp(float value) => float.IsFinite(value) ? Math.Clamp(value, .3f, 1f) : 1f;
        internal static float Get(Pawn vehicle) =>
            AerialFlight.Store?.WingThrottles.TryGetValue(vehicle, out float setting) == true ? Clamp(setting) : 1f;
        internal static void Set(Pawn vehicle, float value)
        {
            if (AerialFlight.Store is not AerialFlightGame game) return;
            float setting = Clamp(value);
            if (setting == 1f) game.WingThrottles.Remove(vehicle);
            else game.WingThrottles[vehicle] = setting;
        }
        internal static void ApplyInput(Pawn vehicle, AerialFlightRecord flight, bool increase, bool decrease, float dt)
        {
            bool wing = AerialFlight.Profile(vehicle)?.kind == AerialFlightKind.FixedWing;
            if (flight != null)
            {
                flight.WingInputFrame = Time.frameCount;
                flight.BoostRequested = increase && !decrease && flight.Airborne
                    && !flight.Automatic && !flight.Landing && !flight.EngineStopped && AerialFlight.HasPower(vehicle);
            }
            if (wing && (increase || decrease))
                Set(vehicle, Get(vehicle) + (decrease ? -1f : 1f) * dt * .35f);
        }

        internal static void ReadInput(Pawn vehicle, AerialFlightRecord flight, float dt)
        {
            bool increase = ControlKeys.RawHeld(KeyCode.LeftShift) || ControlKeys.RawHeld(KeyCode.RightShift);
            bool decrease = AerialFlight.Profile(vehicle)?.kind == AerialFlightKind.FixedWing && ControlKeys.RawHeld(KeyCode.LeftControl);
            ApplyInput(vehicle, flight, increase, decrease, dt);
        }
        internal static Command Command(Pawn vehicle) => new Command_Action
        {
            defaultLabel = L.T("节流阀 ", "Throttle ") + (Get(vehicle) * 100).ToString("0") + "%",
            defaultDesc = L.T("空中按住 Shift 紧急加力，松开恢复常规上限。左 Ctrl 降低推力，PageUp/PageDown 每次调整 5%。", "Hold Shift in flight for emergency boost. Release to return to the normal limit. Left Ctrl lowers throttle. PageUp/PageDown adjust by 5%."),
            icon = TexCommand.ForbidOff,
            action = () => Find.WindowStack.Add(new Dialog_Slider(value => L.T("节流阀 ", "Throttle ") + value + "%", 30, 100,
                value => Set(vehicle, value / 100f), Mathf.RoundToInt(Get(vehicle) * 100f)))
        };
    }
}
