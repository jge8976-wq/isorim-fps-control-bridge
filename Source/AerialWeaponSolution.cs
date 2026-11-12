using System;
using System.Runtime.CompilerServices;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialWeaponSolution
    {
        private sealed class Drop {internal int Tick=-100;internal V3 Origin,Velocity,Impact;internal bool Contact;}
        private static readonly ConditionalWeakTable<object,Drop> drops=new();
        internal static bool Owns(object gun,out Pawn vehicle)
        {vehicle=VehicleApi.Get(gun,"vehicle") as Pawn;return vehicle!=null&&AerialFlight.Airborne(vehicle);}
        internal static bool Fixed(Pawn vehicle,object gun)=>AerialMounts.Fixed(vehicle,gun)
            || !VehicleApi.Bool(gun,"IsTargetable")&&VehicleApi.Get(gun,"attachedTo")==null;
        internal static V3 PhysicalBore(Pawn vehicle,object gun)
        {
            if(AerialMounts.Fixed(vehicle,gun))return VehicleModels.V(AerialMounts.Rotation(vehicle)*Vector3.forward);
            if(Fixed(vehicle,gun)&&AerialFlight.TryGet(vehicle,out var flight))
            {
                float localYaw=Mathf.DeltaAngle(flight.Motor.Yaw,VehicleApi.Float(gun,"TurretRotation"));
                return VehicleModels.V(AerialGeometry.Rotation(flight.Motor)*Quaternion.Euler(0,localYaw,0)*VehicleModels.Weapon(vehicle,gun).boreLocalDirection);
            }
            return VehicleWeapons.Direction(vehicle,gun);
        }
        internal static V3 Bore(Pawn vehicle,object gun)
        {
            if(AerialAutoAttack.Automatic(vehicle)&&!AerialArmament.Bomb(gun))
            {
                var target=(LocalTargetInfo)VehicleApi.Get(gun,"targetInfo");
                if(Solve(vehicle,gun,target,out var direction,out _,false))return direction;
            }
            return PhysicalBore(vehicle,gun);
        }
        internal static bool Valid(Pawn vehicle,LocalTargetInfo target)=>target.IsValid&&vehicle.Spawned
            &&(target.HasThing?target.Thing!=vehicle&&AerialTargetLock.Valid(target.Thing,vehicle.Map):target.Cell.InBounds(vehicle.Map));
        internal static V3 Point(Pawn vehicle,LocalTargetInfo target)
        {
            if(target.HasThing)return VehicleModels.V(AerialTargetLock.Point(target.Thing));
            FloorBand band=FloorSpace.Band(vehicle.Map,target.Cell);return new V3(target.Cell.x+.5f,band.Height+.5f,target.Cell.z-band.Shift+.5f);
        }
        internal static bool Solve(Pawn vehicle,object gun,LocalTargetInfo target,out V3 direction,out string reason,bool checkArc=true)
        {
            direction=default;reason=null;
            if(!Valid(vehicle,target)){reason="target";return false;}
            V3 origin=VehicleWeapons.FlightMuzzle(vehicle,gun),point=Point(vehicle,target);
            float distance=(point-origin).Length;
            if(!float.IsFinite(distance)){reason="target";return false;}
            if(AerialAutoAttack.Automatic(vehicle)&&(distance<VehicleApi.Float(gun,"MinRange")||distance>AerialArmament.AiRange(gun)))
            {reason="range";return false;}
            var projectile=VehicleWeapons.ProjectileFor(gun);
            if(projectile?.projectile==null){reason="projectile";return false;}
            if(AerialArmament.Bomb(gun))
            {
                // A release is permitted only when the actual inherited-velocity
                // drop prediction reaches the target; never aim a bomb like a gun.
                direction=Bore(vehicle,gun);
                if(!checkArc)return true;
                if(!DropWindow(vehicle,gun,projectile,origin,point)){reason="drop-window";return false;}
                return true;
            }
            bool leadAircraft = AerialAutoAttack.Automatic(vehicle)
                && AerialFlight.Profile(vehicle).kind == AerialFlightKind.FixedWing
                && (AerialArmament.Data(gun)?.kind is "Gun" or "Cannon"
                    || !AerialArmament.Trial(gun) && VehicleWeapons.IsMachineGun(gun));
            direction=VehicleCeSpeed.AimAt(vehicle,gun,projectile,origin,target,leadAircraft);
            if(!direction.Finite||direction.Length<.001f){reason="trajectory";return false;}
            if(checkArc&&!Fixed(vehicle,gun)&&VehicleControl.Owns(vehicle))
            {
                var pose=AerialFlight.Store.Flights[vehicle].Motor;
                Vector3 local=Quaternion.Inverse(AerialGeometry.Rotation(pose))*VehicleModels.U(direction);
                float pitch=-Mathf.Atan2(local.y,new Vector2(local.x,local.z).magnitude)*Mathf.Rad2Deg;
                var profile=VehicleModels.Weapon(vehicle,gun);
                if(pitch<profile.minPitch-.01f||pitch>profile.maxPitch+.01f){reason="pitch";return false;}
                float bearing=pose.Yaw+Mathf.Atan2(local.x,local.z)*Mathf.Rad2Deg;
                Vector3 mount=(Vector3)VehicleApi.Get(gun,"TurretLocation");
                if(!(bool)VehicleApi.Call(gun,"AngleBetween",mount+VehicleModels.U(HitMath.Direction(bearing,0))*16)){reason="traverse";return false;}
            }
            if(Ballistics.SightContact(vehicle,origin,point,out V3 hit,false)&&(hit-point).Length>.8f
                &&(!target.HasThing||!target.Thing.OccupiedRect().Contains(FloorSpace.Cell(vehicle.Map,hit,vehicle))))
            {reason="obstacle";return false;}
            return true;
        }
        internal static bool CanFire(Pawn vehicle,object gun,LocalTargetInfo target,out string reason)
        {
            if (!AircraftSystems.CanFire(vehicle, gun)) { reason = "weapon-system-disabled"; return false; }
            if(!Solve(vehicle,gun,target,out var desired,out reason))return false;
            if(AerialAutoAttack.UnsafeExplosive(vehicle,gun,target)){reason="friendly-near-target";return false;}
            if(AerialArmament.Bomb(gun))return true;
            if(AerialAutoAttack.Automatic(vehicle))
            {
                if(AerialAiAim.FixedWing(vehicle) &&
                    (!AerialAiAim.InCone(vehicle,Point(vehicle,target)-VehicleWeapons.FlightMuzzle(vehicle,gun))
                     || !AerialAiAim.InCone(vehicle,desired)))
                {reason="alignment";return false;}
            }
            else
            {
                float error=Vector3.Angle(VehicleModels.U(Bore(vehicle,gun)),VehicleModels.U(desired));
                if(error>2f){reason="alignment";return false;}
            }
            if(AerialArmament.Data(gun)?.lockableMissile==true&&!AerialFireControl.LockReady(gun,target.Thing))
            {reason="lock";return false;}
            return true;
        }
        internal static void Lay(Pawn vehicle,object gun,LocalTargetInfo target)
        {
            if(AerialArmament.Bomb(gun)||Fixed(vehicle,gun)||!Solve(vehicle,gun,target,out var desired,out _))return;
            float yaw=Mathf.Atan2(desired.X,desired.Z)*Mathf.Rad2Deg;
            VehicleApi.Call(gun,"AlignToAngleRestricted",yaw);
            VehicleWeapons.NativePitch(gun,desired);
            VehicleApi.Call(gun,"StartTicking");
        }
        internal static bool DropWindow(Pawn v,object gun,ThingDef projectile,V3 origin,V3 target)
        {
            float radius=Math.Clamp(CeCompat.VehicleBlastRadius(projectile)*.5f,.8f,3f);
            return TryDropImpact(v,gun,projectile,origin,out V3 impact)&&(impact-target).Length<=radius;
        }

        internal static bool TryDropImpact(Pawn v,object gun,ThingDef projectile,V3 origin,out V3 impact)
        {
            var cache=drops.GetOrCreateValue(gun);int now=Find.TickManager.TicksGame;
            V3 velocity=AerialArmament.ReleaseVelocity(v,gun,default,0);
            if(now-cache.Tick>=6||(origin-cache.Origin).Length>.25f||(velocity-cache.Velocity).Length>.02f)
            {
                cache.Tick=now;cache.Origin=origin;cache.Velocity=velocity;cache.Contact=false;
                var native=CeCompat.CreateNativeMotionPreview(v,projectile,origin,velocity,null,true,(LocalTargetInfo)VehicleApi.Get(gun,"targetInfo"),VehicleWeapons.NativeSpeed(gun,projectile));
                var trace=new Ballistics.ExternalTrace(v);V3 a=origin;
                float gravity=ViewRules.Gravity(FpsMod.Settings.gravity);
                for(int tick=0;tick<1200;tick++)
                {
                    V3 b;if(native!=null){b=native.Step(velocity);velocity=native.Velocity;}else{velocity.Y-=gravity;b=a+velocity;}
                    if(trace.Segment(v.Map,a,b,out float fraction,out _,out _,out _,out _))
                    {cache.Impact=V3.Lerp(a,b,fraction);cache.Contact=true;break;}
                    if(!FloorSpace.InWorld(v.Map,b))break;a=b;
                }
            }
            impact=cache.Impact;
            return cache.Contact;
        }
    }
}
