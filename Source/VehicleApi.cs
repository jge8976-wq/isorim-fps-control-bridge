using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace HSK.FpsBridge
{
    // Optional dependency: no Vehicles/SmashTools assembly reference is emitted by the bridge.
    internal static class VehicleApi
    {
        internal static Type PawnType, RoleType, TurretType, PatherType;
        internal static bool Ready;
        internal static string Status = "NOT_LOADED";
        private static readonly Dictionary<(Type, string), MemberInfo> members = new Dictionary<(Type, string), MemberInfo>();
        private static readonly Dictionary<(Type, string, string), MethodInfo> methods = new Dictionary<(Type, string, string), MethodInfo>();
        private static MethodInfo fromAngle, locationRestricted, drivable, capable;
        private static FieldInfo vehicleKind;
        private static object landKind, seaKind;
        private static object movementRole, weaponRole;
        private static int mobilePermission;
        private static object moveSpeedStat;
        private static MethodInfo terrainCost, toRot4;
        private static FieldInfo frameworkSettings, moveStartEvent, moveStopEvent;
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        internal static object Get(object obj, string name)
        {
            if (obj == null) return null;
            var key = (obj.GetType(), name);
            if (!members.TryGetValue(key, out MemberInfo member))
            {
                member = (MemberInfo)AccessTools.Property(key.Item1, name) ?? AccessTools.Field(key.Item1, name);
                if (member == null) throw new MissingMemberException(key.Item1.FullName, name);
                members[key] = member;
            }
            return member is PropertyInfo prop ? prop.GetValue(obj) : ((FieldInfo)member).GetValue(obj);
        }
        internal static void Set(object obj, string name, object value)
        {
            Get(obj, name);
            MemberInfo member = members[(obj.GetType(), name)];
            if (member is PropertyInfo prop) prop.SetValue(obj, value); else ((FieldInfo)member).SetValue(obj, value);
        }
        internal static object Call(object obj, string name, params object[] args)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            string signature = string.Join("|", args.Select(a => a?.GetType().FullName ?? "null"));
            var key = (obj.GetType(), name, signature);
            if (!methods.TryGetValue(key, out MethodInfo method))
            {
                method = key.Item1.GetMethods(Flags).FirstOrDefault(m => m.Name == name && !m.ContainsGenericParameters
                    && m.GetParameters().Length == args.Length && m.GetParameters().Select((p, i) => args[i] == null
                        ? !p.ParameterType.IsValueType : p.ParameterType.IsInstanceOfType(args[i])).All(v => v));
                if (method == null) throw new MissingMethodException(key.Item1.FullName, name + "(" + signature + ")");
                methods[key] = method;
            }
            return method.Invoke(obj, args);
        }
        internal static bool Bool(object o, string name) => (bool)Get(o, name);
        internal static int Int(object o, string name) => Convert.ToInt32(Get(o, name));
        internal static float Float(object o, string name) => Convert.ToSingle(Get(o, name));
        internal static IEnumerable<object> List(object o, string name) => Get(o, name) is IEnumerable list ? list.Cast<object>() : Enumerable.Empty<object>();
        internal static bool IsVehicle(Thing t) => Ready && t != null && PawnType.IsInstanceOfType(t);
        internal static bool Land(Pawn v) => IsVehicle(v) && v.def != null && vehicleKind != null
            && vehicleKind.DeclaringType.IsInstanceOfType(v.def) && Equals(vehicleKind.GetValue(v.def), landKind);
        internal static bool Sea(Pawn v) => IsVehicle(v) && v.def != null && vehicleKind != null
            && vehicleKind.DeclaringType.IsInstanceOfType(v.def) && Equals(vehicleKind.GetValue(v.def), seaKind);
        internal static float NativeYaw(Pawn v) => Float(Get(v, "FullRotation"), "AsAngle");
        internal static float Yaw(Pawn v) => AerialFlight.TryGet(v,out var flight) ? flight.Motor.Yaw : VehicleControl.Owns(v) ? VehicleDriving.VisualYaw : NativeYaw(v);
        internal static List<object> Handlers(Pawn v) => List(v, "handlers").ToList();
        internal static List<object> Turrets(Pawn v) => Get(v, "CompVehicleTurrets") is object c ? List(c, "Turrets").ToList() : new List<object>();
        internal static object Role(object handler) => Get(handler, "role");
        internal static string RoleKey(object h) => (string)Get(Role(h), "key");
        internal static string RoleLabel(object h) => (string)Get(Role(h), "label");
        internal static string TurretKey(object turret) => (string)Get(turret, "key");
        internal static bool Movement(object h) => (Int(Role(h), "HandlingTypes") & 1) != 0;
        internal static bool HasWeapon(object h, object turret) => List(Role(h), "TurretIds").Contains(TurretKey(turret));
        internal static bool Occupant(object h, Pawn p) => List(h, "thingOwner").Contains(p);
        // Upgrades can remove roles. Inspect the live handlers, including turret requirements.
        internal static bool Uncrewed(Pawn v) => IsVehicle(v)
            && Handlers(v).All(h => Int(Role(h), "HandlingTypes") == 0 || Int(Role(h), "SlotsToOperate") <= 0)
            && Bool(v, "HasEnoughOperators");
        internal static bool CanOperate(object h, Pawn p) => (bool)Call(h, "CanOperateRole", p);
        internal static bool OperatorCapable(Pawn p, bool weapon) => Ready && p != null && (bool)capable.Invoke(null, new[] { (object)p, weapon ? weaponRole : movementRole });
        internal static bool Available(object h, Pawn p) => Bool(h, "AreSlotsAvailableAndReservable") && CanOperate(h, p);
        internal static float Efficiency(Pawn v)
        {
            var slots = new List<CrewSlot>();
            foreach (object h in Handlers(v))
            {
                int required = Int(Role(h), "SlotsToOperate");
                if (required <= 0 || Int(Role(h), "HandlingTypes") == 0) continue;
                slots.Add(new CrewSlot(required, List(h, "thingOwner").OfType<Pawn>().Count(p => CanOperate(h, p))));
            }
            return VehicleRules.Efficiency(slots);
        }
        internal static bool Board(Pawn v, Pawn p, object h) => (bool)Call(v, "TryAddPawn", p, h) && Occupant(h, p);
        internal static bool Drafted(Pawn v) => Bool(Get(v, "ignition"), "Drafted");
        internal static void Draft(Pawn v, bool value) => Set(Get(v, "ignition"), "Drafted", value);
        internal static void Stop(Pawn v)
        {
            if (VehicleDriving.ActiveFor(v)) { VehicleDriving.Halt(); return; }
            if (v != null) Call(Get(v, "vehiclePather"), "StopDead");
        }
        internal static float ManualSpeed(Pawn v) => Convert.ToSingle(Call(v, "GetStatValue", moveSpeedStat));
        internal static bool ChassisDisabled(Pawn v) => (Int(v, "MovementPermissions") & mobilePermission) != 0 && !Bool(v, "CanMove");
        internal static bool OutOfFuel(Pawn v) => Get(v, "CompFueledTravel") is object fuel && Float(fuel, "Fuel") <= .0001f;
        internal static float AerialSpeed(Pawn v)
        {
            object stat = AccessTools.Field(AccessTools.TypeByName("Vehicles.VehicleStatDefOf"), "FlightSpeed")?.GetValue(null);
            return stat == null ? 0f : Convert.ToSingle(Call(v, "GetStatValue", stat));
        }
        internal static bool ManualTerrain(Pawn v, IntVec3 cell)
        {
            if (!cell.InBounds(v.Map)) return false;
            TerrainDef terrain = v.Map.terrainGrid.TerrainAt(cell);
            return terrain != null && Convert.ToInt32(terrainCost.Invoke(null, new object[] { Get(v, "VehicleDef"), terrain, null })) < 10000;
        }
        internal static bool ManualPosition(Pawn v, Vector3 position, float yaw, float speed)
        {
            object rot = fromAngle.Invoke(null, new object[] { yaw });
            Rot4 cardinal = (Rot4)toRot4.Invoke(null, new[] { rot });
            if (!GenAdj.OccupiedRect(v.Position, cardinal, v.def.size).InBounds(v.Map)) return false;
            object previous = Get(v, "FullRotation");
            Set(v, "FullRotation", rot);
            Vector3 center = (Vector3)Call(v, "TrueCenter");
            IntVec3 at = new IntVec3(Mathf.RoundToInt(position.x - (center.x - v.Position.x)), v.Position.y,
                Mathf.RoundToInt(position.z - (center.z - v.Position.z)));
            if (!GenAdj.OccupiedRect(at, cardinal, v.def.size).InBounds(v.Map)) { Set(v, "FullRotation", previous); return false; }
            if (at != v.Position)
            {
                v.Position = at; Call(v, "ReclaimPosition");
                if (Bool(Get(frameworkSettings.GetValue(null), "main"), "runOverPawns")) Call(v, "CheckForCollisions", speed);
            }
            return true;
        }
        // Air projection updates native ownership, never ground run-over damage or terrain pathing.
        internal static bool AerialPosition(Pawn v, Vector3 position, float yaw, float yawDelta)
        {
            object rot = fromAngle.Invoke(null, new object[] { yaw });
            Rot4 cardinal = (Rot4)toRot4.Invoke(null, new[] { rot });
            object previous = Get(v,"FullRotation");
            Set(v,"FullRotation",rot);
            Vector3 center = (Vector3)Call(v, "TrueCenter");
            IntVec3 at = new IntVec3(Mathf.RoundToInt(position.x - (center.x-v.Position.x)), v.Position.y, Mathf.RoundToInt(position.z-(center.z-v.Position.z)));
            if (!GenAdj.OccupiedRect(at, cardinal, v.def.size).InBounds(v.Map)) { Set(v,"FullRotation",previous); return false; }
            if (v.Position != at) { v.Position = at; Call(v, "ReclaimPosition"); }
            ManualHullTurn(v, yawDelta);
            if(AerialFlight.Store!=null&&AerialFlight.Store.Flights.TryGetValue(v,out var record))AerialArrival.Stamp(v,record);
            return true;
        }
        internal static void ManualMoveEvent(Pawn v, bool moving)
        {
            object registry = Get(v, "EventRegistry");
            Call(Call(registry, "get_Item", (moving ? moveStartEvent : moveStopEvent).GetValue(null)), "ExecuteEvents");
        }
        public static void DrawManualPosition(Pawn __instance, ref Vector3 __result)
        { if (AerialFlight.TryGet(__instance, out var f)) __result = AerialFlight.RawRoot(__instance, f); else if (VehicleDriving.ActiveFor(__instance)) __result = VehicleDriving.Position;else if(VehicleContacts.RecoveredPose(__instance,out var pushed))__result=AerialFlight.Raw(__instance,VehicleModels.U(pushed.Position)); }
        public static bool ManualPather(object __instance) => !VehicleContacts.Recovering(Get(__instance,"vehicle") as Pawn) && !VehicleDriving.ActiveFor(Get(__instance, "vehicle") as Pawn) && !AerialFlight.OwnsMotion(Get(__instance, "vehicle") as Pawn);
        public static bool NativePathPermissions(object __instance)
        {
            Pawn vehicle=Get(__instance,"vehicle") as Pawn;
            // RecalculatePermissions only aborts a native path. Its Moving query also
            // includes our physical flight, which StopDead cannot stop. Let the active
            // motor handle loss of crew instead of recursively restarting IdleVehicle.
            return !VehicleDriving.ActiveFor(vehicle)&&!AerialFlight.OwnsMotion(vehicle);
        }
        public static void ManualMoving(object __instance, ref bool __result)
        { Pawn v = Get(__instance, "vehicle") as Pawn; if (AerialFlight.Airborne(v)) __result = true; else if (VehicleDriving.ActiveFor(v)) __result = VehicleDriving.IsMoving; }
        public static bool ManualRotationLock(object __instance)
        {
            Pawn v = Get(__instance, "vehicle") as Pawn;
            if (!VehicleDriving.ActiveFor(v)) return true;
            Set(__instance, "parentAngleCached", Float(v, "Angle"));
            Set(__instance, "parentRotCached", v.Rotation);
            return false;
        }
        public static void ManualFixedRotation(object __instance, ref float __result)
        {
            Pawn v = Get(__instance, "vehicle") as Pawn;
            if (VehicleDriving.ActiveFor(v) && !Bool(__instance, "IsTargetable") && Get(__instance, "attachedTo") == null)
                __result += Mathf.DeltaAngle(NativeYaw(v), VehicleDriving.VisualYaw);
        }
        internal static void ManualHullTurn(Pawn v, float delta)
        {
            foreach (object turret in Turrets(v))
            {
                ManualRotationLock(turret);
                if (Get(turret, "attachedTo") != null || !Bool(turret, "IsTargetable")) continue;
                if (ReferenceEquals(turret, VehicleWeapons.Current) && VehicleControl.CanFire) continue;
                Set(turret, "TurretRotation", Float(turret, "TurretRotation") + delta);
            }
        }
        internal static bool Move(Pawn v, IntVec3 to, bool reverse)
        {
            if (!to.InBounds(v.Map) || !Bool(v, "CanMoveFinal")) return false;
            object rot = fromAngle.Invoke(null, new object[] { (to.ToVector3Shifted() - v.Position.ToVector3Shifted()).AngleFlat() + (reverse ? 180f : 0f) });
            if ((bool)locationRestricted.Invoke(null, new object[] { v, v.Map, v.Position, rot })
                || (bool)locationRestricted.Invoke(null, new object[] { v, v.Map, to, rot }) || !(bool)drivable.Invoke(null, new object[] { v, to })) return false;
            if (Bool(v, "Reverse") != reverse) Set(v, "Reverse", reverse);
            Call(Get(v, "vehiclePather"), "StartPath", new LocalTargetInfo(to), PathEndMode.OnCell, false);
            return true;
        }
        internal static bool DriveClear(Pawn v, IntVec3 cell, float heading)
        {
            if (!cell.InBounds(v.Map)) return false;
            object rot = fromAngle.Invoke(null, new object[] { heading });
            return !(bool)locationRestricted.Invoke(null, new object[] { v, v.Map, cell, rot })
                && (bool)drivable.Invoke(null, new object[] { v, cell });
        }
        internal static bool FaceClear(Pawn v,float heading)
        {
            object rotation=fromAngle.Invoke(null,new object[]{heading});
            Rot4 cardinal=(Rot4)toRot4.Invoke(null,new[]{rotation});
            return GenAdj.OccupiedRect(v.Position,cardinal,v.def.size).InBounds(v.Map)
                &&!(bool)locationRestricted.Invoke(null,new object[]{v,v.Map,v.Position,rotation});
        }
        internal static bool Turn(Pawn v, float angle)
        {
            object rot = fromAngle.Invoke(null, new object[] { angle });
            if ((bool)locationRestricted.Invoke(null, new object[] { v, v.Map, v.Position, rot })) return false;
            Stop(v);
            Set(v, "FullRotation", rot);
            Call(v, "ReclaimPosition");
            return true;
        }
        internal static void ClearQueue(Pawn v)
        {
            object comp = Get(v, "CompVehicleTurrets");
            if (comp == null) return;
            foreach (object entry in List(comp, "turretQueue").ToArray()) Call(comp, "DequeueTurret", entry);
        }

        internal static void Install()
        {
            var harmony = new Harmony(Bootstrap.Id + ".vehicles");
            try
            {
                PawnType = AccessTools.TypeByName("Vehicles.VehiclePawn");
                if (PawnType == null) { Status = "NOT_LOADED"; Log.Message("[FpsBridge] VEHICLES OFF: optional Vehicle Framework not loaded"); return; }
                if (!RemoteTurret.CameraReady) throw new InvalidOperationException("Shared vehicle/turret camera hooks are unavailable");
                if (!typeof(Pawn).IsAssignableFrom(PawnType)) throw new TypeLoadException("VehiclePawn is not a compatible Verse.Pawn");
                Type vehicleDef = AccessTools.TypeByName("Vehicles.VehicleDef") ?? throw new TypeLoadException("VehicleDef");
                vehicleKind = AccessTools.Field(vehicleDef, "type") ?? throw new MissingMemberException(vehicleDef.FullName, "type");
                if (!vehicleKind.FieldType.IsEnum) throw new TypeLoadException("VehicleDef.type is not an enum");
                landKind = Enum.Parse(vehicleKind.FieldType, "Land");
                seaKind = Enum.Parse(vehicleKind.FieldType, "Sea");
                RoleType = AccessTools.TypeByName("Vehicles.VehicleRoleHandler") ?? throw new TypeLoadException("VehicleRoleHandler");
                Type handling = AccessTools.TypeByName("Vehicles.HandlingType") ?? throw new TypeLoadException("HandlingType");
                capable = NeedMethod(RoleType, "CanOperateRole", typeof(Pawn), handling);
                movementRole = Enum.Parse(handling, "Movement"); weaponRole = Enum.Parse(handling, "Turret");
                TurretType = AccessTools.TypeByName("Vehicles.VehicleTurret") ?? throw new TypeLoadException("VehicleTurret");
                PatherType = AccessTools.TypeByName("Vehicles.VehiclePathFollower") ?? throw new TypeLoadException("VehiclePathFollower");
                Type rot8 = AccessTools.Property(PawnType, "FullRotation")?.PropertyType ?? throw new MissingMemberException("FullRotation");
                fromAngle = NeedMethod(rot8, "FromAngle", typeof(float));
                toRot4 = NeedMethod(rot8, "ToRot4", rot8);
                frameworkSettings = AccessTools.Field(AccessTools.TypeByName("Vehicles.VehicleMod"), "settings");
                Type events = AccessTools.TypeByName("Vehicles.VehicleEventDefOf");
                moveStartEvent = AccessTools.Field(events, "MoveStart"); moveStopEvent = AccessTools.Field(events, "MoveStop");
                if (frameworkSettings == null || moveStartEvent == null || moveStopEvent == null) throw new MissingMemberException("Native vehicle settings/events");
                Type ext = AccessTools.TypeByName("Vehicles.Ext_Vehicles");
                locationRestricted = NeedMethod(ext, "LocationRestrictedBySize", PawnType, typeof(Map), typeof(IntVec3), rot8);
                drivable = NeedMethod(ext, "Drivable", PawnType, typeof(IntVec3));
                Type statDef = AccessTools.TypeByName("Vehicles.VehicleStatDef");
                moveSpeedStat = AccessTools.Field(AccessTools.TypeByName("Vehicles.VehicleStatDefOf"), "MoveSpeed").GetValue(null);
                var permissions = AccessTools.Property(PawnType, "MovementPermissions");
                if (permissions != null) mobilePermission = Convert.ToInt32(Enum.Parse(permissions.PropertyType, "Mobile"));
                terrainCost = NeedMethod(AccessTools.TypeByName("Vehicles.VehiclePathGrid"), "TerrainCostAt", vehicleDef, typeof(TerrainDef), typeof(System.Text.StringBuilder));
                NeedMethod(PawnType, "GetStatValue", statDef);
                NeedMethod(PawnType, "TrueCenter");
                NeedMethod(PawnType, "CheckForCollisions", typeof(float));
                NeedMethod(PawnType, "Notify_Teleported", typeof(bool), typeof(bool));
                NeedMethod(TurretType, "StartTicking");
                NeedMethod(PawnType, "TryAddPawn", typeof(Pawn), RoleType);
                NeedMethod(PawnType, "DisembarkPawn", typeof(Pawn));
                NeedMethod(PatherType, "StartPath", typeof(LocalTargetInfo), typeof(PathEndMode), typeof(bool));
                NeedMethod(TurretType, "FireTurret");
                Patch(harmony, AccessTools.PropertyGetter(PawnType, "HasEnoughOperators"), null, nameof(EnoughOperators));
                Patch(harmony, AccessTools.PropertyGetter(TurretType, "IsManned"), null, nameof(Manned));
                Patch(harmony, NeedMethod(PatherType, "CostToPayThisTick"), null, nameof(MoveCost));
                Patch(harmony, AccessTools.PropertyGetter(TurretType, "TicksPerShot"), null, nameof(ReloadTime));
                Patch(harmony, NeedMethod(TurretType, "TurretReloadTick"), nameof(ReloadTick), null);
                harmony.Patch(NeedMethod(TurretType, "AutoReload"),
                    prefix: new HarmonyMethod(typeof(VehicleWeapons), nameof(VehicleWeapons.BeforeReload)),
                    postfix: new HarmonyMethod(typeof(VehicleWeapons), nameof(VehicleWeapons.AfterReload)));
                Patch(harmony, NeedMethod(TurretType, "TurretTargeterTick"), nameof(Targeter), null);
                Patch(harmony, NeedMethod(TurretType, "TurretRotationTick"), nameof(Targeter), null);
                Patch(harmony, NeedMethod(TurretType, "FireTurret"), nameof(FireGuard), null);
                VehicleGroupFire.Install(harmony);
                VehicleCeSpeed.Install(harmony);
                Patch(harmony, AccessTools.PropertyGetter(PawnType, "DrawPos"), null, nameof(DrawManualPosition));
                Patch(harmony, NeedMethod(PatherType, "PatherTick"), nameof(ManualPather), null);
                Patch(harmony, NeedMethod(PatherType, "RecalculatePermissions"), nameof(NativePathPermissions), null);
                VehicleNativeJobs.Install(harmony);
                harmony.Patch(NeedMethod(PatherType, "StartPath", typeof(LocalTargetInfo), typeof(PathEndMode), typeof(bool)),
                    prefix: new HarmonyMethod(typeof(AerialHandoff), nameof(AerialHandoff.BeforeNativePath)));
                Patch(harmony, AccessTools.PropertyGetter(PatherType, "Moving"), null, nameof(ManualMoving));
                Patch(harmony, NeedMethod(TurretType, "UpdateRotationLock"), nameof(ManualRotationLock), null);
                Patch(harmony, AccessTools.PropertyGetter(TurretType, "TurretRotation"), null, nameof(ManualFixedRotation));
                Patch(harmony, NeedMethod(TurretType, "TryFindShootLineFromTo", typeof(IntVec3), typeof(LocalTargetInfo), typeof(ShootLine).MakeByRefType()), nameof(ShootLine), null);
                MethodInfo launch = NeedMethod(typeof(Projectile), "Launch", typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef));
                harmony.Patch(launch, postfix: new HarmonyMethod(typeof(VehicleWeapons), nameof(VehicleWeapons.AfterLaunch)));
                // Vehicle overlays are independent of the optional human pawn possession path.
                harmony.Patch(NeedMethod(typeof(UIRoot_Play), "UIRootOnGUI"), postfix: new HarmonyMethod(typeof(VehicleControl), nameof(VehicleControl.DrawGUI)) { priority = Priority.Last });
                harmony.Patch(NeedMethod(PawnType, "GetGizmos"), postfix: new HarmonyMethod(typeof(VehicleControl), nameof(VehicleControl.Gizmos)));
                harmony.Patch(NeedMethod(PawnType, "DynamicDrawPhaseAt", typeof(DrawPhase), typeof(Vector3), typeof(bool)),
                    prefix: new HarmonyMethod(typeof(VehicleModels), nameof(VehicleModels.DrawVehicle)));
                VehicleMouseInput.Install(harmony);
                VehicleFollowVisibility.Install(harmony);
                VehicleContacts.Install(harmony);
                Ready = true; Status = "BOUND";
                Log.Message("[FpsBridge] VEHICLES BOUND " + PawnType.Assembly.FullName + "; ground vehicles; native acceptance pending");
            }
            catch (Exception e)
            {
                Ready = false; Status = "INCOMPATIBLE";
                harmony.UnpatchAll(Bootstrap.Id + ".vehicles");
                Log.Warning("[FpsBridge] VEHICLES OFF (pawn/turret controls unchanged): " + e);
            }
        }
        private static MethodInfo NeedMethod(Type type, string name, params Type[] args) => type == null ? throw new TypeLoadException(name)
            : AccessTools.Method(type, name, args) ?? throw new MissingMethodException(type.FullName, name);
        private static void Patch(Harmony h, MethodBase target, string prefix, string postfix)
        {
            if (target == null) throw new MissingMethodException("Vehicle hook missing");
            h.Patch(target, prefix == null ? null : new HarmonyMethod(typeof(VehicleApi), prefix), postfix == null ? null : new HarmonyMethod(typeof(VehicleApi), postfix));
        }
        public static void EnoughOperators(Pawn __instance, ref bool __result)
        // Uncrewed sessions retain native permission, and Uncrewed can safely query this getter.
        { if (VehicleControl.Crew != null && VehicleControl.FullControl(__instance)) __result = true; }
        public static void Manned(object __instance, ref bool __result)
        { if (VehicleControl.Crew != null && VehicleControl.FullControl(Get(__instance, "vehicle") as Pawn)) __result = true; }
        public static void MoveCost(object __instance, ref float __result)
        { if (VehicleControl.Owns(Get(__instance, "vehicle") as Pawn)) __result *= VehicleControl.Efficiency * VehicleDriving.SpeedFactor; }
        public static void ReloadTime(object __instance, ref int __result)
        { if (VehicleControl.Owns(Get(__instance, "vehicle") as Pawn)) __result = VehicleRules.Delay(__result, VehicleControl.Efficiency); }
        public static bool ReloadTick(object __instance, ref bool __result)
        {
            if (!VehicleControl.Owns(Get(__instance, "vehicle") as Pawn) || VehicleControl.Efficiency >= 1f || (Find.TickManager.TicksGame & 1) == 0) return true;
            __result = Int(__instance, "ReloadTicks") > 0 || Int(__instance, "burstTicks") > 0;
            return false; // half-rate progress, no lasting changes to native reload timers/Defs
        }
        public static bool Targeter(object __instance, ref bool __result)
        {
            if (VehicleGroupFire.Supports(__instance)) return true;
            if (!VehicleControl.Owns(Get(__instance, "vehicle") as Pawn) || !VehicleWeapons.ManualGun(__instance)) return true;
            __result = false; return false;
        }
        public static bool FireGuard(object __instance) => !VehicleControl.Owns(Get(__instance, "vehicle") as Pawn)
            || ReferenceEquals(VehicleWeapons.Firing, __instance)
            || !VehicleWeapons.ManualGun(__instance)&&VehicleControl.OperatorCapable(true);
        public static bool ShootLine(object __instance, IntVec3 root, LocalTargetInfo target, ref ShootLine resultingLine, ref bool __result)
        {
            if (!ReferenceEquals(VehicleWeapons.Firing, __instance)) return true;
            resultingLine = new ShootLine(root, target.Cell); __result = true; return false; // our 3D trace owns obstacles
        }
    }
}
