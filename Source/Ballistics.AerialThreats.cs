using System;
using RimWorld;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static partial class Ballistics
    {
        // Called from the native Launch postfix, so native accuracy, cadence, costs and projectile damage remain intact.
        private static void AdoptAerialThreat(Projectile p,Thing launcher)
        {
            if(!Enabled||nativeShot!=null||launcher==null||!launcher.Spawned||p.Destroyed||!p.Spawned||shots.ContainsKey(p)
                ||launcher==Bridge.ShootingPawn||launcher is Pawn owned&&VehicleControl.Owns(owned)
                ||!AerialCombat.Target(p.intendedTarget,out Pawn target)||!WeaponCompatibility.SafeProjectile(p.def))return;
            V3 start=VehicleModels.V(AerialCombat.Origin(launcher));
            Vector3 nativeStart=originRef(p),nativeEnd=destinationRef(p);
            Bounds body=AerialGeometry.BoundsAt(target,AerialFlight.Store.Flights[target].Motor);
            Vector3 aim=body.center;
            // Native misses and cover rolls still miss by their original horizontal offset.
            Vector3 intended=target.DrawPos;
            aim.x+=nativeEnd.x-intended.x;aim.z+=nativeEnd.z-intended.z;
            if (launcher is Building_Turret turret)
                start = AerialTurretFire.Muzzle(turret, AerialTurretTargeting.TryAim(turret, out var bore) ? bore : VehicleModels.V(aim)-start);
            else
            {
                start.X=nativeStart.x;start.Z=nativeStart.z-FloorSpace.Band(launcher.Map,launcher.Position).Shift;
            }
            float speed=ViewRules.Speed(p.def.projectile.SpeedTilesPerTick,FpsMod.Settings.speed);
            float gravity=ViewRules.Gravity(FpsMod.Settings.gravity);
            V3 delta = VehicleModels.V(aim) - start;
            if (!AerialCombat.Intercept(target, start, VehicleModels.V(aim), speed, FpsMod.Settings.antiAirLead, out var velocity))
                velocity=delta*(speed/Math.Max(.001f,delta.Length)); // unreachable: the real round falls short, never a 2D hit
            if(launcher is Building_Turret emplacement)AerialTurretFire.Record(emplacement,start,velocity);
            V3 far=Landing(p.Map,start,velocity,gravity);
            IntVec3 cell=FloorSpace.Cell(p.Map,far).ClampInsideMap(p.Map);
            FloorSpace.OwnFlight(p);
            originRef(p)=FloorSpace.Logical(p.Map,start,launcher.Position);
            destinationRef(p)=FloorSpace.Logical(p.Map,far,cell);
            // Near-vertical rounds still have a finite life and are advanced in real 3D space.
            ticksRef(p)=Math.Max(1,Mathf.CeilToInt((far-start).Length/Math.Max(.01f,speed)));
            shots[p]=new Shot{kind=Kind.Round,pos=start,vel=velocity,gravity=gravity,startY=start.Y,muzzleY=start.Y,
                lastTick=Find.TickManager.TicksGame,launcher=launcher,turretOperator=AerialTurretFire.Operator(launcher)};
            p.Position=FloorSpace.Cell(p.Map,start).ClampInsideMap(p.Map);
        }
    }
}
