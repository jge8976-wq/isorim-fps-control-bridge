using System.Linq;
using RimWorld;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialCrew
    {
        internal static bool HasPilot(Pawn vehicle) => (vehicle.Faction != null && VehicleApi.Uncrewed(vehicle)) || VehicleApi.Handlers(vehicle).Where(VehicleApi.Movement)
            .Any(h => VehicleApi.List(h, "thingOwner").OfType<Pawn>()
                .Any(p => !p.Dead && !p.Downed && !p.InMentalState && VehicleApi.CanOperate(h, p)));

        internal static bool Check(Pawn vehicle, AerialFlightRecord flight)
        {
            if (HasPilot(vehicle)) return !flight.PilotLost;
            if (!flight.Airborne)
            {
                if (flight.Automatic) AerialRunway.Cancel(flight);
                flight.Status = L.T("没有可操作的驾驶员，无法起飞", "No capable pilot: takeoff unavailable");
                return false;
            }
            if (flight.PilotLost) return false;
            flight.PilotLost = true;
            flight.Navigate = flight.Orbit = flight.Cruise = flight.PilotAim = flight.BoostRequested = false;
            AerialRunway.Cancel(flight);
            if (VehicleControl.Owns(vehicle)) VehicleControl.End();
            flight.Status = L.T("驾驶员失能，载具失控坠落", "Pilot incapacitated: aircraft out of control");
            Log.Message("[FpsBridge] PILOT_LOST aircraft=" + vehicle.ThingID + "; native crash requested");
            // Existing native Kill interception owns the falling aircraft, evacuation and wreck.
            // A small hit requests a recoverable crash rather than catastrophic airburst handling.
            vehicle.Kill(new DamageInfo(DamageDefOf.Blunt, 1f));
            return false;
        }
    }
}
