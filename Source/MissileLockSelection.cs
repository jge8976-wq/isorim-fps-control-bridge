using System;
using System.Collections.Generic;

namespace HSK.FpsBridge
{
    internal static class MissileLockSelection
    {
        // Eligibility retains the seeker's range, visibility and reticle cone.
        // Within that set, prefer hostiles, then the closest screen-space aim point.
        internal static T Choose<T>(IEnumerable<T> candidates,Func<T,bool> eligible,Func<T,bool> hostile,Func<T,float> reticleDistanceSquared) where T:class
        {
            T pick=null;bool bestHostile=false;float bestDistance=float.MaxValue;
            foreach(T candidate in candidates)
            {
                if(candidate==null||!eligible(candidate))continue;
                float distance=reticleDistanceSquared(candidate);
                if(!float.IsFinite(distance)||distance<0)continue;
                bool enemy=hostile(candidate);
                if(pick==null||enemy&&!bestHostile||enemy==bestHostile&&distance<bestDistance)
                {pick=candidate;bestHostile=enemy;bestDistance=distance;}
            }
            return pick;
        }
    }
}
