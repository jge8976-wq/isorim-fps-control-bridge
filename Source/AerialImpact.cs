using System;
using System.Collections.Generic;
using System.Linq;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialImpact
    {
        private static Game game;
        private static readonly Dictionary<(Thing,Thing),int> hitAt=new Dictionary<(Thing,Thing),int>();
        internal static int Hits;
        internal static bool Apply(Pawn v,AerialMotor before,ref AerialMotor next)
        {
            float speed=before.Velocity.Length,damage=AerialGuidance.Impact(speed,v.def.size.x*v.def.size.z);
            if(damage<=0||!v.Spawned)return false;
            if(game!=Current.Game){hitAt.Clear();game=Current.Game;Hits=0;}
            Bounds box=AerialGeometry.BoundsAt(v,next);
            box.Encapsulate(AerialGeometry.BoundsAt(v,before));
            bool hit=false;var seen=new HashSet<Thing>();int tick=Find.TickManager.TicksGame;
            foreach(var band in FloorSpace.Bands(v.Map))
            for(int x=Mathf.FloorToInt(box.min.x);x<=Mathf.FloorToInt(box.max.x);x++)
            for(int z=Mathf.FloorToInt(box.min.z);z<=Mathf.FloorToInt(box.max.z);z++)
            {
                IntVec3 cell=new IntVec3(x,0,z+band.Shift);if(!cell.InBounds(v.Map))continue;
                foreach(Thing t in cell.GetThingList(v.Map).ToArray())
                {
                    if(t is Pawn vehicle&&VehicleApi.IsVehicle(vehicle))continue; // shared pair solver owns all vehicle contacts
                    if(t==v||!t.Spawned||t.Destroyed||!seen.Add(t)||!(t is Pawn||t is Building||t is Plant tree&&tree.def.plant.IsTree))continue;
                    if(!(t is Pawn)&&(!t.def.destroyable||!t.def.useHitPoints))continue;
                    if(t is Building_Door door&&door.Open)continue;
                    if(hitAt.TryGetValue((v,t),out int last)&&tick-last<36)continue;
                    if(!AircraftModelContact.Touches(v,before,next,t))continue;
                    hitAt[(v,t)]=tick;Hits++;hit=true;
                    float dealt=t.TakeDamage(new DamageInfo(DamageDefOf.Blunt,damage,1f,before.Yaw,v)).totalDamageDealt;
                    AircraftCollisionDamage.ReturnDamage(v,t,dealt,before.Yaw);
                }
            }
            if(hit)
            {
                next.Velocity=next.Velocity*.6f;if(AerialFlight.Profile(v)?.kind==AerialFlightKind.FixedWing)next=AerialFixedWing.Synchronize(next,true);
            }
            if(hitAt.Count>512){var expired=new List<(Thing,Thing)>();foreach(var entry in hitAt)if(tick-entry.Value>600||entry.Key.Item1.Destroyed||entry.Key.Item2.Destroyed)expired.Add(entry.Key);foreach(var key in expired)hitAt.Remove(key);}
            return hit;
        }
    }
}
