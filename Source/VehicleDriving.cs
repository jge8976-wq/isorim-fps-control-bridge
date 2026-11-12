using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Continuous movement belongs only to this possession. Native map occupancy is updated at cell crossings.
    internal static class VehicleDriving
    {
        private static Pawn vehicle;
        private static bool poseReady;
        private static VehicleMotorState motor;
        private static float altitude, maximum,contactRemaining;
        private static V3 contactVelocity;
        private static int nextImpact, nextStat, nextLog, lastFrame;
        private static readonly Dictionary<IntVec3, bool> terrain = new Dictionary<IntVec3, bool>();
        internal static AerialMotor PhysicalPose=>new AerialMotor(new V3(motor.X,FloorSpace.Offset(vehicle).y,motor.Z-FloorSpace.Band(vehicle.Map,vehicle.Position).Shift),motor.Yaw){Velocity=contactRemaining>0?contactVelocity:new V3(Mathf.Sin(motor.Yaw*Mathf.Deg2Rad)*motor.Speed,0,Mathf.Cos(motor.Yaw*Mathf.Deg2Rad)*motor.Speed)};
        internal static bool Throttle { get; private set; }
        internal static float VisualYaw => AerialFlight.TryGet(vehicle, out var f) ? f.Motor.Yaw : motor.Yaw;
        internal static float SpeedFactor => 1f;
        internal static float Speed => AerialFlight.TryGet(vehicle, out var f) ? f.Motor.Velocity.Length : Math.Abs(motor.Speed);
        internal static bool IsMoving { get; private set; }
        internal static string Status { get; private set; }
        internal static Vector3 Position => AerialFlight.TryGet(vehicle, out var f) ? AerialFlight.RawRoot(vehicle, f) : new Vector3(motor.X, altitude, motor.Z);
        internal static bool ActiveFor(Pawn v) => poseReady && v != null && v == vehicle && VehicleControl.Owns(v);

        internal static void Begin(Pawn v)
        {
            // Do not override DrawPos until its native position has seeded the new motor.
            poseReady = false; vehicle = null; contactRemaining=0;contactVelocity=default;
            if (AerialFlight.Supports(v))
            {
                AerialFlight.Begin(v);
                if (!AerialFlight.TryGet(v,out _)) throw new InvalidOperationException("VTOL pose initialization failed");
                vehicle = v; poseReady = true; Throttle = IsMoving = false; Status = null; return;
            }
            Vector3 at = v.DrawPos;
            VehicleApi.Call(VehicleApi.Get(v, "vehiclePather"), "StopDead");
            if (VehicleApi.Bool(v, "Reverse")) VehicleApi.Set(v, "Reverse", false);
            motor = new VehicleMotorState(at.x, at.z, VehicleApi.NativeYaw(v)); altitude = at.y;
            vehicle = v; poseReady = true; maximum = 3f; nextImpact = nextStat = nextLog = 0; lastFrame = -1;
            Throttle = IsMoving = false; Status = null;
        }
        internal static void AcceptContact(AerialMotor pose)
        {
            var band=FloorSpace.Band(vehicle.Map,vehicle.Position);
            motor=new VehicleMotorState(pose.Position.X,pose.Position.Z+band.Shift,pose.Yaw,pose.Velocity.X*Mathf.Sin(pose.Yaw*Mathf.Deg2Rad)+pose.Velocity.Z*Mathf.Cos(pose.Yaw*Mathf.Deg2Rad));
            contactVelocity=pose.Velocity;contactRemaining=.3f;
        }
        internal static void Halt()
        {
            if (AerialFlight.TryGet(vehicle, out _)) { AerialFlight.Brake(vehicle); Throttle = IsMoving = false; return; }
            motor = motor.Halt(); Throttle = false;
            Moving(false);
        }
        internal static void End()
        {
            Pawn old = vehicle;
            if(AerialFlight.TryGet(old,out var flight)){flight.BoostRequested=false;flight.WingInputFrame=-100;}
            if(!(old!=null&&AerialFlight.Airborne(old)))Halt();
            if (old != null && old.Spawned && !old.Destroyed) VehicleApi.ManualHullTurn(old, 0f);
            poseReady = false; vehicle = null; Status = null; terrain.Clear();contactRemaining=0;contactVelocity=default;
            if (old != null && old.Spawned && !old.Destroyed) VehicleApi.Call(old, "Notify_Teleported", false, true);
        }
        private static void Moving(bool value)
        {
            if (IsMoving == value) return;
            IsMoving = value;
            if (vehicle != null && vehicle.Spawned && !vehicle.Destroyed) VehicleApi.ManualMoveEvent(vehicle, value);
        }
        internal static void Drive(Pawn v, int tick, int forward, int side)
        {
            if (AerialFlight.Supports(v)) { AerialFlight.Drive(v, forward, side); Throttle = AerialFlight.Airborne(v); IsMoving = Speed > .02f; return; }
            if (vehicle != v) Begin(v);
            if (lastFrame == Time.frameCount) return;
            lastFrame = Time.frameCount;
            if (v.stances?.stunner?.Stunned == true) { Halt(); return; }
            if (!VehicleApi.Drafted(v)) VehicleApi.Draft(v, true);
            if (!VehicleApi.Drafted(v) || !VehicleApi.Bool(v, "CanMoveFinal")&&contactRemaining<=0) { Halt(); return; }
            if (tick >= nextStat)
            {
                maximum = Mathf.Clamp(VehicleApi.ManualSpeed(v) * VehicleControl.Efficiency, .1f, 20f);
                nextStat = tick + 15;
            }
            Throttle = forward != 0; Status = null; terrain.Clear();
            VehicleMotorState before = motor;
            float remaining = Mathf.Clamp(Time.unscaledDeltaTime, 0f, .1f) * Find.TickManager.TickRateMultiplier;
            int budget = 0;
            while (remaining > .00001f && budget++ < 160)
            {
                float dt = Math.Min(.005f, remaining); remaining -= dt;
                VehicleMotorState proposed = motor.Step(dt, maximum, Math.Max(v.def.size.x, v.def.size.z), forward, side);
                if(contactRemaining>0){contactRemaining=Math.Max(0,contactRemaining-dt);proposed=new VehicleMotorState(motor.X+contactVelocity.X*dt,motor.Z+contactVelocity.Z*dt,motor.Yaw,motor.Speed);contactVelocity*=MathF.Exp(-4*dt);}
                AerialMotor prior=PhysicalPose,physical=prior;physical.Position.X=proposed.X;physical.Position.Z=proposed.Z-FloorSpace.Band(v.Map,v.Position).Shift;physical.Yaw=proposed.Yaw;
                physical.Velocity=new V3(Mathf.Sin(proposed.Yaw*Mathf.Deg2Rad)*proposed.Speed,0,Mathf.Cos(proposed.Yaw*Mathf.Deg2Rad)*proposed.Speed);
                bool impact=VehicleContacts.Resolve(v,prior,ref physical,dt);
                if(impact){AcceptContact(physical);proposed=motor;}
                if (Math.Abs(proposed.X - motor.X) + Math.Abs(proposed.Z - motor.Z) < .000001f) { motor = proposed; continue; }
                if (!Clear(v, proposed, tick)) { motor = motor.Halt(); break; }
                motor = proposed;
            }
            bool changed = Math.Abs(motor.X - before.X) + Math.Abs(motor.Z - before.Z) > .00001f;
            if (changed && !VehicleApi.ManualPosition(v, Position, motor.Yaw, Math.Abs(motor.Speed)))
            { motor = before.Halt(); changed = false; Block(L.T("地图边缘", "Map edge"), v.Position.ToString()); }
            if (changed) VehicleApi.ManualHullTurn(v, Mathf.DeltaAngle(before.Yaw, motor.Yaw));
            Moving(changed);
            if (VehicleControl.FollowHull)
                VehicleControl.SetLook(IsoRim.FirstPerson.Yaw + Mathf.DeltaAngle(before.Yaw, motor.Yaw), IsoRim.FirstPerson.Pitch);
        }
        private static bool Clear(Pawn v, VehicleMotorState proposed, int tick)
        {
            float a = proposed.Yaw * Mathf.Deg2Rad;
            float rx = (Math.Abs(Mathf.Cos(a)) * v.def.size.x + Math.Abs(Mathf.Sin(a)) * v.def.size.z) * .5f;
            float rz = (Math.Abs(Mathf.Sin(a)) * v.def.size.x + Math.Abs(Mathf.Cos(a)) * v.def.size.z) * .5f;
            for (int x = Mathf.FloorToInt(proposed.X - rx); x <= Mathf.FloorToInt(proposed.X + rx); x++)
                for (int z = Mathf.FloorToInt(proposed.Z - rz); z <= Mathf.FloorToInt(proposed.Z + rz); z++)
                {
                    if (!VehicleMotionRules.Covers(proposed.X, proposed.Z, proposed.Yaw, v.def.size.x, v.def.size.z, x, z)) continue;
                    IntVec3 cell = new IntVec3(x, v.Position.y, z);
                    if (!terrain.TryGetValue(cell, out bool passable)) terrain[cell] = passable = VehicleApi.ManualTerrain(v, cell);
                    if (!passable) { Block(L.T("地形不允许通行", "Terrain restriction"), cell.ToString()); return false; }
                    List<Thing> things = cell.GetThingList(v.Map);
                    for (int i = things.Count - 1; i >= 0; i--)
                    {
                        if (i >= things.Count) continue;
                        Thing thing = things[i];
                        if (thing == v || thing.Destroyed || !thing.Spawned) continue;
                        if (thing is Plant plant)
                        {
                            // Grass, crops and flowers never block the motor. Clear small saplings without repeated stops.
                            if (!VehicleMotionRules.PlantBlocks(plant.def.plant.IsTree, plant.Growth))
                            { if (plant.def.plant.IsTree) plant.Destroy(DestroyMode.Vanish); continue; }
                        }
                        else if (thing is Pawn other)
                        {
                            if (!VehicleApi.IsVehicle(other)) continue; // Native pawn collision settings are applied on cell crossings.
                            continue; // shared triangle contact solver, not native footprint occupancy
                        }
                        else if (!(thing is Building) || (thing.def.fillPercent <= .15f && thing.def.passability != Traversability.Impassable)) continue;
                        // Allow escape if a vehicle was spawned with a solid object already inside its footprint.
                        if (VehicleMotionRules.Covers(motor.X, motor.Z, motor.Yaw, v.def.size.x, v.def.size.z, x, z)) continue;
                        if (thing.def.useHitPoints && tick >= nextImpact)
                        {
                            nextImpact = tick + VehicleRules.Delay(15, VehicleControl.Efficiency);
                            float damage = VehicleRules.ContactDamage(v.def.size.x * v.def.size.z, Math.Abs(motor.Speed), thing is Plant);
                            float dealt = thing.TakeDamage(new DamageInfo(DamageDefOf.Blunt, damage, 0f, -1f, v)).totalDamageDealt;
                            if (dealt > 0f) v.TakeDamage(new DamageInfo(DamageDefOf.Blunt, Math.Clamp(dealt * .025f, .25f, 3f), 0f, -1f, thing));
                            if (v.Destroyed || !v.Spawned || v.Dead) return false;
                            if (thing.Destroyed || !thing.Spawned) continue;
                        }
                        Block(L.T("正在推撞", "Ramming"), thing.LabelShort + " " + cell); return false;
                    }
                }
            return true;
        }
        private static void Block(string reason, string detail)
        {
            Status = reason + " · " + detail;
            int tick = Find.TickManager.TicksGame;
            if (FpsMod.Settings.verboseLog && tick >= nextLog)
            { nextLog = tick + 120; Log.Message("[FpsBridge] MANUAL_DRIVE_BLOCK vehicle=" + vehicle?.ThingID + "; " + Status); }
        }
    }
}
