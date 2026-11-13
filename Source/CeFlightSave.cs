using System.Linq;
using Verse;

namespace HSK.FpsBridge
{
    internal static partial class CeCompat
    {
        internal static void ExposeFlight(Thing __instance)
        {
            bool owns=traces.TryGetValue(__instance,out Trace trace)&&!trace.completed;
            if(Scribe.mode==LoadSaveMode.Saving&&!owns)return;
            if (Scribe.mode == LoadSaveMode.Saving && trace?.nativeRestorePending == true && __instance.Map != null)
                RestoreNativeFlight(__instance, trace);
            Scribe_Values.Look(ref owns,"fpsCeFlightR3",false);
            if(!owns)return;
            if(trace==null){trace=new Trace();traces.Add(__instance,trace);}
            float x=trace.pos.X,y=trace.pos.Y,z=trace.pos.Z,vx=trace.velocity.X,vy=trace.velocity.Y,vz=trace.velocity.Z;
            Scribe_Values.Look(ref x,"fpsCeX");Scribe_Values.Look(ref y,"fpsCeY");Scribe_Values.Look(ref z,"fpsCeZ");
            Scribe_Values.Look(ref vx,"fpsCeVx");Scribe_Values.Look(ref vy,"fpsCeVy");Scribe_Values.Look(ref vz,"fpsCeVz");
            Scribe_Values.Look(ref trace.gravity,"fpsCeGravity");Scribe_Values.Look(ref trace.bounces,"fpsCeBounces");
            if (Scribe.mode == LoadSaveMode.Saving && !trace.nativeRestorePending) trace.nativePhysicsMode = trace.nativePhysics ? 1 : -1;
            Scribe_Values.Look(ref trace.nativePhysicsMode,"fpsCeNativePhysicsR5",0);
            Scribe_Values.Look(ref trace.nativeVelocityMotion,"fpsCeNativeVelocityMotionR5");
            Scribe_Values.Look(ref trace.nativeHeight,"fpsCeNativeHeightR5");
            Scribe_Values.Look(ref trace.nativeShift,"fpsCeNativeShiftR5");
            Scribe_Values.Look(ref trace.nativeLevel,"fpsCeNativeLevelR6");
            if (Scribe.mode == LoadSaveMode.Saving && !trace.nativeRestorePending) trace.nativeUnitsVersion = 1;
            Scribe_Values.Look(ref trace.nativeUnitsVersion,"fpsCeNativeUnitsR6",0);
            Scribe_References.Look(ref trace.launcher,"fpsCeOwner");Scribe_References.Look(ref trace.turretOperator,"fpsCeTurretOperator");Scribe_References.Look(ref trace.homing,"fpsCeHeadTarget");
            Scribe_References.Look(ref trace.aerialTarget,"fpsAirMissileTargetR14");Scribe_Values.Look(ref trace.aerialTurn,"fpsAirMissileTurnR14");
            trace.pos=new V3(x,y,z);trace.previous=trace.pos;trace.velocity=new V3(vx,vy,vz);
            if(Scribe.mode==LoadSaveMode.PostLoadInit)
            {
                trace.nativePhysics = trace.nativePhysicsMode == 1;
                trace.nativeRestorePending = true;
                trace.map = __instance.Map;
                if (__instance.Map != null) RestoreNativeFlight(__instance, trace);
                trace.lastTick=Find.TickManager.TicksGame;RestoredFlights++;
            }
        }

        private static void RestoreNativeFlight(Thing projectile, Trace trace)
        {
                Map map = projectile.Map;
                if (map == null || !trace.nativeRestorePending) return;
                RestoreNativeDestination(projectile);
                ThingDef equipment = VehicleApi.Get(projectile, "equipmentDef") as ThingDef;
                var throwVerb = Named("Verb_ThrowGrenade");
                bool savedThrow = throwVerb != null && equipment?.Verbs?.Any(v => v.verbClass != null && throwVerb.IsAssignableFrom(v.verbClass)) == true;
                if (trace.nativePhysics && trace.nativeUnitsVersion == 0)
                {
                    // R5 native traces stamped CE vertical coordinates as world
                    // coordinates. Decode through the original launch frame once.
                    FloorBand original = NativeOriginBand(projectile);
                    trace.nativeLevel = original.Level;
                    trace.pos = NativePointToWorld(map,
                        new UnityEngine.Vector3(trace.pos.X, trace.pos.Y - trace.nativeHeight, trace.pos.Z + trace.nativeShift), original);
                    trace.previous = trace.pos;
                    trace.velocity = NativeVelocityToWorld(trace.velocity);
                    trace.nativeHeight = original.Height; trace.nativeShift = original.Shift;
                }
                if (trace.nativePhysicsMode == 0 && trace.bounces == 0 && !savedThrow && !projectile.def.projectile.flyOverhead)
                {
                    // Older saved direct-fire traces have native CE origin/physics fields,
                    // but no launch-floor stamp. Recover the frame from that saved origin.
                    FloorBand band = NativeOriginBand(projectile);
                    trace.nativeHeight = band.Height; trace.nativeShift = band.Shift; trace.nativeLevel = band.Level;
                    // Preserve a pre-R5 FPS flight's saved world position and
                    // velocity. A lerped worker's old analytic launch curve has
                    // never owned that motion, so resume with CE velocity physics.
                    trace.nativeVelocityMotion = true;
                    trace.nativePhysics = true;
                }
                trace.nativeUnitsVersion = 1;
                trace.nativeRestorePending = false;
                trace.map=map;trace.contacts=new Ballistics.ExternalTrace(trace.launcher,trace.turretOperator ?? AerialTurretFire.Operator(trace.launcher));
                trace.lastTick=Find.TickManager.TicksGame-1;
        }

        private static void RestoreNativeDestination(Thing projectile)
        {
            var destination=(UnityEngine.Vector2)VehicleApi.Get(projectile,"Destination");
            if(float.IsFinite(destination.x)&&float.IsFinite(destination.y))return;
            // CE omits Destination when it equals the launch-derived default.
            // During LoadingVars that default is evaluated before PostLoadInit
            // initializes GravityPerWidth, producing an infinite/NaN destination.
            // Re-evaluate the SAME native formula after those fields are ready;
            // retain explicit finite destinations and the saved flight clock.
            float distance=VehicleApi.Float(projectile,"DistanceTraveled");
            var origin=(UnityEngine.Vector2)VehicleApi.Get(projectile,"origin");
            var restored=origin+UnityEngine.Vector2.up.RotatedBy(VehicleApi.Float(projectile,"shotRotation"))*distance;
            if(float.IsFinite(restored.x)&&float.IsFinite(restored.y))VehicleApi.Set(projectile,"Destination",restored);
        }
    }
}
