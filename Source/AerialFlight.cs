using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialFlight
    {
        private static int lastFrame = -1;
        private static Pawn lastVehicle;
        private static AerialDoubleTap landTap;
        internal static bool Enabled { get; private set; }
        internal static AerialFlightGame Store => Current.Game?.GetComponent<AerialFlightGame>();
        internal static AerialFlightDef Profile(Pawn v) => VehicleApi.IsVehicle(v)
            ? DefDatabase<AerialFlightDef>.AllDefsListForReading.FirstOrDefault(d => d.vehicleDef == v.def.defName) : null;
        internal static bool Supports(Pawn v) => Enabled && Profile(v) != null && VehicleApi.Get(v.def, "type")?.ToString() == "Air";
        internal static bool TryGet(Pawn v, out AerialFlightRecord record)
        {
            record = null;
            if(v==null||!v.Spawned||Store==null||!Store.Flights.TryGetValue(v,out record)||!record.Motor.Finite)return false;
            return true;
        }
        internal static bool Airborne(Pawn v) => TryGet(v, out var r) && r.Airborne;
        internal static bool OwnsMotion(Pawn v) => TryGet(v,out var r)&&(VehicleControl.Owns(v)||VehicleApi.Drafted(v)||r.Airborne||r.Automatic||r.Landing);
        internal static void Begin(Pawn v)
        {
            if (!Supports(v) || Store == null) return;
            AerialArmament.Ensure(v);
            VehicleApi.Call(VehicleApi.Get(v, "vehiclePather"), "StopDead");
            if (!TryGet(v, out var r))
            {
                V3 at = FloorSpace.Spot(v);
                Vector3 native = (Vector3)VehicleApi.Call(v,"TrueCenter");
                FloorBand band=FloorSpace.Band(v.Map,v.Position);
                var projection=new V3(native.x,at.Y,native.z-band.Shift);
                if (!AerialSafety.ProjectionStep(projection,at,2f))
                {
                    Log.Warning("[FpsBridge] AERIAL_SEED rejected non-native DrawPos; recovering from TrueCenter: "+v.ThingID);
                    at.X=projection.X;at.Z=projection.Z;
                }
                at.Y -= AerialGeometry.LocalBounds(v).min.y;
                Store.Flights[v] = r = new AerialFlightRecord { Motor = new AerialMotor(at, VehicleApi.NativeYaw(v)), HomeHeight = at.Y };
                AerialArrival.Initial(v,r);
            }
            AerialArrival.Sync(v,r);
            if(!r.Airborne&&!r.Automatic&&!r.Landing)
            {
                // Re-entering a saved ground pose must use its current hull and a flat attitude.
                // A stale pitch can put the support probe below the actual runway surface.
                var flat=r.Motor.Brake();flat.Pitch=flat.Roll=0;
                Bounds box=AircraftFootprint.BoundsAt(v,flat);
                float support=AerialCollision.Support(v,box,Math.Max(FloorSpace.Band(v.Map,v.Position).Height+.08f,box.min.y+.08f));
                if(float.IsFinite(support))
                {
                    flat.Position.Y+=support+.012f-box.min.y;
                    if(AerialCollision.Clear(v,flat)){r.Motor=flat;r.HomeHeight=flat.Position.Y;}
                }
            }
            if(!r.Airborne&&!r.Automatic)r.Motor = r.Motor.Brake();
            if(!r.Airborne&&!r.Automatic)StopEngine(v,r);
            if(VehicleControl.Owns(v)&&!VehicleApi.Drafted(v))VehicleApi.Draft(v,true);
            lastFrame = -1; lastVehicle = v;
            landTap=default;
            Log.Message("[FpsBridge] AERIAL_ON vehicle=" + v.ThingID + "; native="+v.Position+"; seed="+r.Motor.Position.X+","+r.Motor.Position.Y+","+r.Motor.Position.Z+"; mode=VTOL; coordinates=true-space; state=per-vehicle");
        }
        internal static void Brake(Pawn v) { if (TryGet(v, out var r) && !r.Airborne&&!r.Automatic) r.Motor = r.Motor.Brake(); }
        internal static Vector3 Raw(Pawn v, Vector3 world)
        {
            FloorBand band = FloorSpace.Band(v.Map, v.Position);
            return new Vector3(world.x, world.y - band.Height, world.z + band.Shift);
        }
        internal static Vector3 RawRoot(Pawn v, AerialFlightRecord r) => Raw(v, VehicleModels.U(r.Motor.Position));
        internal static void Drive(Pawn v,int forward,int turn)
        {
            if(!TryGet(v,out var r)){Begin(v);if(!TryGet(v,out r))return;}
            if(lastFrame==Time.frameCount&&lastVehicle==v)return;
            lastFrame=Time.frameCount;lastVehicle=v;
            bool valid=!Find.TickManager.Paused&&VehicleControl.Captured&&Application.isFocused;
            if(!valid){landTap.Press(false,Time.unscaledTime,false);r.BoostRequested=false;Brake(v);return;}
            bool wing=Profile(v).kind==AerialFlightKind.FixedWing;
            int climb=(ControlKeys.Held(ControlAction.AirAscend)?1:0)-(!wing&&ControlKeys.Held(ControlAction.AirDescend)?1:0);
            if(!ControlKeys.Held(ControlAction.AirAscend))r.TakeoffInputLatch=false;
            if(!r.Airborne&&!r.Automatic&&ControlKeys.Down(ControlAction.AirAscend))AerialRunway.RequestTakeoff(v,r);
            if(r.TakeoffInputLatch||!r.Airborne)climb=0;
            bool pilotAim=VehicleControl.PilotLook;
            if(pilotAim&&!r.PilotAim&&r.Airborne)VehicleControl.SetLook(r.Motor.Yaw,r.Motor.Pitch);
            r.PilotAim=pilotAim;
            if(!VehicleControl.FixedScope){r.AimYaw=FirstPerson.Yaw;r.AimPitch=FirstPerson.Pitch;}
            if(r.PilotAim&&(Math.Abs(Input.GetAxisRaw("Mouse X"))+Math.Abs(Input.GetAxisRaw("Mouse Y"))>.02f)){r.Navigate=r.Orbit=r.Cruise=false;}
            bool landing=landTap.Press(ControlKeys.RawDown(KeyCode.LeftControl),Time.unscaledTime,!wing)||ControlKeys.Down(ControlAction.AirLand);
            if(landing)
            {
                if(r.Landing){AerialRunway.Cancel(r);r.Status=L.T("已取消自动着陆","Auto landing cancelled");}
                else AerialLanding.Request(v,r);
            }
            if(forward!=0||turn!=0){r.Navigate=r.Orbit=r.Cruise=false;}
            if(climb!=0&&r.Navigate)r.NavHeight=Math.Clamp(r.NavHeight+climb*3f*Time.unscaledDeltaTime*Find.TickManager.TickRateMultiplier,r.HomeHeight,r.HomeHeight+Profile(v).maximumHeight);
            if(r.Landing&&!r.Automatic)
            {
                if(r.LandInputLatch){if(forward==0&&turn==0&&climb==0)r.LandInputLatch=false;forward=turn=climb=0;}
                else if(forward!=0||turn!=0||climb!=0){r.Landing=false;r.Status=L.T("手动接管，取消自动着陆","Manual control: landing cancelled");}
            }
            float dt=Mathf.Clamp(Time.unscaledDeltaTime,0f,.1f)*Find.TickManager.TickRateMultiplier;
            AerialThrottle.ReadInput(v,r,dt);
            if(r.BoostRequested)r.Navigate=r.Orbit=r.Cruise=false;
            float oldYaw=r.Motor.Yaw;
            Advance(v,r,forward,turn,climb,dt);
            if(VehicleControl.FollowHull&&(!r.PilotAim||!r.Airborne))VehicleControl.SetLook(FirstPerson.Yaw+Mathf.DeltaAngle(oldYaw,r.Motor.Yaw),FirstPerson.Pitch);
        }
        internal static bool HasPower(Pawn v)
        {
            object fuel=VehicleApi.Get(v,"CompFueledTravel");
            return (fuel==null||VehicleApi.Float(fuel,"Fuel")>.0001f)&&VehicleApi.AerialSpeed(v)>.01f;
        }
        internal static float NormalMaximum(Pawn v) => AerialFixedWing.EffectiveMaximum(Profile(v).MaximumSpeed(v),VehicleControl.Owns(v)?VehicleControl.Efficiency:1f)
            *(TryGet(v,out var flight)?flight.ReconSpeedFactor:1f);
        internal static float EmergencyMaximum(Pawn v) => Profile(v).BaseSpeed(v)*2f;
        internal static void StopEngine(Pawn v,AerialFlightRecord r)
        {
            r.EngineStopped=true;AerialRunway.Cancel(r);r.Navigate=r.Orbit=r.Cruise=r.BoostRequested=false;r.Motor=r.Motor.Brake();var m=r.Motor;m.Throttle=0;r.Motor=AerialFixedWing.Synchronize(m,false);
        }
        // Shared by normal input, unattended landing and native regression. The caller owns the simulation clock.
        internal static void Advance(Pawn v,AerialFlightRecord r,int forward,int turn,int climb,float remaining)
        {
            if(!AerialCrew.Check(v,r))return;
            AerialArrival.Sync(v,r);
            var profile=Profile(v);bool fixedWing=profile.kind==AerialFlightKind.FixedWing;
            var systems = AircraftSystems.Flight(v);
            if (r.Airborne && systems.critical)
            {
                v.Kill(new DamageInfo(DamageDefOf.Blunt, 1f));
                return;
            }
            bool stunned = v.stances?.stunner?.Stunned == true;
            if (stunned) forward = turn = climb = 0;
            if(!r.Airborne&&!r.Automatic&&!r.Landing&&(forward!=0||turn!=0)&&HasPower(v))
            {r.EngineStopped=false;if(!VehicleApi.Drafted(v))VehicleApi.Draft(v,true);}
            bool powered=!stunned&&!r.EngineStopped&&HasPower(v);
            if(!r.Airborne&&!powered&&!r.Automatic){r.Status=L.T("地面待命：WASD滑行，空格自动起飞","Ground ready: WASD taxi, Space auto takeoff");return;}
            if(!powered&&r.Automatic){AerialRunway.Cancel(r);r.Status=L.T("动力不足，取消自动起降","Power lost: automatic sequence cancelled");}
            bool boost=BoostActive(v,r);
            float normalMaximum=NormalMaximum(v);
            float maximum=(boost?EmergencyMaximum(v):normalMaximum)*systems.power;
            float throttle=fixedWing&&!boost?AerialThrottle.Get(v):1f;
            int budget=0;
            bool emp = stunned && v.stances.stunner.StunFromEMP;
            if (emp) r.BoostRequested = false;
            r.Status=AerialRunway.Phase(r)??(r.Landing?L.T("自动着陆：减速／下降","Auto landing: slowing / descending")
                :!powered?L.T("动力不足，缓降","No power: descending"):r.Airborne?null:L.T("地面滑行 · 空格自动起飞","Ground taxi · Space auto takeoff"));
            if (emp) r.Status = L.T("EMP 停机 ", "EMP disabled ") + (v.stances.stunner.StunTicksLeft / 60f).ToString("0.0") + " s";
            while(remaining>.000001f&&budget++<240)
            {
                float dt=Math.Min(.005f,remaining);remaining-=dt;
                AerialMotor before=r.Motor;
                bool automatic=r.Automatic,landing=r.Landing,complete=false;
                if(r.Navigate&&!r.Orbit&&fixedWing){float dx=before.Position.X-r.NavTarget.X,dz=before.Position.Z-r.NavTarget.Z;if(dx*dx+dz*dz<r.OrbitRadius*r.OrbitRadius*2.25f)r.Orbit=true;}
                AerialMotor next=automatic?AerialRunwayRules.Automatic(before,dt,profile.kind,maximum,r.RunwayStart,r.RunwayEnd,r.RunwayYaw,ref r.RunwayMode,out complete)
                    :r.Landing&&powered?before.LandingStep(dt,r.LandingTarget,false)
                    :!r.Airborne?AerialRunwayRules.Taxi(before,dt,forward,turn,profile.BaseSpeed(v))
                    :r.Navigate?AerialGuidance.Step(before,dt,r.NavTarget,r.OrbitRadius,r.Orbit,r.NavHeight,maximum,r.HomeHeight+profile.maximumHeight,profile.kind,false,powered)
                    :r.PilotAim&&VehicleControl.Owns(v)&&!boost?AerialGuidance.MouseStep(before,dt,forward,turn,climb,maximum,r.HomeHeight+profile.maximumHeight,profile.kind,false,powered,r.AimYaw,r.AimPitch,boost)
                    :fixedWing&&powered?AerialFixedWing.Step(before,dt,forward,turn,climb,maximum,r.HomeHeight+profile.maximumHeight,before.Yaw,before.Pitch,boost)
                    :before.StepKind(dt,forward,turn,climb,maximum,r.HomeHeight+profile.maximumHeight,profile.kind,false,powered,boost);
                if(fixedWing&&r.Airborne&&!automatic&&!landing)
                {
                    float yaw=r.PilotAim&&VehicleControl.Owns(v)?r.AimYaw:before.Yaw,pitch=r.PilotAim&&VehicleControl.Owns(v)?r.AimPitch:before.Pitch;
                    if(r.Navigate){yaw=AerialGuidance.Heading(before,r.NavTarget,r.OrbitRadius,r.Orbit);pitch=-Mathf.Atan2(r.NavHeight-before.Position.Y,Math.Max(5,(VehicleModels.U(r.NavTarget)-VehicleModels.U(before.Position)).magnitude))*Mathf.Rad2Deg;}
                    if(r.CombatAim&&!VehicleControl.Owns(v)){yaw=r.CombatYaw;pitch=r.CombatPitch;}
                    next=AerialFixedWing.Step(before,dt,forward,turn,climb,maximum,r.HomeHeight+profile.maximumHeight,yaw,pitch,boost,maximum*throttle,powered,normalMaximum);
                }
                if (emp && r.Airborne && !fixedWing)
                {
                    next.Velocity = before.Velocity * (1f - .25f * dt);
                    next.Velocity.Y = Math.Max(-28f, before.Velocity.Y - 9.81f * dt);
                    next.Position = before.Position + next.Velocity * dt;
                }
                if(!fixedWing&&r.CombatAim&&!VehicleControl.Owns(v)&&!automatic&&!landing)
                {
                    next.Yaw=before.Yaw+Math.Clamp(Mathf.DeltaAngle(before.Yaw,r.CombatYaw),-65f*dt,65f*dt);
                    next.Pitch=before.Pitch+(r.CombatPitch-before.Pitch)*(1-MathF.Exp(-3f*dt));
                }
                if (r.Airborne && !automatic && (stunned || systems.control < 1f))
                {
                    float response = stunned ? 0f : systems.control;
                    float unrestrictedYaw = next.Yaw;
                    next.Yaw = Mathf.LerpAngle(before.Yaw, next.Yaw, response);
                    next.Pitch = Mathf.Lerp(before.Pitch, next.Pitch, response);
                    var correction = Quaternion.AngleAxis(Mathf.DeltaAngle(unrestrictedYaw, next.Yaw), Vector3.up);
                    next.Velocity = VehicleModels.V(correction * VehicleModels.U(next.Velocity));
                    next.Position = before.Position + VehicleModels.V(correction * VehicleModels.U(next.Position - before.Position));
                }
                if (r.Airborne && powered && !automatic && (!fixedWing || landing))
                {
                    float speed = next.Velocity.Length;
                    if (speed > maximum)
                    {
                        next.Velocity *= maximum / speed;
                        next.Position = before.Position + next.Velocity * dt;
                    }
                }
                if(r.Airborne&&!automatic&&r.CollisionRecoverySeconds>0)
                {
                    r.CollisionRecoverySeconds=Math.Max(0,r.CollisionRecoverySeconds-dt);
                    next.Velocity.X=r.CollisionRecoveryVelocity.X;next.Velocity.Z=r.CollisionRecoveryVelocity.Z;
                    next.Position.X=before.Position.X+next.Velocity.X*dt;next.Position.Z=before.Position.Z+next.Velocity.Z*dt;
                }
                if(r.Airborne&&AerialBoundary.Enforce(v,r,ref next))return;
                if(boost)r.Status=L.T("紧急加力 · 上限 ","EMERGENCY BOOST · CAP ")+maximum.ToString("0.0")+L.T(" 格/秒 · 燃油 ×2"," cells/s · FUEL x2");
                Bounds shape=AircraftFootprint.BoundsAt(v,next);
                float surface=AerialCollision.Support(v,shape,Math.Max(shape.min.y,AircraftFootprint.BoundsAt(v,before).min.y)+.08f);
                // A powered low pass never becomes a landing command. Hold clearance, keep horizontal momentum and engine.
                if (emp && r.Airborne && float.IsFinite(surface) && shape.min.y <= surface + .012f && next.Velocity.Y < -4f)
                {
                    v.Kill(new DamageInfo(DamageDefOf.Blunt, 1f));
                    return;
                }
                float clearance=r.Airborne&&powered&&!fixedWing&&!landing&&!automatic?AerialRunwayRules.HoverClearance:.012f;
                bool gravityGroundContact=fixedWing&&r.Airborne&&before.WingSink<-.5f&&float.IsFinite(surface)&&shape.min.y<surface+.012f;
                if(float.IsFinite(surface)&&shape.min.y<surface+clearance)
                {
                    next.Position.Y+=surface+clearance-shape.min.y;
                    if(next.Velocity.Y<0)next.Velocity.Y=0;
                }
                if(!r.Airborne&&!automatic&&!landing&&!float.IsFinite(surface))
                {next=before.Brake();r.Status=L.T("前方没有可滑行地面","No taxi surface ahead");}
                VehicleContacts.Resolve(v,before,ref next,dt);
                if(!automatic)AerialImpact.Apply(v,before,ref next);
                if(v.Destroyed||v.Dead)return;
                bool clear=automatic?AerialCollision.LandingClear(v,next):AerialCollision.Clear(v,next);
                if(!clear)
                {
                    if(automatic)
                    {
                        AerialRunway.Cancel(r);r.Motor=before.Brake();
                        if(!r.Airborne)StopEngine(v,r);
                        r.Status=L.T("起降路径出现障碍，已中止","Obstacle on automatic route: sequence aborted");break;
                    }
                    next=AerialSlide.Resolve(v,before,next);
                    float slidX=next.Position.X-before.Position.X,slidZ=next.Position.Z-before.Position.Z;
                    float incoming=MathF.Sqrt(before.Velocity.X*before.Velocity.X+before.Velocity.Z*before.Velocity.Z);
                    if(r.Airborne&&r.CollisionRecoverySeconds<=0&&incoming>.5f&&slidX*slidX+slidZ*slidZ<.00000001f)
                    {
                        // Brief physical recoil leaves room for steering. Every recoil step
                        // still passes this collision solver; never teleport through a wall.
                        float speed=Math.Clamp(incoming*.25f,.6f,3f);
                        r.CollisionRecoveryVelocity=new V3(-before.Velocity.X/incoming*speed,0,-before.Velocity.Z/incoming*speed);
                        r.CollisionRecoverySeconds=.35f;next.Velocity.X=r.CollisionRecoveryVelocity.X;next.Velocity.Z=r.CollisionRecoveryVelocity.Z;
                    }
                    if(r.Landing)AerialRunway.Cancel(r);
                    r.Status=L.T("接触障碍：可转向、上升或退离","Obstacle contact: turn, climb or move away");
                }
                r.Motor=next;
                Bounds final=AircraftFootprint.BoundsAt(v,next);float floor=AerialCollision.Support(v,final,final.min.y+.06f);
                bool contact=float.IsFinite(floor)&&final.min.y<=floor+.025f;
                if(automatic)
                {
                    r.Airborne=r.RunwayMode!=AerialRunwayMode.TakeoffRoll&&r.RunwayMode!=AerialRunwayMode.LandingRoll&&!contact;
                    if(complete)
                    {
                        if(landing){r.Airborne=false;r.HomeHeight=r.Motor.Position.Y;StopEngine(v,r);r.Status=L.T("着陆完成，停机停油 · WASD滑行","Landed, engine off · WASD taxi");}
                        else{r.Airborne=true;r.AimPitch=0;r.Status=L.T("自动起飞完成，已交还操控","Takeoff complete: manual control ready");if(VehicleControl.Owns(v)&&VehicleControl.PilotLook)VehicleControl.SetLook(r.Motor.Yaw,0);}
                        break;
                    }
                }
                // Taxi deliberately has zero airspeed. Only an airborne stall can trigger touchdown.
                else if((landing||!powered||gravityGroundContact||fixedWing&&r.Airborne&&AerialFixedWing.Stalled(next,maximum))&&contact&&next.Velocity.Y<=0)
                {
                    var settled=next;settled.Pitch=settled.Roll=0;settled.Position.Y+=floor+.012f-AircraftFootprint.BoundsAt(v,settled).min.y;
                    if(!AerialCollision.Clear(v,settled))continue;
                    r.Motor=settled;r.Airborne=false;r.HomeHeight=settled.Position.Y;StopEngine(v,r);
                    r.Status=L.T("已着陆，发动机关闭／不耗油","Landed: engine off / zero fuel use");break;
                }
                if(!r.Airborne&&!r.Automatic&&forward==0&&turn==0&&AerialRunwayRules.FlatSpeed(next)<.03f)StopEngine(v,r);
            }
            if(!Commit(v,r)){r.Motor=r.Motor.Brake();r.Status=L.T("位置跳变／地图边界已阻止","Position jump / map boundary blocked");}
        }
        private static bool Commit(Pawn v, AerialFlightRecord r)
        {
            Vector3 native=(Vector3)VehicleApi.Call(v,"TrueCenter");
            FloorBand band=FloorSpace.Band(v.Map,v.Position);
            var from=new V3(native.x,r.Motor.Position.Y,native.z-band.Shift);
            float cap=BoostActive(v,r)?EmergencyMaximum(v):Profile(v).MaximumSpeed(v);
            float budget=2f+cap*2f*Mathf.Clamp(Time.unscaledDeltaTime,0f,.1f)*Math.Max(1f,Find.TickManager.TickRateMultiplier);
            if (!AerialSafety.ProjectionStep(from,r.Motor.Position,budget))
            {
                var recovered=r.Motor.Brake();recovered.Position.X=from.X;recovered.Position.Z=from.Z;r.Motor=recovered;
                Log.WarningOnce("[FpsBridge] AERIAL_POSITION guarded a discontinuous projection for "+v.ThingID,17470201);
                return false;
            }
            float delta = r.Committed ? Mathf.DeltaAngle(r.CommittedYaw,r.Motor.Yaw) : 0f;
            if (!VehicleApi.AerialPosition(v, RawRoot(v, r), r.Motor.Yaw,delta)) return false;
            r.Committed = true; r.CommittedYaw = r.Motor.Yaw;
            return true;
        }
        internal static void Install(Harmony harmony)
        {
            var target = AccessTools.Method(VehicleApi.PawnType, "DynamicDrawPhaseAt");
            if (target == null) throw new MissingMethodException("VehiclePawn.DynamicDrawPhaseAt");
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(AerialGeometry), nameof(AerialGeometry.DrawVehicle))
                { priority = Priority.First + 100 });
            var launch = AccessTools.Method(AccessTools.TypeByName("Vehicles.CompVehicleLauncher"), "Launch");
            if (launch != null) harmony.Patch(launch, prefix: new HarmonyMethod(typeof(AerialFlight), nameof(BeforeNativeLaunch)));
            var launchGizmos=AccessTools.Method(AccessTools.TypeByName("Vehicles.CompVehicleLauncher"),"CompGetGizmosExtra");
            if(launchGizmos!=null)harmony.Patch(launchGizmos,prefix:new HarmonyMethod(typeof(AerialHandoff),nameof(AerialHandoff.BeforeGizmos)){priority=Priority.First+100});
            // The installed model pack's old eye prefixes assume yaw-only mounts. Let the 3D-aware methods handle these aircraft.
            Type optional = AccessTools.TypeByName("HSK.VehicleModels.OptionalFps");
            foreach (string name in new[] { "CrewEyePrefix", "SightPrefix" })
            {
                var old = optional == null ? null : AccessTools.DeclaredMethod(optional,name);
                if (old != null) harmony.Patch(old,prefix:new HarmonyMethod(typeof(AerialFlight),nameof(BeforeLegacyEyePrefix)) { priority = Priority.First });
            }
            var fuelType=AccessTools.TypeByName("Vehicles.CompFueledTravel");
            var fuelRate=AccessTools.PropertyGetter(fuelType,"ConsumptionRatePerTick");
            if(fuelRate==null)throw new MissingMethodException("CompFueledTravel.ConsumptionRatePerTick");
            harmony.Patch(fuelRate,postfix:new HarmonyMethod(typeof(AerialFlight),nameof(FuelRate)));
            var fuelActive=AccessTools.PropertyGetter(fuelType,"ShouldConsumeNow");
            if(fuelActive==null)throw new MissingMethodException("CompFueledTravel.ShouldConsumeNow");
            harmony.Patch(fuelActive,postfix:new HarmonyMethod(typeof(AerialFlight),nameof(FuelActive)));
            AerialOrders.Install(harmony);
            AerialCombat.Install(harmony);
            Enabled = true;
            Log.Message("[FpsBridge] AERIAL_VTOL READY; opt-in models=" + DefDatabase<AerialFlightDef>.AllDefsListForReading.Count);
        }
        public static bool BeforeLegacyEyePrefix(Pawn __0, ref bool __result)
        { if (!TryGet(__0,out _)) return true; __result=true; return false; }
        public static bool BeforeNativeLaunch(ThingComp __instance)
        {
            Pawn v = __instance.parent as Pawn;
            if (TryGet(v,out var current)&&(current.Airborne||current.Automatic||current.Landing))
            {
                Messages.Message(L.T("请先着陆，再使用原版世界地图起飞。", "Land before launching to the world map."), MessageTypeDefOf.RejectInput, false);
                return false;
            }
            if (v != null && Store != null) Store.Flights.Remove(v);
            return true;
        }
        internal static void TickUncontrolled()
        {
            if(Store==null)return;
            foreach(var pair in Store.Flights.ToArray())
            {
                Pawn v=pair.Key;var r=pair.Value;
                if(!v.Spawned||v.Destroyed||v.Dead||!Supports(v))continue;
                if(!VehicleControl.Owns(v)&&!VehicleApi.Drafted(v)&&!r.Airborne&&!r.Automatic&&!r.Landing)
                {AerialHandoff.Release(v,r);continue;}
                bool power=!r.EngineStopped&&HasPower(v);
                r.Spool=Mathf.MoveTowards(r.Spool,power?1f:0f,power?.025f:.012f);
                r.RotorAngle=(r.RotorAngle+20f*r.Spool)%360f;
                if(VehicleControl.Owns(v))continue;
                int rise=Time.frameCount-r.GodClimbFrame<=1?r.GodClimb:0;
                int forward=Time.frameCount-r.GodClimbFrame<=1?r.GodForward:0,turn=Time.frameCount-r.GodClimbFrame<=1?r.GodTurn:0;
                if(!r.Airborne&&!r.Automatic&&!r.Landing&&forward==0&&turn==0&&AerialRunwayRules.FlatSpeed(r.Motor)<.03f)continue;
                if(forward==0&&turn==0)AerialOrders.Hold(v,r);
                int climb=Time.frameCount-r.GodClimbFrame<=1?r.GodClimb:0;
                Advance(v,r,forward,turn,climb,1f/60f);
            }
        }
        internal static bool BoostActive(Pawn v,AerialFlightRecord r)=>r.BoostRequested&&Time.frameCount-r.WingInputFrame<=1
            &&Application.isFocused&&Find.TickManager?.Paused==false&&r.Airborne&&!r.Automatic&&!r.Landing&&!r.EngineStopped
            &&v.stances?.stunner?.Stunned!=true
            &&(VehicleControl.Owns(v)?VehicleControl.Captured:AerialOrders.Selected==v)&&HasPower(v);
        public static void FuelRate(ThingComp __instance,ref float __result)
        {if(__instance.parent is Pawn v&&TryGet(v,out var r)){if(r.EngineStopped&&!r.Airborne)__result=0f;else if(BoostActive(v,r))__result*=2f;}}
        public static void FuelActive(ThingComp __instance,ref bool __result)
        {
            if(!(__instance.parent is Pawn v)||!TryGet(v,out var r))return;
            if(r.EngineStopped&&!r.Airborne)__result=false;
            else if(!r.EngineStopped&&(r.Airborne||r.Automatic||AerialRunwayRules.FlatSpeed(r.Motor)>.03f)&&VehicleApi.Float(__instance,"Fuel")>.0001f)__result=true;
        }
        internal static string KindLabel(AerialFlightKind k)=>k==AerialFlightKind.DropPod?L.T("空投舱","POD"):k==AerialFlightKind.FixedWing?L.T("固定翼","FIXED WING"):L.T("直升机","ROTOR");
        internal static void DrawHud()
        {
            Pawn v = VehicleControl.Vehicle ?? AerialOrders.Selected;
            if (!TryGet(v, out var r)) return;
            Color old = GUI.color; GameFont font = Text.Font;
            try
            {
                GUI.color = Color.white; Text.Font = GameFont.Small;
                var rect = new Rect(UI.screenWidth - 400f, UI.screenHeight - (VehicleControl.On?218f:360f), 386f, 170f);
                Widgets.DrawBoxSolid(rect, new Color(.025f, .05f, .075f, .88f));
                float speed = r.Motor.Velocity.Length;
                string state = AerialRunway.Phase(r)??AerialFireControl.Status(v)??(r.Orbit?L.T("绕点盘旋","Orbit"):r.Navigate?L.T("飞往目标／定点悬停","Navigate / hover"):null) ?? r.Status ?? (r.Airborne ? L.T("悬停／飞行", "Hover / flight") : L.T("地面", "Ground"));
                float effectiveMaximum=NormalMaximum(v);
                bool emergency=BoostActive(v,r);
                if(emergency)state=r.Status??L.T("紧急加力","Emergency boost");
                float displayedCap=emergency?EmergencyMaximum(v)*AircraftSystems.Flight(v).power:effectiveMaximum*AerialThrottle.Get(v);
                string wingState=Profile(v).kind==AerialFlightKind.FixedWing?L.T("空速 ","Airspeed ")+r.Motor.WingAirSpeed.ToString("0.0")+L.T(" · 上限 "," · cap ")+displayedCap.ToString("0.0")+L.T(" · 机型 "," · nominal ")+Profile(v).BaseSpeed(v).ToString("0.0")+(r.Airborne&&AerialFixedWing.Stalled(r.Motor,effectiveMaximum)?L.T(" · 失速下坠"," · STALL / FALLING"):L.T(" · 节流 "," · throttle ")+(AerialThrottle.Get(v)*100).ToString("0")+"%"):null;
                var preview=AerialRunway.Preview(v,r);
                if(preview!=null&&!r.Automatic&&!r.CombatEnabled&&!emergency)state=preview.Clear?(r.Airborne?L.T("跑道畅通 · 自动着陆：","RUNWAY CLEAR · land: ")+ControlKeys.Label(ControlAction.AirLand):L.T("路径畅通 · 空格自动起飞","PATH CLEAR · Space auto takeoff")):preview.Reason;
                Widgets.Label(rect.ContractedBy(8f), KindLabel(Profile(v).kind)+" · " + state + (wingState==null?"":"\n"+wingState)+"\n" + L.T("离地 ", "AGL ") + (float.IsFinite(AerialRunway.Agl(v,r))?AerialRunway.Agl(v,r).ToString("0.0"):L.T("未知","unknown"))
                    + "  |  " + speed.ToString("0.0") + L.T(" 格/秒", " cells/s") + "\nV/S " + r.Motor.Velocity.Y.ToString("+0.0;-0.0;0.0")
                    + "  |  HDG " + r.Motor.Yaw.ToString("000") + "\n" + ControlKeys.Label(ControlAction.AirAscend) + L.T(" 上升 · ", " up · ")
                    + (Profile(v).kind==AerialFlightKind.FixedWing?L.T("PgUp/PgDn 节流 · ","PgUp/PgDn throttle · ")+ControlKeys.Label(ControlAction.AirLand)+L.T(" 自动着陆"," auto land"):ControlKeys.Label(ControlAction.AirDescend)+L.T(" 下降 · 双击左Ctrl着陆", " down · double left Ctrl to land")));
            }
            finally { GUI.color = old; Text.Font = font; }
        }
    }
}
