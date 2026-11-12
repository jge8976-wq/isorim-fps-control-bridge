using System.Linq;
using RimWorld;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialCrew
    {
        internal const float EmergencyGlideHeight = 15f;

        internal static bool HasPilot(Pawn vehicle) => (vehicle.Faction != null && VehicleApi.Uncrewed(vehicle)) || VehicleApi.Handlers(vehicle).Where(VehicleApi.Movement)
            .Any(h => VehicleApi.List(h, "thingOwner").OfType<Pawn>()
                .Any(p => !p.Dead && !p.Downed && !p.InMentalState && VehicleApi.CanOperate(h, p)));

        internal static bool Check(Pawn vehicle, AerialFlightRecord flight)
        {
            if (flight.EmergencyLanding) return true;
            if (HasPilot(vehicle)) return !flight.PilotLost;
            if (!flight.Airborne)
            {
                if (flight.Automatic) AerialRunway.Cancel(flight);
                flight.Status = L.T("没有可操作的驾驶员，无法起飞", "No capable pilot: takeoff unavailable");
                return false;
            }
            if (flight.PilotLost) return false;
            flight.PilotLost = true;
            AerialFireControl.Stop(vehicle, flight);
            flight.Navigate = flight.Orbit = flight.Cruise = flight.PilotAim = flight.BoostRequested = false;
            AerialRunway.Cancel(flight);
            if (VehicleControl.Owns(vehicle)) VehicleControl.End();

            if (flight.EnemyPilotLoss || vehicle.Faction?.HostileTo(Faction.OfPlayer) == true)
            {
                var profile = AerialFlight.Profile(vehicle);
                float height = AerialRunway.Agl(vehicle, flight);
                bool planned = profile?.kind == AerialFlightKind.Rotorcraft && AerialLanding.Request(vehicle, flight)
                    || profile?.kind == AerialFlightKind.FixedWing && float.IsFinite(height) && height <= EmergencyGlideHeight
                        && AerialRunway.RequestEmergencyLanding(vehicle, flight);
                if (planned)
                {
                    flight.EmergencyLanding = true;
                    return true;
                }
            }
            Crash(vehicle, flight);
            return false;
        }

        internal static bool LandingWithoutPilot(Pawn vehicle) => AerialFlight.TryGet(vehicle, out var flight) && flight.EmergencyLanding;

        internal static bool RaidCanCommand(Pawn vehicle) => !AerialFlight.TryGet(vehicle, out var flight)
            || (!flight.PilotLost && !flight.EnemyPilotLoss);

        internal static void Crash(Pawn vehicle, AerialFlightRecord flight)
        {
            flight.EmergencyLanding = false;
            flight.Status = L.T("驾驶员失能，载具失控坠落", "Pilot incapacitated: aircraft out of control");
            Log.Message("[FpsBridge] PILOT_LOST aircraft=" + vehicle.ThingID + "; native crash requested");
            // Existing native Kill interception owns the falling aircraft, evacuation and wreck.
            // A small hit requests a recoverable crash rather than catastrophic airburst handling.
            vehicle.Kill(new DamageInfo(DamageDefOf.Blunt, 1f));
        }
    }
}
