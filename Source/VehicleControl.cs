using System;
using System.Collections.Generic;
using System.Linq;
using IsoRim;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Crew stays natively boarded. A vehicle with no native operating roles can use an uncrewed camera session.
    internal static partial class VehicleControl
    {
        internal static Pawn Vehicle { get; private set; }
        internal static Pawn Crew { get; private set; }
        internal static object Seat { get; private set; }
        internal static VehicleView Mode => camera.Scoped ? VehicleView.Scoped : VehicleView.ThirdPerson;
        internal static bool On => Vehicle != null;
        internal static float Efficiency { get; private set; } = 1f;
        internal static bool Whole => On;
        internal static bool Scoped => On && Mode == VehicleView.Scoped;
        internal static bool Owns(Pawn v) => On && v == Vehicle;
        internal static bool Uncrewed => On && Crew == null && VehicleApi.Uncrewed(Vehicle);
        internal static bool OperatorCapable(bool weapon) => On && (Crew != null
            ? VehicleApi.OperatorCapable(Crew, weapon) : VehicleApi.Uncrewed(Vehicle));
        internal static bool FullControl(Pawn v) => Owns(v) && Whole && OperatorCapable(false);
        internal static bool Captured => On && capture.Captured && AllowsInput();
        // GUI input can arrive before this frame's Update. Reserve it from capture intent, not the previous frame.
        internal static bool OwnsMapInput => On && capture.WantsCapture && AllowsInput();
        internal static bool CanFire => On && VehicleRules.CanShoot(Mode, VehicleWeapons.Current != null && (AerialArmament.Trial(VehicleWeapons.Current) || VehicleApi.HasWeapon(Seat, VehicleWeapons.Current)),
            OperatorCapable(true));
        internal static string FireBlockReason => !On ? null : Crew!=null&&Crew.WorkTagIsDisabled(WorkTags.Violent)
            ? Crew.LabelShort + L.T("无法从事暴力，不能操作武器；请换可战斗乘员"," cannot perform violence; use a combat-capable crew member")
            : !OperatorCapable(true) ? (Crew?.LabelShort ?? Vehicle.LabelShort) + L.T("当前无法操作武器"," is currently unable to operate weapons") : null;
        private static readonly CapturePolicy capture = new CapturePolicy();
        private static Game game;
        private static Map map;
        private static bool old3D, oldVisible, oldDrafted, oldReverse;
        private static CursorLockMode oldLock;
        private static int enterFrame, lastCrewTick;
        private static bool triggerArmed, machineTriggerArmed, ownsFov;
        private static float parachuteHeldSince = -1f;
        internal static Func<Pawn, Pawn, bool> Parachute;
        internal static bool Descending(Pawn pawn) => AerialFlight.Airborne(pawn) && !VehicleApi.IsVehicle(pawn);
        internal static void BindParachutes(Func<Pawn, Pawn, bool> jump) => Parachute = jump;
        private static bool scoped => camera.Scoped;
        private static readonly VehicleCameraCycle camera = new VehicleCameraCycle();
        private static List<object> cameraRoles = new List<object>();
        internal static bool FollowHull => !scoped && (camera.Kind == VehicleCrewCamera.Driver
            || (camera.Kind == VehicleCrewCamera.Shoulder && (Uncrewed || VehicleApi.Movement(Seat))));
        internal static bool FixedScope => Scoped && AerialMounts.Fixed(Vehicle, VehicleWeapons.Current);
        // Whole-vehicle possession may select a gunner/passenger as its capable
        // operator. The actual native pilot still owns flight; the shoulder
        // camera must not lose mouse steering because of that selected seat.
        internal static bool PilotLook => FollowHull || FixedScope || (!scoped && camera.Kind == VehicleCrewCamera.Shoulder
            && On && AerialFlight.Supports(Vehicle) && AerialCrew.HasPilot(Vehicle));
        internal static bool DriverCamera => On && !scoped && camera.Kind==VehicleCrewCamera.Driver;
        internal static bool ExternalFollow => On && !scoped && camera.Kind == VehicleCrewCamera.Shoulder;
        private static float baseFov, zoom = 3f;
        private static readonly Dictionary<object, float> opticZooms = new Dictionary<object, float>();
        private static object opticWeapon;
        private static VehicleScopeTemplate opticTemplate;
        private static IntVec3 moveTarget = IntVec3.Invalid;
        private static Pawn looked;
        private static List<object> seats = new List<object>();
        private static int lookFrame = -1, seatIndex, wheelFrame = -1;

        internal static Pawn Looked(Pawn pawn)
        {
            if (!VehicleApi.Ready || On || pawn?.Map == null || TurretControl.Manned || WorkControl.Auto || WeaponView.ScopeLatched || ControlKeys.Held(ControlAction.Crouch)) return null;
            if (lookFrame == Time.frameCount) return looked;
            lookFrame = Time.frameCount;
            Pawn previous = looked; looked = null;
            V3 eye = Ballistics.Eye(pawn), direction = HitMath.Direction(FirstPerson.Yaw, FirstPerson.Pitch);
            float best = float.MaxValue;
            foreach (Pawn candidate in pawn.Map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer))
            {
                if (!VehicleApi.IsVehicle(candidate) || candidate.Destroyed || candidate.Dead || !(VehicleApi.Land(candidate) || VehicleApi.Sea(candidate) || AerialFlight.Supports(candidate))) continue;
                IntVec3 edge = candidate.OccupiedRect().ClosestCellTo(pawn.Position);
                if ((edge - pawn.Position).LengthHorizontal > WorkControl.Range || candidate.Position.Fogged(pawn.Map)) continue;
                if (FloorSpace.Band(pawn.Map, pawn.Position).Level != FloorSpace.Band(pawn.Map, candidate.Position).Level) continue;
                V3 center = FloorSpace.Spot(candidate); center.Y += VehicleModels.Profile(candidate).hullHeight * .6f;
                V3 delta = center - eye;
                float distance = delta.Length;
                if (distance < .01f || V3.Dot(direction, delta * (1f / distance)) < .55f) continue;
                // Reject boarding through intervening walls, even inside the two-cell interaction radius.
                V3 near = new V3(edge.x + .5f, eye.Y, edge.z - FloorSpace.Band(pawn.Map, edge).Shift + .5f);
                if (Ballistics.SightContact(pawn, eye, near, out V3 hit, false) && (hit - eye).Length + .2f < (near - eye).Length) continue;
                if (distance < best) { best = distance; looked = candidate; }
            }
            if (looked != previous) seatIndex = 0;
            seats = looked == null ? new List<object>() : VehicleApi.Handlers(looked);
            seatIndex = Math.Clamp(seatIndex, 0, Math.Max(0, seats.Count - 1));
            return looked;
        }
        internal static bool Wheel(Pawn pawn, float delta)
        {
            if (Looked(pawn) == null || seats.Count == 0) return false;
            if (wheelFrame != Time.frameCount && Math.Abs(delta) > .01f)
            { seatIndex = VehicleRules.Cycle(seatIndex, seats.Count, delta < 0 ? 1 : -1); wheelFrame = Time.frameCount; }
            return true;
        }
        internal static bool TryBoard(Pawn pawn)
        {
            Pawn v = Looked(pawn);
            if (v == null) return false;
            if (seats.Count == 0) { Reject(L.T("该载具没有成员席位。", "This vehicle has no crew seats.")); return true; }
            object h = seats[seatIndex];
            if (!VehicleApi.Available(h, pawn)) { Reject(L.T("席位已满、已被预留，或该人物无法担任此职位。", "Seat full, reserved, or this pawn cannot perform the role.")); return true; }
            Map footMap = pawn.Map; IntVec3 footCell = pawn.Position;
            if (!Bridge.ReleaseForVehicle()) { Reject(L.T("请先退出外部接管，再由第一人称上车。", "Leave the external possession before boarding from first person.")); return true; }
            try
            {
                if (!VehicleApi.Board(v, pawn, h)) { RecoverBoard(pawn, footMap, footCell); Reject(L.T("上车失败，席位状态已改变。", "Boarding failed: the seat changed.")); Bridge.ReturnFromVehicle(pawn); return true; }
                Start(v, pawn, h);
            }
            catch (Exception e)
            {
                End();
                RecoverBoard(pawn, footMap, footCell);
                // Native ownership remains authoritative: do not spawn a pawn that is already inside a holder.
                if (pawn.Spawned) Bridge.ReturnFromVehicle(pawn);
                Log.ErrorOnce("[FpsBridge] Vehicle boarding failed: " + e, 19271801);
            }
            return true;
        }
        private static void RecoverBoard(Pawn pawn, Map footMap, IntVec3 footCell)
        {
            // TryAddPawn despawns before transferring. Recover only an unowned pawn after a failed native transfer.
            if (!pawn.Spawned && !pawn.Destroyed && !pawn.Dead && pawn.ParentHolder == null && footMap != null)
                GenSpawn.Spawn(pawn, footCell, footMap, WipeMode.Vanish);
        }
        internal static bool TrySelected()
        {
            if (!VehicleApi.Ready || !(Find.Selector.SingleSelectedThing is Pawn v) || !VehicleApi.IsVehicle(v)) return false;
            if (v.Faction != Faction.OfPlayer || !v.Spawned || v.Position.Fogged(v.Map)) return true;
            if (!(VehicleApi.Land(v) || VehicleApi.Sea(v) || AerialFlight.Supports(v)))
            {
                Reject(L.T("该机型缺少飞行控制配置。", "This aircraft has no flight control profile."));
                return true;
            }
            var occupants = VehicleApi.Handlers(v).SelectMany(h => VehicleApi.List(h, "thingOwner").OfType<Pawn>()
                .Where(p => p.Faction == Faction.OfPlayer && !p.Dead && !p.Downed && !p.InMentalState && p.RaceProps.Humanlike)
                .Select(p => new { pawn = p, handler = h }));
            var chosen = VehicleCrewSelection.Choose(occupants,
                c => VehicleApi.OperatorCapable(c.pawn, false), c => VehicleApi.OperatorCapable(c.pawn, true));
            if (chosen != null) { Start(v, chosen.pawn, chosen.handler); return true; }
            if (VehicleApi.Uncrewed(v)) { Start(v, null, null); return true; }
            Reject(L.T("先让一名己方人物上车，再进入载具视角。", "Board an own pawn before entering the vehicle view."));
            return true;
        }
        private static void Start(Pawn v, Pawn p, object h)
        {
            if (On || Bridge.Active || FirstPerson.On || RemoteTurret.On || Bindings.Avatar != null) return;
            if (p == null ? h != null || !VehicleApi.Uncrewed(v) : !VehicleApi.Occupant(h, p)) return;
            if (!v.Spawned || v.Map != Find.CurrentMap) return;
            old3D = View3D.Active; oldLock = Cursor.lockState; oldVisible = Cursor.visible;
            oldDrafted = VehicleApi.Drafted(v); oldReverse = VehicleApi.Bool(v, "Reverse");
            Vehicle = v; Crew = p; Seat = h; game = Current.Game; map = v.Map;
            VehicleHud.Expanded = false;
            VehicleDriving.Begin(v);
            camera.Reset(); RefreshCameraRoles(); enterFrame = Time.frameCount; lastCrewTick = -1;
            triggerArmed = machineTriggerArmed = false; moveTarget = IntVec3.Invalid;
            opticZooms.Clear(); opticWeapon = null;
            capture.Enter(); View3D.Active = true;
            try
            {
                WeaponView.Release(); AimMotion.Release(); WeaponLight.Release(); FireControl.CancelInput();
                VehicleApi.Stop(v); VehicleApi.ClearQueue(v);
                VehicleWeapons.Reset(); VehicleWeapons.Select(0);
                Efficiency = VehicleApi.Efficiency(v);
                if(AerialFlight.TryGet(v,out var airborne)&&airborne.Airborne)SetLook(airborne.Motor.Yaw,airborne.Motor.Pitch);
                else SetLook(VehicleApi.Yaw(v), 5f);
                DubsMinimap.Begin();
                Find.Selector.ClearSelection();
                Find.CameraDriver.JumpToCurrentMapLoc(v.DrawPos);
                FirstPersonTransition.Begin(v);
                BeginVehicleDiagnostics();
                Log.Message("[FpsBridge] VEHICLE_ON vehicle=" + v.ThingID + "; crew=" + (p?.ThingID ?? "uncrewed") + "; role=" + VehicleApi.RoleKey(h) + "; mode=ThirdPerson");
            }
            catch { End(); throw; }
        }
        internal static void TacticalCamera()
        {
            camera.Next(cameraRoles.Count); // Driver view, while keeping all native occupants in their seats.
            SetLook(AerialFlight.TryGet(Vehicle,out var r)?r.Motor.Yaw:VehicleApi.Yaw(Vehicle),0);
        }
        private static bool AllowsInput() => InputBlockReason == null;
        internal static void Frame()
        {
            if (!On || FirstPersonTransition.Active) return;
            try
            {
                if (Current.Game != game || Current.ProgramState != ProgramState.Playing || Find.CurrentMap != map || !View3D.Active
                    || !Vehicle.Spawned || Vehicle.Destroyed || Vehicle.Dead || Vehicle.Faction != Faction.OfPlayer || Vehicle.Map != map
                    || (Crew == null ? !VehicleApi.Uncrewed(Vehicle)
                        : Crew.Dead || Crew.Downed || Crew.InMentalState || Crew.Faction != Faction.OfPlayer)
                    || FirstPerson.On || Bridge.Active)
                { End(); return; }
                object actualSeat = VehicleApi.Handlers(Vehicle).FirstOrDefault(h => VehicleApi.Occupant(h, Crew));
                if (Crew != null && actualSeat == null) { End(); return; }
                if (!ReferenceEquals(actualSeat, Seat)) { Seat = actualSeat; VehicleWeapons.Select(0); }
                if (Time.frameCount <= enterFrame) return;
                bool allows = AllowsInput();
                if (allows && ControlKeys.Down(ControlAction.ExitRecall)) { End(); return; }
                capture.Step(allows, ControlKeys.Down(ControlAction.Capture), Input.GetKeyDown(KeyCode.Escape));
                bool input = Captured;
                ApplyCursor();
                if (allows && ControlKeys.Down(ControlAction.CockpitStatus)) VehicleHud.Expanded = !VehicleHud.Expanded;
                if (input) Bridge.GameSpeedKeys();
                bool paused = Find.TickManager.Paused;
                bool simulation = VehicleRules.Simulation(input, paused, Application.isFocused, !allows || capture.SkipMouse);
                if (input && ControlKeys.FrameDown(ControlAction.Aim)) ToggleScope();
                if (input && ControlKeys.Down(ControlAction.ViewCycle)) NextCamera();
                bool airborne = AerialFlight.Airborne(Vehicle);
                if (input && !paused && ControlKeys.Down(ControlAction.ManualWork) && (!airborne || ExternalFollow))
                { Disembark(); return; }
                if (TryHoldParachute(simulation && airborne && !ExternalFollow && Crew != null,
                        ControlKeys.Held(ControlAction.AirParachute), Time.unscaledTime))
                { Jump(true); return; }
                int tick = Find.TickManager.TicksGame;
                if (tick != lastCrewTick) { Efficiency = VehicleApi.Efficiency(Vehicle); lastCrewTick = tick; }
                if (!input || paused || capture.SkipMouse) { triggerArmed = machineTriggerArmed = false; VehicleApi.Stop(Vehicle); moveTarget = IntVec3.Invalid; }
                else
                {
                    if (!ControlKeys.Held(ControlAction.Fire)) triggerArmed = true;
                    if (!ControlKeys.Held(ControlAction.VehicleMachineGun)) machineTriggerArmed = true;
                }
                if (simulation)
                {
                    if (ControlKeys.Down(ControlAction.NextWeapon)) { VehicleWeapons.Select(1); triggerArmed = false; }
                    if (ControlKeys.Down(ControlAction.Reload)) VehicleWeapons.Reload();
                    if (ControlKeys.Down(ControlAction.FireMode)) VehicleWeapons.CycleMode();
                    if (VehicleRules.CanDrive(Mode, VehicleApi.Movement(Seat)) && OperatorCapable(false)) Move(tick);
                    else { VehicleApi.Stop(Vehicle); moveTarget = IntVec3.Invalid; }
                }
                AerialHud.Layout(); Optics(input);
                if (input && !capture.SkipMouse)
                {
                    float dx = Input.GetAxisRaw("Mouse X"), dy = Input.GetAxisRaw("Mouse Y");
                    ApplyLookDelta(dx, dy);
                }
                AerialTargetLock.Frame();
                VehicleWeapons.Frame(simulation && triggerArmed, simulation && machineTriggerArmed, paused);
                if (scoped && VehicleWeapons.Current != null) SyncScopeLook();
                VehiclePrediction.Update();
                Find.CameraDriver.JumpToCurrentMapLoc(Vehicle.DrawPos);
                if (input && ControlKeys.Held(ControlAction.Overlay))
                {
                    try { HitboxView.Draw(Vehicle); }
                    catch (Exception e) { Log.ErrorOnce("[FpsBridge] Vehicle hit overlay: " + e, 19271826); }
                }
            }
            catch (Exception e) { End(); Log.ErrorOnce("[FpsBridge] Vehicle control stopped: " + e, 19271802); }
        }
        private static void Move(int tick)
        {
            int forward = (ControlKeys.Held(ControlAction.Forward) ? 1 : 0) - (ControlKeys.Held(ControlAction.Backward) ? 1 : 0);
            int side = (ControlKeys.Held(ControlAction.Right) ? 1 : 0) - (ControlKeys.Held(ControlAction.Left) ? 1 : 0);
            VehicleDriving.Drive(Vehicle, tick, forward, side);
        }
        internal static void MouseDown(KeyCode key)
        {
            if (key >= KeyCode.Mouse0 && key <= KeyCode.Mouse2 && Captured && !capture.SkipMouse && ControlKeys.Matches(ControlAction.Aim, key)) ToggleScope();
        }
        internal static void SyncScopeLook()
        {
            V3 direction = VehicleWeapons.ScopeDirection;
            float yaw = Mathf.Atan2(direction.X, direction.Z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Atan2(direction.Y, new Vector2(direction.X, direction.Z).magnitude) * Mathf.Rad2Deg;
            SetLook(yaw, pitch);
        }
        private static void ToggleScope()
        {
            if (!camera.ToggleScope(VehicleWeapons.Current != null && CanFire)) return;
            if(FixedScope && AerialFlight.TryGet(Vehicle,out var flight)) {flight.AimYaw=flight.Motor.Yaw;flight.AimPitch=flight.Motor.Pitch;flight.PilotAim=true;SetLook(flight.AimYaw,flight.AimPitch);}
            triggerArmed = machineTriggerArmed = false;
            VehicleWeapons.LeaveScope();
            Log.Message("[FpsBridge] VIEW_TOGGLE mode=" + CameraLabel);
        }
        private static bool Commander(object handler)
        {
            string key = VehicleApi.RoleKey(handler) ?? "";
            string label = VehicleApi.RoleLabel(handler) ?? "";
            return key.IndexOf("commander", StringComparison.OrdinalIgnoreCase) >= 0
                || label.IndexOf("commander", StringComparison.OrdinalIgnoreCase) >= 0 || label.Contains("车长") || label.Contains("車長");
        }
        private static void RefreshCameraRoles()
        {
            cameraRoles = VehicleApi.Handlers(Vehicle).Where(h => !VehicleApi.Movement(h) && !Commander(h)).ToList();
            camera.Validate(cameraRoles.Count);
        }
        private static void NextCamera()
        {
            RefreshCameraRoles(); camera.Next(cameraRoles.Count);
            triggerArmed = machineTriggerArmed = false; VehicleWeapons.LeaveScope(); RestoreFov();
            if(AerialFlight.TryGet(Vehicle,out var flight)&&flight.Airborne&&PilotLook)SetLook(flight.Motor.Yaw,flight.Motor.Pitch);
            else if(camera.Kind==VehicleCrewCamera.Driver&&!AerialFlight.Airborne(Vehicle))SetLook(VehicleApi.Yaw(Vehicle),5f);
            Log.Message("[FpsBridge] VEHICLE_CAMERA view=" + CameraLabel);
        }
        private static object CameraRole => camera.Kind == VehicleCrewCamera.Driver
            ? VehicleApi.Handlers(Vehicle).FirstOrDefault(VehicleApi.Movement)
            : camera.Kind == VehicleCrewCamera.Commander ? VehicleApi.Handlers(Vehicle).FirstOrDefault(Commander)
            : camera.Kind == VehicleCrewCamera.Role && camera.RoleIndex >= 0 && camera.RoleIndex < cameraRoles.Count ? cameraRoles[camera.RoleIndex] : null;
        internal static string CameraLabel => scoped ? L.T("炮手瞄准镜", "Gunner optic")
            : camera.Kind == VehicleCrewCamera.Shoulder ? L.T("越肩视角", "Over the shoulder")
            : camera.Kind == VehicleCrewCamera.Driver ? L.T("驾驶员视角", "Driver view")
            : camera.Kind == VehicleCrewCamera.Commander ? L.T("车长视角", "Commander view")
            : (CameraRole == null ? L.T("乘员视角", "Crew view") : VehicleApi.RoleLabel(CameraRole));
        internal static void ApplyCursor()
        {
            if (!On) return;
            bool locked = Captured;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
        private static void Optics(bool input)
        {
            if (Mode != VehicleView.Scoped || VehicleWeapons.Current == null) { RestoreFov(); return; }
            if (!ownsFov) { baseFov = IsoRimMod.Settings.fpFov; ownsFov = true; }
            object weapon = VehicleWeapons.Current;
            VehicleScopeTemplate template = VehicleWeapons.Optic;
            if (!ReferenceEquals(opticWeapon, weapon) || opticTemplate != template)
            {
                zoom = opticZooms.TryGetValue(weapon, out float remembered) ? remembered : VehicleScopeRules.DefaultZoom(template);
                opticWeapon = weapon; opticTemplate = template;
            }
            zoom = ScopeRules.Zoom(zoom, input ? Input.mouseScrollDelta.y : 0f, baseFov, VehicleScopeRules.Style(template));
            opticZooms[weapon] = zoom;
            IsoRimMod.Settings.fpFov = 2f * Mathf.Atan(Mathf.Tan(baseFov * .5f * Mathf.Deg2Rad) / zoom) * Mathf.Rad2Deg;
        }
        private static void RestoreFov() { if (ownsFov && IsoRimMod.Settings != null) IsoRimMod.Settings.fpFov = baseFov; ownsFov = false; }
        internal static Vector3 Eye()
        {
            if (!On) return FirstPerson.Eye;
            if (Mode == VehicleView.Scoped && VehicleWeapons.Current != null) return VehicleScopeCamera.Eye(Vehicle, VehicleWeapons.Current);
            if (camera.Kind != VehicleCrewCamera.Shoulder)
            {
                Vector3 authoredEye = VehicleModels.CrewEye(Vehicle, CameraRole, camera.Kind == VehicleCrewCamera.Driver);
                return camera.Kind == VehicleCrewCamera.Driver ? VehicleDriverCamera.Resolve(Vehicle, authoredEye) : authoredEye;
            }
            VehicleViewDef profile = VehicleModels.Profile(Vehicle);
            Vector3 local = VehicleModels.Seat(Vehicle, Seat)?.eye ?? (VehicleApi.Movement(Seat) ? profile.driverEye : profile.commanderEye);
            Vector3 eye = VehicleModels.World(Vehicle, local);
            Vector3 forward = Quaternion.Euler(FirstPerson.Pitch, FirstPerson.Yaw, 0f) * Vector3.forward;
            Vector3 wanted = eye - forward * profile.shoulderDistance + Vector3.up * 1.5f;
            float ceiling = VehicleFollowVisibility.Ceiling(Vehicle);
            if (float.IsFinite(ceiling))
            {
                eye.y = Math.Min(eye.y, ceiling - .25f);
                wanted = eye - forward * profile.shoulderDistance + Vector3.up * 1.5f;
                wanted.y = Math.Min(wanted.y, ceiling - .2f);
            }
            if (Ballistics.SightContact(Vehicle, VehicleModels.V(eye), VehicleModels.V(wanted), out V3 hit, false))
            {
                float distance = (VehicleModels.U(hit) - eye).magnitude;
                bool roofAtEye = distance < .25f && RoofSegmentContact.Try(map, VehicleModels.V(eye), VehicleModels.V(wanted),
                    Math.Min(1, .3f / Math.Max(.001f, (wanted - eye).magnitude)), out _, out _);
                if (!roofAtEye) wanted = eye + (wanted - eye).normalized * Mathf.Max(0f, distance - .2f);
            }
            wanted.x = Mathf.Clamp(wanted.x, .1f, map.Size.x - .1f);
            wanted.z = Mathf.Clamp(wanted.z, .1f, FloorSpace.Bands(map)[0].Rows - .1f);
            wanted.y = Mathf.Max(.2f, wanted.y);
            return wanted;
        }
        internal static void SetLook(float yaw, float pitch)
        { Bindings.SetHeading(yaw); Bindings.SetLookYaw(0f); Bindings.SetHeadingReady(true); FirstPerson.Look(0f, pitch - FirstPerson.Pitch); }
        internal static void ApplyLookDelta(float dx, float dy)
        {
            if (!float.IsFinite(dx) || !float.IsFinite(dy)) return;
            float sens = FpsLook.Scale(scoped ? 1f / zoom : 1f, scoped);
            float pitchDelta = dy * sens * (IsoRimMod.Settings.invertY ? 1f : -1f);
            if (scoped) VehicleWeapons.ScopeLook(dx * sens, pitchDelta);
            else { VehicleWeapons.LeaveScope(); FirstPerson.Look(dx * sens, pitchDelta); }
        }
        private static void Disembark()
        {
            Pawn v = Vehicle, p = Crew;
            if (p == null) { End(); return; }
            if (AerialFlight.Airborne(v)) { Jump(false); return; }
            VehicleApi.Stop(v);
            VehicleApi.Call(v, "DisembarkPawn", p);
            if (!p.Spawned) { Reject(L.T("暂时无法安全下车。", "Cannot disembark safely right now.")); return; }
            End(); Bridge.ReturnFromVehicle(p);
        }
        internal static bool TryHoldParachute(bool allowed, bool held, float now)
        {
            if (!allowed || !held) { parachuteHeldSince = -1f; return false; }
            if (parachuteHeldSince < 0f) parachuteHeldSince = now;
            if (now - parachuteHeldSince < 3f) return false;
            parachuteHeldSince = -1f;
            return true;
        }

        internal static bool Jump(bool firstPerson)
        {
            Pawn p = Crew;
            float heading = FirstPerson.Yaw;
            if (p == null || Parachute?.Invoke(Vehicle, p) != true)
            {
                Reject(L.T("当前无法跳伞。", "Cannot parachute right now."));
                return false;
            }
            End();
            if (firstPerson) Bridge.ReturnFromVehicle(p, heading);
            else
            {
                Find.Selector.ClearSelection();
                Find.Selector.Select(p, false, false);
                Find.CameraDriver.JumpToCurrentMapLoc(VehicleModels.U(FloorSpace.Spot(p)));
            }
            return true;
        }

        internal static void End()
        {
            FirstPersonTransition.Reset();
            parachuteHeldSince = -1f;
            if (!On) return;
            EndVehicleDiagnostics();
            Pawn v = Vehicle;
            if(AerialFlight.TryGet(v,out var airborne)){airborne.PilotAim=false;AerialOrders.Hold(v,airborne);}
            try { VehicleAuxiliary.Restore(v); VehicleApi.Stop(v); VehicleApi.ClearQueue(v); if (v.Spawned && !v.Destroyed) { VehicleApi.Set(v, "Reverse", oldReverse); if (!AerialFlight.Airborne(v)) VehicleApi.Draft(v, oldDrafted); } }
            catch (Exception e) { Log.Warning("[FpsBridge] Vehicle cleanup: " + e.Message); }
            finally
            {
                AerialHud.Restore(); AerialTargetLock.Reset(); RestoreFov(); DubsMinimap.End(); VehiclePrediction.Clear(); VehicleWeapons.Reset(); VehicleDriving.End(); capture.Exit();
                HitboxView.EndVehicleInspection();
                opticZooms.Clear(); opticWeapon = null;
                Vehicle = null; Crew = null; Seat = null; game = null; map = null; camera.Reset(); cameraRoles.Clear();
                if(AerialFlight.TryGet(v,out var grounded))AerialHandoff.Release(v,grounded);
                Cursor.lockState = oldLock; Cursor.visible = oldVisible; View3D.Active = old3D;
                looked = null; lookFrame = -1;
                if (v.Spawned && v.Map == Find.CurrentMap) { Find.Selector.Select(v, false, false); Find.CameraDriver?.JumpToCurrentMapLoc(v.DrawPos); }
                Log.Message("[FpsBridge] VEHICLE_OFF " + v.ThingID);
            }
        }
        internal static void DrawPrompt(Pawn pawn)
        {
            if (Looked(pawn) == null || seats.Count == 0) return;
            string label = looked.LabelShort + L.T(" · 滚轮选择席位", " · wheel: choose seat");
            var rows = new List<string> { label };
            for (int i = 0; i < seats.Count; i++) rows.Add((i == seatIndex ? "> " : "  ") + VehicleApi.RoleLabel(seats[i])
                + " " + VehicleApi.List(seats[i], "thingOwner").Count() + "/" + VehicleApi.Int(VehicleApi.Role(seats[i]), "Slots")
                + (i == seatIndex ? " · " + ControlKeys.Label(ControlAction.InteractLeanRight) + L.T(" 上车", " board") : ""));
            Panel(string.Join("\n", rows), UI.screenHeight * .52f, Math.Min(320f, rows.Count * 23f + 12f));
        }
        public static void DrawGUI()
        {
            ObserveVehicleGui(false);
            if (FirstPersonTransition.Active) return;
            ApplyCursor(); // Perspective Shift restores a visible pointer when it has no pawn avatar.
            if (Event.current?.type != EventType.Repaint) return;
            try
            {
                if (!On)
                {
                    if (View3D.Active && !FirstPerson.On && !Bridge.Active && Find.Selector?.SingleSelectedThing is Pawn selected && VehicleApi.IsVehicle(selected)) VehicleHud.Draw(selected);
                    return;
                }
                // Essential control feedback must render before optional world overlays.
                VehicleHud.Draw();
                VehicleReloadHud.Draw();
                ObserveVehicleGui(true);
                IffMarkers.Draw(Vehicle);
                bool drawn = Mode == VehicleView.Scoped && VehicleWeapons.Current != null && VehicleScopeHud.Draw(zoom);
                if (!drawn) { float x = UI.screenWidth / 2f, y = UI.screenHeight / 2f; Widgets.DrawLineHorizontal(x - 7, y, 14); Widgets.DrawLineVertical(x, y - 7, 14); }
                AerialFlight.DrawHud();
                if (VehiclePrediction.Visible) VehiclePrediction.Draw();
                AerialTargetLock.Draw();
                if(!DubsMinimap.Active)PawnMinimap.Draw(Vehicle);
                if (Captured && ControlKeys.Held(ControlAction.Overlay)) HitboxView.DrawInspection(Vehicle);
            }
            catch (Exception e) { Log.ErrorOnce("[FpsBridge] Vehicle HUD: " + e, 19271803); }
        }
        private static void Panel(string text, float y, float height)
        {
            Rect r = new Rect(Mathf.Max(8f, UI.screenWidth / 2f - 350f), y, Mathf.Min(700f, UI.screenWidth - 16f), height);
            Color c = GUI.color; GameFont f = Text.Font; TextAnchor a = Text.Anchor;
            try { GUI.color = Color.white; Text.Font = GameFont.Small; Text.Anchor = TextAnchor.UpperLeft; Widgets.DrawBoxSolid(r, new Color(0f, 0f, 0f, .72f)); Widgets.Label(r.ContractedBy(6f), text); }
            finally { GUI.color = c; Text.Font = f; Text.Anchor = a; }
        }
        public static void Gizmos(Pawn __instance, ref IEnumerable<Gizmo> __result)
        { if (VehicleApi.Ready && __instance.Faction == Faction.OfPlayer) __result = WithGizmo(__result, __instance); }
        private static IEnumerable<Gizmo> WithGizmo(IEnumerable<Gizmo> original, Pawn vehicle)
        {
            foreach (Gizmo g in original) yield return g;
            foreach(Gizmo g in AerialMissileTrial.Commands(vehicle))yield return g;
            if(AerialFlight.Supports(vehicle)&&AerialFlight.Profile(vehicle).kind==AerialFlightKind.FixedWing)
                yield return AerialThrottle.Command(vehicle);
            if(!On)foreach(Gizmo g in AerialFireControl.Commands(vehicle))yield return g;
            if(AerialFlight.Supports(vehicle)&&AerialFlight.Airborne(vehicle))yield return new Command_Action{defaultLabel=L.T("自动着陆","Auto land"),defaultDesc=L.T("请求自动着陆；固定翼左Ctrl仅减速。","Request auto landing; fixed-wing LCtrl only brakes."),icon=TexCommand.Install,action=()=>{if(AerialFlight.TryGet(vehicle,out var r))AerialLanding.Request(vehicle,r);}};
            var command = new Command_Action { defaultLabel = L.T("载具第一人称", "Vehicle first person"), defaultDesc = L.T("进入已登车成员的席位视角，或接管原生无人载具。", "Enter a boarded crew member's seat view, or control a native uncrewed vehicle."), icon = vehicle.def.uiIcon,
                action = () => { Find.Selector.ClearSelection(); Find.Selector.Select(vehicle, false, false); TrySelected(); } };
            if (On || Bridge.Active || FirstPerson.On || RemoteTurret.On || Bindings.Avatar != null) command.Disable(L.T("请先退出当前控制。", "Leave current control first."));
            yield return command;
        }
        private static void Reject(string why) => Messages.Message(why, MessageTypeDefOf.RejectInput, false);
    }
}
