using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static partial class Ballistics
    {
        private sealed class RatkinFlight
        {
            internal bool cannon, bounced;
            internal float remaining = float.PositiveInfinity;
            internal readonly HashSet<Thing> pierced = new HashSet<Thing>();
        }
        // Native Impact owns damage, penetration count, explosive effects and the initial ground bounce.
        // Only the continuation's position/height is retained in our 3D simulation.
        private static bool ContinueRatkin(Projectile p, Shot shot, Thing thing, V3 normal)
        {
            if (shot.ratkin == null || p.Destroyed || !p.Spawned) return false;
            RatkinFlight flight = shot.ratkin;
            if (!flight.cannon)
            {
                if (thing == null || thing is Building) return false;
                int count = VehicleApi.Int(p, "currentPenetrationCount");
                int maximum = VehicleApi.Int(p.def.projectile, "maxPenetrationCount");
                if (maximum <= 0 || count >= maximum) return false;
                flight.pierced.Add(thing);
                shot.pos += shot.vel * (.06f / Mathf.Max(.001f, shot.vel.Length));
                return true;
            }
            if (thing != null || flight.bounced || normal.Y < .5f) return false;
            V3 delta = new V3(destinationRef(p).x-originRef(p).x,0,destinationRef(p).z-originRef(p).z);
            float distance = delta.Length;
            if (!delta.Finite || distance <= .1f) return false;
            V3 forward = delta * (1f/distance);
            float speed = Mathf.Max(.01f, Mathf.Sqrt(shot.vel.X*shot.vel.X+shot.vel.Z*shot.vel.Z));
            flight.bounced = true; flight.remaining = distance;
            shot.pos += forward*.06f + new V3(0,.10f,0);
            shot.vel = forward*speed; shot.gravity=0;
            return true;
        }
    }
}
