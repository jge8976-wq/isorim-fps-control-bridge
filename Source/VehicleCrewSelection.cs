using System;
using System.Collections.Generic;

namespace HSK.FpsBridge
{
    internal static class VehicleCrewSelection
    {
        // A vehicle-level command has no explicitly chosen pawn. Prefer an actual
        // occupant capable of both controls; retain native order among equal choices.
        // Explicit first-person boarding continues to use the pawn who boarded.
        internal static T Choose<T>(IEnumerable<T> occupants, Func<T, bool> canDrive, Func<T, bool> canShoot) where T : class
        {
            T chosen = null;
            int best = -1;
            foreach (T occupant in occupants)
            {
                int score = (canDrive(occupant) ? 1 : 0) + (canShoot(occupant) ? 2 : 0);
                if (score > best) { chosen = occupant; best = score; }
            }
            return chosen;
        }
    }
}
