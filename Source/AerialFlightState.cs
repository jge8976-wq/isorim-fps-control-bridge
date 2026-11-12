using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using UnityEngine;

namespace HSK.FpsBridge
{
    public sealed class AerialFlightRecord : IExposable
    {
        internal AerialMotor Motor;
        internal float HomeHeight, ReconSpeedFactor=1f;
        internal int NativeMapId=-1;
        internal IntVec3 NativeCell;
        internal bool NativeStamped;
        internal bool TacticalCameraPending;
        internal bool Airborne;
        internal bool Committed;
        internal float CommittedYaw;
        internal string Status;
        internal bool Landing, LandInputLatch, EngineStopped;
        internal V3 LandingTarget;
        internal bool Navigate,Orbit,Cruise;
        internal bool CombatEnabled,CombatAttackRun,CombatAim;
        internal bool CombatOrbit;
        internal Thing CombatTarget;
        internal string CombatStatus;
        internal string CombatGunKey;
        internal float CombatYaw,CombatPitch;
        internal int CombatRecoverUntil;
        internal V3 CombatEgress;
        internal V3 NavTarget;
        internal float NavHeight,OrbitRadius;
        internal int GodClimb,GodClimbFrame=-100;
        internal int GodForward,GodTurn;
        internal bool PilotAim;
        internal bool PilotLost;
        internal bool EnemyPilotLoss, EmergencyLanding;
        internal bool BoostRequested;
        internal int WingInputFrame=-100;
        internal float AimYaw,AimPitch;
        internal float RotorAngle, Spool;
        internal AerialRunwayMode RunwayMode;
        internal V3 RunwayStart,RunwayEnd;
        internal float RunwayYaw,RunwaySurface;
        internal bool TakeoffInputLatch;
        internal float CollisionRecoverySeconds;
        internal V3 CollisionRecoveryVelocity;
        internal bool Automatic=>RunwayMode!=AerialRunwayMode.None;
        public void ExposeData()
        {
            Scribe_Values.Look(ref Motor.Position.X, "x"); Scribe_Values.Look(ref Motor.Position.Y, "y"); Scribe_Values.Look(ref Motor.Position.Z, "z");
            Scribe_Values.Look(ref Motor.Velocity.X, "vx"); Scribe_Values.Look(ref Motor.Velocity.Y, "vy"); Scribe_Values.Look(ref Motor.Velocity.Z, "vz");
            Scribe_Values.Look(ref Motor.Yaw, "yaw"); Scribe_Values.Look(ref Motor.Pitch, "pitch"); Scribe_Values.Look(ref Motor.Roll, "roll");
            Scribe_Values.Look(ref NativeMapId,"nativeMapIdR8",-1);
            Scribe_Values.Look(ref TacticalCameraPending,"tacticalCameraPendingR8");
            Scribe_Values.Look(ref ReconSpeedFactor,"reconSpeedFactor",1f);
            Scribe_Values.Look(ref HomeHeight, "homeHeight"); Scribe_Values.Look(ref Airborne, "airborne");
            Scribe_Values.Look(ref PilotLost, "pilotLostR8");
            Scribe_Values.Look(ref EnemyPilotLoss, "enemyPilotLoss");
            Scribe_Values.Look(ref EmergencyLanding, "emergencyLanding");
            Scribe_Values.Look(ref Motor.Throttle,"throttle");
            Scribe_Values.Look(ref Motor.WingAirSpeed,"wingAirSpeedR4");Scribe_Values.Look(ref Motor.WingSink,"wingSinkR4");Scribe_Values.Look(ref Motor.WingAirReady,"wingAirReadyR4");
            Scribe_Values.Look(ref Landing,"landing");Scribe_Values.Look(ref EngineStopped,"engineStopped");
            Scribe_Values.Look(ref Navigate,"navigateR4");Scribe_Values.Look(ref Orbit,"orbitR4");Scribe_Values.Look(ref Cruise,"cruiseR4");
            Scribe_Values.Look(ref CombatEnabled,"combatEnabledR1");Scribe_Values.Look(ref CombatAttackRun,"combatAttackRunR1");
            Scribe_References.Look(ref CombatTarget,"combatTargetR1");
            Scribe_Values.Look(ref CombatGunKey,"combatGunKeyR2");
            Scribe_Values.Look(ref CombatOrbit,"combatOrbitR3");
            Scribe_Values.Look(ref NavTarget.X,"navX");Scribe_Values.Look(ref NavTarget.Y,"navY");Scribe_Values.Look(ref NavTarget.Z,"navZ");
            Scribe_Values.Look(ref NavHeight,"navHeight");Scribe_Values.Look(ref OrbitRadius,"orbitRadius");
            Scribe_Values.Look(ref LandingTarget.X,"landingX");Scribe_Values.Look(ref LandingTarget.Y,"landingY");Scribe_Values.Look(ref LandingTarget.Z,"landingZ");
            Scribe_Values.Look(ref RunwayMode,"runwayModeR6");Scribe_Values.Look(ref RunwayYaw,"runwayYawR6");Scribe_Values.Look(ref RunwaySurface,"runwaySurfaceR6");
            Scribe_Values.Look(ref RunwayStart.X,"runwayStartX");Scribe_Values.Look(ref RunwayStart.Y,"runwayStartY");Scribe_Values.Look(ref RunwayStart.Z,"runwayStartZ");
            Scribe_Values.Look(ref RunwayEnd.X,"runwayEndX");Scribe_Values.Look(ref RunwayEnd.Y,"runwayEndY");Scribe_Values.Look(ref RunwayEnd.Z,"runwayEndZ");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (Airborne && !Motor.WingAirReady) Motor = AerialFixedWing.Synchronize(Motor, true);
                else if (!Airborne && !EmergencyLanding) Motor = AerialFixedWing.Synchronize(Motor.Brake(), false);
            }
        }
    }

    // Each aircraft owns its saved pose, including when no player camera is attached.
    public sealed class AerialFlightGame : GameComponent
    {
        internal Dictionary<Pawn, AerialFlightRecord> Flights = new Dictionary<Pawn, AerialFlightRecord>();
        // Native ground control releases the flight pose, but keeps the aircraft's throttle setting.
        internal Dictionary<Pawn, float> WingThrottles = new Dictionary<Pawn, float>();
        private List<Pawn> keys;
        private List<AerialFlightRecord> values;
        private List<Pawn> throttleKeys;
        private List<float> throttleValues;
        internal RimWorld.Building_Turret DebugRangeTurret;
        public AerialFlightGame(Game game) { }
        public override void ExposeData()
        {
            Scribe_Collections.Look(ref Flights, "fpsAerialFlightsR1", LookMode.Reference, LookMode.Deep, ref keys, ref values);
            Scribe_Collections.Look(ref WingThrottles, "fpsWingThrottlesR1", LookMode.Reference, LookMode.Value, ref throttleKeys, ref throttleValues);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                Flights ??= new Dictionary<Pawn, AerialFlightRecord>();
                WingThrottles ??= new Dictionary<Pawn, float>();
                foreach (var p in Flights.Where(p => p.Key == null || p.Value == null || !p.Value.Motor.Finite || !float.IsFinite(p.Value.HomeHeight)).Select(p => p.Key).ToArray())
                    if (p != null) Flights.Remove(p);
            }
        }
        public override void GameComponentOnGUI()
        {
            if (Event.current?.type != EventType.Repaint) return;
            if (!VehicleControl.On) AerialFlight.DrawHud();
        }
        public override void GameComponentUpdate()
        {
            TurretTracers.DrawNative(Find.CurrentMap);
            AerialRangeOverlay.Draw();
            if (!AerialFlight.Enabled) return;
            AerialArrival.Frame();
            AerialHandoff.Frame();
            AerialOrders.Frame();
            AerialTactical.Frame();
            var selected = FpsMod.Settings.showTurretSphere ? Find.Selector?.SingleSelectedThing as RimWorld.Building_Turret : null;
            AerialDebugActions.DrawTurretRange(DebugRangeTurret ?? selected, DebugRangeTurret != null);
            Map map = Find.CurrentMap;
            AerialTurretFire.Draw(map);
            AerialProximityFuse.Draw(map);
            Ballistics.DrawAirborneRounds(map);
            CeCompat.DrawAirborneRounds(map);
            CeCompat.DrawNativeAirThreats(map);
        }
        public override void GameComponentTick()
        {
            AerialFireControl.Tick();
            AerialFlight.TickUncontrolled();
            if (Find.TickManager.TicksGame % 250 != 0) return;
            foreach (Pawn p in Flights.Keys.Where(p => p == null || p.Destroyed || p.Dead || !p.Spawned).ToArray())
                if (p != null) Flights.Remove(p);
            foreach (Pawn p in WingThrottles.Keys.Where(p => p.Destroyed || p.Dead).ToArray())
                WingThrottles.Remove(p);
        }
    }

    // Explicit opt-in, not '!Land': boats and fixed-wing aircraft do not silently acquire VTOL physics.
    public sealed class AerialFlightDef : Def
    {
        public string vehicleDef;
        public AerialFlightKind kind=AerialFlightKind.Rotorcraft;
        public int exhaustTier=-1;
        public AerialPropulsion propulsion=AerialPropulsion.None;
        public float maximumSpeed = 8f;
        public bool useNativeSpeed;
        public float maximumHeight = 120f;
        internal float MaximumSpeed(Pawn vehicle) => BaseSpeed(vehicle) * FpsMod.Settings.colonyAircraftSpeed;
        internal float BaseSpeed(Pawn vehicle)
        {
            float native = useNativeSpeed ? VehicleApi.AerialSpeed(vehicle) : 0f;
            return float.IsFinite(native) && native > 0f ? native : maximumSpeed;
        }
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors()) yield return e;
            if (string.IsNullOrWhiteSpace(vehicleDef)) yield return "Missing VTOL vehicleDef";
            if (!float.IsFinite(maximumSpeed) || maximumSpeed <= 0f || maximumSpeed > 24f) yield return "VTOL speed must be in (0,24] cells/second";
            if (!float.IsFinite(maximumHeight) || maximumHeight < 2f || maximumHeight > 200f) yield return "VTOL height must be in [2,200] cells";
        }
    }
}
