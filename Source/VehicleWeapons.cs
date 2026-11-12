using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class VehicleWeapons
    {
        internal static object Current { get; private set; }
        internal static object Firing { get; private set; }
        internal static ThingDef ProjectileFor(object gun)=>CeCompat.VehicleProjectile(gun,VehicleApi.Get(gun,"ProjectileDef") as ThingDef);
        internal static float Pitch { get; private set; }
        internal static float AimRange { get; private set; } = -1f;
        internal static VehicleScopeTemplate Optic
        {
            get
            {
                Pawn v = VehicleControl.Vehicle;
                if (v == null || Current == null) return VehicleScopeTemplate.ModernTank;
                Def def = VehicleApi.Get(Current, "def") as Def;
                ThingDef projectile = VehicleApi.Get(Current, "ProjectileDef") as ThingDef;
                return VehicleScopeRules.Select(VehicleModels.Weapon(v, Current).scopeTemplate, def?.defName,
                    projectile?.defName, (int)v.def.techLevel, VehicleApi.Float(Current, "MaxRange"), projectile?.projectile?.flyOverhead ?? false);
            }
        }
        private static int launches;
        [ThreadStatic] private static object manualReload, previousAmmo;
        private static string failure { get => Current == null ? null : StateOf(Current).Failure; set { if (Current != null) StateOf(Current).Failure=value; } }
        private static bool scopeAimReady;
        private static VehicleAimCommand scopeAim;
        private static Game clockGame;
        private static ConditionalWeakTable<object, VehicleGunState> states = new ConditionalWeakTable<object, VehicleGunState>();
        private static readonly HashSet<object> activeGuns = new HashSet<object>();
        private static VehicleGunState StateOf(object t) { activeGuns.Add(t); return states.GetValue(t, gun => new VehicleGunState(IsMachineGun(gun) || IsSelectable(gun) && VehicleApi.Bool(gun, "FullAuto"))); }
        internal static VehicleFireCycle CycleFor(object turret) => StateOf(turret).Cycle;
        internal static VehicleWeaponClock ClockFor(object turret) => StateOf(turret).Clock;
        internal static VehicleFireCycle Cycle => CycleFor(Current);
        private static bool IsSelectable(object t) => t != null && (AerialArmament.Data(t) is AerialWeaponExtension air?air.quickFire:VehicleFireCycle.Selectable((VehicleApi.Get(t, "def") as Def)?.defName + " " + (VehicleApi.Get(t, "ProjectileDef") as Def)?.defName, VehicleApi.Int(VehicleApi.Get(t, "def"), "magazineCapacity"), VehicleApi.Int(t, "TicksPerShot"), VehicleApi.Bool(t, "FullAuto")));
        internal static bool IsMachineGun(object t) => t != null && (AerialArmament.Trial(t)?AerialArmament.QuickGun(t):VehicleGunRules.MachineGun((VehicleApi.Get(t,"def") as Def)?.defName + " " + (VehicleApi.Get(t,"ProjectileDef") as Def)?.defName + " " + VehicleApi.TurretKey(t)));
        internal static List<object> MachineGuns() => Available().Where(IsMachineGun).ToList();
        internal static bool SelectableMode => IsSelectable(Current);
        internal static string ModeLabel => ModeLabelFor(Current);
        internal static string ModeLabelFor(object t) => t == null ? "—" : !IsSelectable(t) ? VehicleApi.Get(VehicleApi.Get(t, "CurrentFireMode"), "label") as string ?? L.T("单发", "Single")
            : CycleFor(t).Mode == VehicleManualFireMode.Single ? L.T("单发", "Single") : CycleFor(t).Mode == VehicleManualFireMode.Triple ? L.T("三连发", "3-round burst") : L.T("全自动", "Automatic");
        private static readonly Dictionary<object, float> pitches = new Dictionary<object, float>();
        internal static float PitchOf(object turret) => pitches.TryGetValue(turret, out float value) ? value : 0f;
        internal static void NativePitch(object gun,V3 direction)=>pitches[gun]=-Mathf.Atan2(direction.Y,new Vector2(direction.X,direction.Z).magnitude)*Mathf.Rad2Deg;
        internal static bool ManualGun(object gun) => ReferenceEquals(gun,Current)
            || IsMachineGun(gun)&&ControlKeys.Held(ControlAction.VehicleMachineGun);
        internal static V3 ScopeDirection => VehicleControl.FixedScope||!scopeAimReady||VehicleModels.Weapon(VehicleControl.Vehicle,Current).boreFixedToParent ? Direction(VehicleControl.Vehicle,Current)
            : HitMath.Direction(scopeAim.Yaw,scopeAim.Pitch);
        internal static V3 Direction(Pawn v,object turret) => AerialMounts.Fixed(v,turret) ? VehicleModels.V(AerialMounts.Rotation(v)*Vector3.forward) : VehicleAimAlignment.BoreDirection(v,turret);
        private static Vector3 Muzzle(Pawn vehicle, object turret) =>
            VehicleScopeCamera.TrySecondaryAnchor(vehicle, turret, false, out Vector3 point) ? point : VehicleModels.Muzzle(vehicle, turret);
        internal static V3 FlightMuzzle(Pawn vehicle, object turret)
        {
            Vector3 muzzle = Muzzle(vehicle, turret), mount = VehicleModels.Mount(vehicle, turret);
            return Ballistics.SightContact(vehicle, VehicleModels.V(mount), VehicleModels.V(muzzle), out V3 hit, false)
                ? hit - VehicleModels.V((muzzle - mount).normalized * .02f)
                : VehicleModels.V(muzzle);
        }
        internal static float NativeSpeed(object turret, ThingDef projectile)
        {
            if (CeCompat.NativeProjectile(projectile))
                return VehicleCeSpeed.ConfiguredSpeed(turret, projectile) / 60f;
            float speed = VehicleApi.Float(VehicleApi.Get(turret, "def"), "projectileSpeed");
            if(speed>0f)return speed/60f;
            float original=projectile?.projectile?.SpeedTilesPerTick??0f;
            // 1.7 ProjectileProperties.SpeedTilesPerTick uses 100 ticks per speed unit.
            return original>0f?original:VehicleCeSpeed.Charges(turret)/100f;
        }
        internal static VehicleWeaponClock Clock => ClockFor(Current);
        internal static void Reset()
        {
            VehicleGroupFire.CancelAll();
            foreach (object gun in activeGuns) if (states.TryGetValue(gun,out VehicleGunState state)) { state.Cycle.Interrupt(); state.AimPoint = null; }
            activeGuns.Clear();
            if (clockGame != Verse.Current.Game) { states = new ConditionalWeakTable<object, VehicleGunState>(); clockGame = Verse.Current.Game; }
            Current = Firing = null; Pitch = 0f; AimRange = -1f; scopeAimReady = false; pitches.Clear();
        }
        internal static void ScopeLook(float dx, float dy)
        {
            if (Current == null) return;
            if(VehicleControl.FixedScope && AerialFlight.TryGet(VehicleControl.Vehicle,out var flight))
            {flight.AimYaw=Mathf.Repeat(flight.AimYaw+dx,360f);flight.AimPitch=Mathf.Clamp(flight.AimPitch+dy,-75f,75f);scopeAim=new VehicleAimCommand(flight.AimYaw,flight.AimPitch);scopeAimReady=true;return;}
            if(VehicleModels.Weapon(VehicleControl.Vehicle,Current).boreFixedToParent)
            {
                V3 bore=Direction(VehicleControl.Vehicle,Current);
                float yaw=Mathf.Atan2(bore.X,bore.Z)*Mathf.Rad2Deg;
                float pitch=-Mathf.Atan2(bore.Y,new Vector2(bore.X,bore.Z).magnitude)*Mathf.Rad2Deg;
                if(!scopeAimReady){scopeAim=new VehicleAimCommand(yaw,pitch);scopeAimReady=true;}
                scopeAim=scopeAim.Look(dx,0,pitch,pitch);
                return;
            }
            object aimAxis=VehicleAimAlignment.BoreParent(VehicleControl.Vehicle,Current)??Current;
            if (!scopeAimReady) { scopeAim = new VehicleAimCommand(VehicleAimAlignment.TurretYaw(VehicleControl.Vehicle, aimAxis), PitchOf(aimAxis)); scopeAimReady = true; }
            VehicleWeaponView profile = VehicleModels.Weapon(VehicleControl.Vehicle, aimAxis);
            scopeAim = scopeAim.Look(dx, dy, profile.minPitch, profile.maxPitch);
        }
        internal static void LeaveScope() => scopeAimReady = false;
        private static void KeepTimerRunning()
        {
            foreach (object turret in VehicleApi.Turrets(VehicleControl.Vehicle))
                if (VehicleApi.Int(turret, "ReloadTicks") > 0 || VehicleApi.Int(turret, "burstTicks") > 0 || VehicleApi.Float(turret, "currentHeatRate") > 0f || VehicleApi.Bool(turret, "OnCooldown"))
                    VehicleApi.Call(turret, "StartTicking");
        }
        internal static List<object> Available()
        {
            Pawn v = VehicleControl.Vehicle;
            if (v == null) return new List<object>();
            return VehicleApi.Turrets(v).Where(t => VehicleControl.Whole || AerialArmament.Trial(t) || VehicleApi.HasWeapon(VehicleControl.Seat, t)).ToList();
        }
        internal static void Select(int direction)
        {
            VehicleGroupFire.CancelAll();
            if (Current != null && !(IsMachineGun(Current) && ControlKeys.Held(ControlAction.VehicleMachineGun))) Cycle.Interrupt();
            List<object> list = Available();
            int index = list.IndexOf(Current);
            if (direction == 0)
            {
                string preferred = VehicleModels.Seat(VehicleControl.Vehicle, VehicleControl.Seat)?.turretKey;
                index = preferred == null ? 0 : Math.Max(0, list.FindIndex(t => VehicleApi.TurretKey(t) == preferred));
            }
            else index = VehicleRules.Cycle(index, list.Count, direction);
            Current = list.Count == 0 ? null : list[Math.Clamp(index, 0, list.Count - 1)];
            failure = null; AimRange = -1f;
            if (Current != null && !(IsMachineGun(Current) && ControlKeys.Held(ControlAction.VehicleMachineGun))) Cycle.Interrupt(); scopeAimReady = false;
            Pitch = Current == null ? 0f : PitchOf(Current);
            if (VehicleGroupFire.Supports(Current)) VehicleGroupFire.ClearTarget(Current);
            else if (Current != null) VehicleApi.Call(Current, "SetTarget", LocalTargetInfo.Invalid);
        }
        internal static void Reload()
        {
            if (!VehicleControl.CanFire) return;
            foreach (object gun in Available()) Reload(gun);
            KeepTimerRunning();
        }
        private static void Reload(object gun)
        {
            object definition = VehicleApi.Get(gun, "def");
            if (VehicleApi.Int(gun,"ReloadTicks")>0 || VehicleApi.Get(definition, "ammunition") == null
                || VehicleApi.Int(gun, "shellCount") >= VehicleApi.Int(definition, "magazineCapacity")) return;
            StateOf(gun).ReloadStarted(Find.TickManager.TicksGame);
            object oldManual = manualReload, oldAmmo = previousAmmo;
            manualReload = gun; previousAmmo = VehicleApi.Get(gun, "loadedAmmo");
            try { VehicleApi.Call(gun,"AutoReload"); }
            finally { manualReload = oldManual; previousAmmo = oldAmmo; }
        }
        public static void BeforeReload(object __instance, out int __state) => __state = VehicleApi.Int(__instance, "shellCount");
        public static void AfterReload(object __instance, bool __result, int __state)
        {
            if (__result && VehicleControl.Owns(VehicleApi.Get(__instance, "vehicle") as Pawn) && VehicleApi.Int(__instance, "shellCount") > 0)
            {
                // This native AutoReload writes only the newly supplied count to shellCount.
                // Preserve rounds already in the magazine for our manual top-up, with the same ammo.
                if (ReferenceEquals(manualReload, __instance) && __state > 0)
                    VehicleApi.Set(__instance, "shellCount", VehicleGunRules.RefilledCount(__state, VehicleApi.Int(__instance, "shellCount"),
                        VehicleApi.Int(VehicleApi.Get(__instance, "def"), "magazineCapacity"), previousAmmo != null && previousAmmo == VehicleApi.Get(__instance, "loadedAmmo")));
                CycleFor(__instance).Interrupt();
                if (!VehicleGroupFire.Guiding(__instance))
                {
                    VehicleGroupFire.Cancel(__instance);
                    if (VehicleGroupFire.Supports(__instance)) VehicleGroupFire.ClearTarget(__instance);
                    else VehicleApi.Call(__instance, "SetTarget", LocalTargetInfo.Invalid);
                }
                VehicleApi.Call(__instance, "ActivateTimer", false);
                VehicleApi.Call(__instance, "StartTicking");
            }
        }
        internal static void CycleMode()
        {
            if (Current == null) return;
            if (SelectableMode) Cycle.NextMode();
            else { Cycle.Interrupt(); VehicleApi.Call(Current, "CycleFireMode"); }
        }
        internal static string StateLabel => Current == null ? L.T("无武器", "No weapon") : VehicleControl.FireBlockReason ?? failure
            ?? (VehicleApi.Bool(Current, "OnCooldown") ? L.T("过热", "Overheated")
                : VehicleApi.Int(Current, "ReloadTicks") > 0 ? L.T("装填 ", "Loading ") + VehicleWeaponClock.Seconds(VehicleApi.Int(Current, "ReloadTicks"), VehicleControl.Efficiency).ToString("0.0") + "s"
                : !VehicleApi.Bool(Current, "HasAmmo") ? L.T("无弹药", "No ammunition")
                : VehicleApi.Bool(Current, "TurretDisabled") ? L.T("不可用", "Unavailable")
                : Clock.Remaining(Find.TickManager.TicksGame) > 0 ? L.T("射击循环 ", "Cycling ") + (Clock.Remaining(Find.TickManager.TicksGame) / 60f).ToString("0.0") + "s"
                : Cycle.FreshPressRequired ? L.T("就绪 · 松开后重新点击开火", "Ready · release and click to fire") : L.T("就绪", "Ready"));
        internal static string Status
        {
            get
            {
                if (Current == null) return L.T("此席位没有可操作的武器", "No controllable weapon at this seat");
                Def def = VehicleApi.Get(Current, "def") as Def;
                return (def?.LabelCap.ToString() ?? VehicleApi.TurretKey(Current)) + " · " + ModeLabel
                    + " · " + L.T("弹药 ", "ammo ") + VehicleApi.Int(Current, "shellCount") + " · "
                    + (failure ?? (VehicleApi.Bool(Current, "OnCooldown") ? L.T("过热", "overheated") : VehicleApi.Int(Current, "ReloadTicks") > 0 ? L.T("装填中", "reloading")
                        : !VehicleApi.Bool(Current, "HasAmmo") ? L.T("无弹药", "no ammunition")
                        : VehicleApi.Bool(Current, "TurretDisabled") ? L.T("不可用", "unavailable")
                        : Clock.Remaining(Find.TickManager.TicksGame) > 0 ? L.T("射击循环", "cycling") : L.T("就绪", "ready")))
                    +(AerialTargetLock.Hint==null?"":" · "+AerialTargetLock.Hint);
            }
        }
        internal static void Frame(bool fireInput, bool machineInput, bool paused)
        {
            KeepTimerRunning(); // Native cooldown/reload/recoil ticks remain authoritative, even without an AI target.
            Pawn v = VehicleControl.Vehicle;
            List<object> guns=Available();
            if (!guns.Contains(Current)) Select(0);
            VehicleAuxiliary.Frame();
            if (Current==null) return;
            // Keep a free look for drivers/passengers; only an armed seat or whole-vehicle control lays the gun.
            if (VehicleControl.CanFire && !paused && VehicleControl.Captured)
            {
                if (VehicleControl.Scoped) ScopeLook(0f, 0f); else LeaveScope();
                Vector3 eye = VehicleControl.Eye();
                float viewYaw = VehicleControl.Scoped ? scopeAim.Yaw : FirstPerson.Yaw;
                float viewPitch = VehicleControl.Scoped ? scopeAim.Pitch : FirstPerson.Pitch;
                V3 start = VehicleModels.V(eye), far = start + HitMath.Direction(viewYaw, viewPitch) * 180f;
                AimRange = -1f;
                if (Ballistics.SightContact(v, start, far, out V3 contact, true)) { far = contact; AimRange = (contact - start).Length; }
                foreach(object gun in guns)
                    if(ManualGun(gun)&&!VehicleApi.Bool(gun,"ComponentDisabled")) AimGun(v,gun,far,gun==Current);
                V3 actualBore=Direction(v,Current);
                Pitch=-Mathf.Atan2(actualBore.Y,new Vector2(actualBore.X,actualBore.Z).magnitude)*Mathf.Rad2Deg;
            }
            foreach(object gun in guns)
            {
                bool primary=gun==Current&&fireInput, machine=IsMachineGun(gun)&&machineInput;
                bool pressed=primary&&ControlKeys.Down(ControlAction.Fire)||machine&&ControlKeys.Down(ControlAction.VehicleMachineGun);
                bool held=primary&&ControlKeys.Held(ControlAction.Fire)||machine&&ControlKeys.Held(ControlAction.VehicleMachineGun);
                FrameGun(gun,(primary||machine)&&VehicleControl.CanFire,pressed,held,paused);
            }
        }
        private static void AimGun(Pawn vehicle,object gun,V3 far,bool selected)
        {
            if (VehicleGroupFire.Guiding(gun)) return;
            if (VehicleGroupFire.Supports(gun)) StateOf(gun).AimPoint = far;
            VehicleWeaponView profile=VehicleModels.Weapon(vehicle,gun);
            if(profile.boreFixedToParent)
            {
                object fixedParent=VehicleAimAlignment.FixedParent(vehicle,gun);
                if(fixedParent!=null)
                {
                    Vector3 direction=VehicleModels.U(far)-VehicleModels.Muzzle(vehicle,gun);
                    float localYaw=Mathf.Atan2(profile.boreLocalDirection.x,profile.boreLocalDirection.z)*Mathf.Rad2Deg;
                    Traverse(vehicle,fixedParent,Mathf.Atan2(direction.x,direction.z)*Mathf.Rad2Deg-localYaw);
                }
                return;
            }
            object boreParent=VehicleAimAlignment.BoreParent(vehicle,gun);
            if(boreParent!=null)
            {
                AimGun(vehicle,boreParent,far,selected);
                pitches[gun]=PitchOf(boreParent);
                return;
            }
            if(AerialMounts.Fixed(vehicle,gun))
            {
                Vector3 f=AerialMounts.Rotation(vehicle)*Vector3.forward;
                pitches[gun]=-Mathf.Atan2(f.y,new Vector2(f.x,f.z).magnitude)*Mathf.Rad2Deg;
                VehicleApi.Set(gun,"TurretRotation",Mathf.Atan2(f.x,f.z)*Mathf.Rad2Deg);return;
            }
            Vector3 d=VehicleModels.U(far)-VehicleModels.U(FlightMuzzle(vehicle,gun));
            float wanted=Mathf.Atan2(d.x,d.z)*Mathf.Rad2Deg;
            pitches[gun]=Mathf.Clamp(-Mathf.Atan2(d.y,new Vector2(d.x,d.z).magnitude)*Mathf.Rad2Deg,profile.minPitch,profile.maxPitch);
            Traverse(vehicle,gun,wanted);
        }
        private static void Traverse(Pawn vehicle,object gun,float worldBearing)
        {
            float wanted=VehicleAimAlignment.NativeBearing(vehicle,worldBearing);
            object def=VehicleApi.Get(gun,"def");
            float rotation=VehicleApi.Float(gun,"TurretRotation");
            if (!VehicleApi.Bool(gun, "IsTargetable")) return;
            // Native traversal checks a bearing from TurretLocation. Use the requested
            // bearing, since an offset optic can see a different near-field angle.
            Vector3 nativeMount = (Vector3)VehicleApi.Get(gun, "TurretLocation");
            Vector3 restrictionTarget = nativeMount + VehicleModels.U(HitMath.Direction(wanted, 0f)) * 16f;
            if (!(bool)VehicleApi.Call(gun, "AngleBetween", restrictionTarget)) return;
            VehicleApi.Call(gun,"AlignToAngleRestricted",wanted);
            object parent=VehicleApi.Get(gun,"attachedTo");
            float parentYaw=parent==null?0f:VehicleApi.Float(parent,"TurretRotation");
            float target=VehicleApi.Float(gun,"TurretRotationTargeted")+parentYaw;
            float step=Math.Max(30f,VehicleApi.Float(def,"rotationSpeed")*60f)*VehicleControl.Efficiency
                *Math.Min(.1f,Time.unscaledDeltaTime)*Find.TickManager.TickRateMultiplier;
            VehicleApi.Set(gun,"TurretRotation",Mathf.MoveTowardsAngle(rotation,target,step)-parentYaw);
        }
        private static void FrameGun(object gun,bool input,bool pressed,bool held,bool paused)
        {
            VehicleGunState state=StateOf(gun);
            if (VehicleGroupFire.GuideFrame(gun)) return;
            VehicleGroupFire.CheckInput(gun, input && !paused, pressed, held);
            int tick = Find.TickManager.TicksGame;
            bool empty = !VehicleApi.Bool(gun, "HasAmmo"), reloading = VehicleApi.Int(gun, "ReloadTicks") > 0;
            // Start native loading without waiting for a fire click. Retry at most once per second after resupply.
            if (!paused && VehicleControl.CanFire && state.RetryReload(tick,empty,reloading))
            { Reload(gun); reloading = VehicleApi.Int(gun, "ReloadTicks") > 0; empty=!VehicleApi.Bool(gun,"HasAmmo"); }
            bool wants = state.Cycle.Poll(input&&!paused,pressed,held,reloading,!empty);
            if (!wants) { VehicleGroupFire.Cancel(gun); return; }
            if (!Ballistics.Enabled) { state.Failure = L.T("3D 弹道未启用，禁止载具手动射击", "3D ballistics unavailable: manual vehicle fire disabled"); return; }
            if (paused || Find.TickManager.Paused || VehicleApi.Bool(gun, "TurretDisabled") || VehicleApi.Bool(gun, "OnCooldown")) return;
            ThingDef projectile = ProjectileFor(gun);
            if (projectile?.projectile == null || typeof(Beam).IsAssignableFrom(projectile.thingClass)) { state.Failure = L.T("此特殊武器尚无载具弹道适配", "This special weapon needs a vehicle ballistics adapter"); return; }
            // CE owns a different projectile pipeline; never silently fire vanilla substitutes or consume its ammo.
            if (VehicleApi.TurretType.GetField("LaunchProjectileCE")?.GetValue(null) != null && !CeCompat.Ready)
            { state.Failure = L.T("当前 CE 载具弹道需专用适配", "CE vehicle ballistics needs a dedicated adapter"); return; }
            state.Failure = null;
            if (!VehicleApi.Bool(gun, "HasAmmo")) { state.Cycle.Interrupt(); return; }
            int now = Find.TickManager.TicksGame;
            if (!state.Clock.Ready(now, VehicleApi.Int(gun, "ReloadTicks") > 0, VehicleApi.Bool(gun, "OnCooldown"),
                VehicleApi.Bool(gun, "TurretDisabled"), VehicleApi.Bool(gun, "HasAmmo"))) return;
            if (state.Cycle.Remaining == 0)
            {
                object mode = VehicleApi.Get(gun, "CurrentFireMode");
                IntRange count = (IntRange)VehicleApi.Get(mode, "shotsPerBurst");
                state.Cycle.BeginBurst(count.RandomInRange, IsSelectable(gun));
            }
            if (VehicleGroupFire.Supports(gun))
            {
                if (state.AimPoint.HasValue) VehicleGroupFire.Submit(gun, VehicleGroupFire.Target(gun, state.AimPoint.Value));
                return;
            }
            if (!Fire(gun)) { state.Cycle.Interrupt(); return; }
            state.Cycle.Fired();
            int shotTicks = Math.Max(1, VehicleApi.Int(gun, "TicksPerShot")); // efficiency hook already applied
            state.Clock.Fired(now, shotTicks);
            VehicleApi.Set(gun, "burstTicks", shotTicks); // Native AI inherits the remaining shot cadence on release.
            VehicleApi.Call(gun, "StartTicking"); // No second warmup or burst-recovery wait in manual control.
            if (!VehicleApi.Bool(gun, "HasAmmo")) Reload(gun);
        }
        private static bool Fire(object gun)
        {
            Pawn v = VehicleControl.Vehicle;
            if (!AircraftSystems.CanFire(v, gun))
            { StateOf(gun).Failure = L.T("武器系统失能", "Weapon system disabled"); return false; }
            if(AerialArmament.Data(gun)?.lockableMissile==true&&!AerialTargetLock.EnsureLock())
            {StateOf(gun).Failure=L.T("导弹尚未锁定 · ","Missile not locked · ")+ControlKeys.Label(ControlAction.AirOrbit)+L.T(" 锁定目标"," lock target");return false;}
            StateOf(gun).Failure=null;
            if(AerialArmament.Bomb(gun)&&!AerialFlight.Airborne(v)){StateOf(gun).Failure=L.T("起飞后才能投弹","Take off before releasing bombs");return false;}
            VehicleApi.Set(gun, "targetInfo", NativeTarget(v, gun));
            launches = 0; Firing = gun;
            try { VehicleApi.Call(gun, "FireTurret"); }
            finally { Firing = null; VehicleApi.Set(gun, "targetInfo", LocalTargetInfo.Invalid); }
            if (launches == 0) StateOf(gun).Failure = L.T("武器未产生可接管的弹丸", "Weapon did not launch a supported projectile");
            else { VehicleApi.Set(gun, "CurrentTurretFiring", VehicleApi.Int(gun, "CurrentTurretFiring") + 1); VehicleApi.Call(gun, "StartTicking"); }
            return launches > 0;
        }
        private static LocalTargetInfo NativeTarget(Pawn v, object gun)
        {
            Vector3 muzzle = Muzzle(v, gun);
            V3 direction = Direction(v,gun);
            ThingDef projectile = VehicleApi.Get(gun, "ProjectileDef") as ThingDef;
            bool areaFlight = VehicleProjectileRules.NativeAreaFlight(projectile?.thingClass);
            float range = AerialFlight.Airborne(v) && !areaFlight
                ? v.Map.Size.x + v.Map.Size.z
                : Math.Max(8f, VehicleApi.Float(gun, "MaxRange"));
            V3 far = VehicleModels.V(muzzle) + direction * range;
            // An expandable native spray is a planar area attack. Keep both endpoints in this floor's raw rows;
            // selecting a different ProjectZ band here can turn a 15-cell flame into a 256-cell damage radius.
            IntVec3 cell = (areaFlight
                ? FloorSpace.Logical(v.Map, far, v.Position).ToIntVec3() : FloorSpace.Cell(v.Map, far)).ClampInsideMap(v.Map);
            if (cell == v.Position) cell = (v.Position + v.Rotation.FacingCell).ClampInsideMap(v.Map);
            return new LocalTargetInfo(cell);
        }
        internal static void BeginGroupShot(object gun, out object previous, out int previousLaunches)
        {
            previous = Firing; previousLaunches = launches;
            Firing = gun; launches = 0;
        }
        internal static bool EndGroupShot(object gun, object previous, int previousLaunches, bool successful)
        {
            bool launched = launches > 0;
            Firing = previous; launches = previousLaunches;
            if (!successful || !launched) return false;
            VehicleGunState state = StateOf(gun);
            state.Cycle.Fired();
            state.Clock.Fired(Find.TickManager.TicksGame, Math.Max(1, VehicleApi.Int(gun, "TicksPerShot")));
            state.Failure = null;
            // The native queue owns CurrentTurretFiring, warmup and burst timers.
            return true;
        }
        public static void AfterLaunch(Projectile __instance, Thing launcher)
        {
            if (Firing == null || launcher != VehicleControl.Vehicle || __instance.Destroyed || __instance.Map == null) return;
            // This postfix runs inside VEF's derived Launch, before it initializes StartingPosition. Leave all of
            // its origin/destination, tick, cone and damage state native, but count the shot for reload/cadence.
            if (VehicleProjectileRules.NativeAreaFlight(__instance.GetType())) { launches++; return; }
            Pawn vehicle = VehicleControl.Vehicle;
            V3 muzzle = FlightMuzzle(vehicle, Firing);
            float speed = NativeSpeed(Firing, __instance.def);
            int shots = IsSelectable(Firing) ? CycleFor(Firing).Mode == VehicleManualFireMode.Triple ? 3 : CycleFor(Firing).Mode == VehicleManualFireMode.Automatic ? 10 : 1
                : Math.Max(1, ((IntRange)VehicleApi.Get(VehicleApi.Get(Firing, "CurrentFireMode"), "shotsPerBurst")).max);
            Ballistics.AdoptVehicleRound(__instance, vehicle, muzzle, Direction(vehicle,Firing), speed, shots);
            launches++;
        }
        internal static void CeLaunched()=>launches++;
    }
}
