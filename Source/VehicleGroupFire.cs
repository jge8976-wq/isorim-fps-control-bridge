using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // BMP's grouped weapons own their target revisions, warmup and due queue.
    // Manual control submits an order and observes a real native shot.
    internal static class VehicleGroupFire
    {
        private sealed class Request
        {
            internal Pawn Vehicle;
            internal Map Map;
            internal Game Game;
            internal int Expires;
            internal VehicleManualFireMode Mode;
            internal bool Guiding;
            internal LocalTargetInfo Target;
        }

        private sealed class Shot
        {
            internal object Gun;
            internal object Previous;
            internal int PreviousLaunches;
        }

        private static Type groupedType;
        private static MethodInfo canFireQueued, isDue;
        private static readonly Dictionary<object, Request> requests = new Dictionary<object, Request>();

        internal static void Install(Harmony harmony)
        {
            groupedType = AccessTools.TypeByName("BMP1IFV.GroupedWeaponTurret");
            if (groupedType == null) return;
            canFireQueued = AccessTools.DeclaredMethod(groupedType, "CanFireQueuedShot", Type.EmptyTypes);
            isDue = AccessTools.DeclaredMethod(AccessTools.TypeByName("BMP1IFV.TurretGroupQueue"), "IsDue", new[] { groupedType });
            MethodInfo fire = AccessTools.DeclaredMethod(groupedType, "FireTurret", Type.EmptyTypes);
            if (fire == null || canFireQueued == null || isDue == null)
                throw new MissingMethodException("BMP grouped native fire contract changed");
            harmony.Patch(fire, prefix: new HarmonyMethod(typeof(VehicleGroupFire), nameof(BeforeFire)),
                finalizer: new HarmonyMethod(typeof(VehicleGroupFire), nameof(AfterFire)));
            harmony.Patch(AccessTools.DeclaredMethod(groupedType, "PushTurretToQueue", Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(VehicleGroupFire), nameof(BeforeQueue)));
        }

        internal static bool Supports(object gun) => groupedType != null && groupedType.IsInstanceOfType(gun)
            && VehicleApi.Get(gun, "Controller") != null;
        internal static bool Pending(object gun) => requests.ContainsKey(gun);
        internal static bool Guiding(object gun) => gun != null && requests.TryGetValue(gun, out Request request) && request.Guiding;

        internal static bool GuideFrame(object gun)
        {
            if (!Guiding(gun)) return false;
            Request request = requests[gun];
            if (!Valid(gun, request))
            { Cancel(gun); return false; }
            return true;
        }

        private static bool GuideContext(Request request) => Current.Game == request.Game && Find.CurrentMap == request.Map
            && VehicleControl.Owns(request.Vehicle) && request.Vehicle.Spawned && request.Vehicle.Map == request.Map
            && !request.Vehicle.Destroyed && !request.Vehicle.Dead && VehicleControl.OperatorCapable(true);

        private static bool HasGuidance(object gun) => (bool)VehicleApi.Call(VehicleApi.Get(gun, "Controller"), "HasActiveGuidedProjectile", gun);

        internal static LocalTargetInfo Target(object gun, V3 point)
        {
            Pawn vehicle = VehicleControl.Vehicle;
            object controller = VehicleApi.Get(gun, "Controller");
            Vector3 muzzle = (Vector3)VehicleApi.Call(controller, "MuzzlePosition", gun);
            FloorBand band = FloorSpace.Band(vehicle.Map, vehicle.Position);
            float dx = point.X - muzzle.x, dz = point.Z - (muzzle.z - band.Shift);
            float distance = Mathf.Sqrt(dx * dx + dz * dz);
            // Leave room for the final cell centre while retaining the native
            // muzzle's exact horizontal range origin and the selected floor.
            float range = Math.Max(.01f, VehicleApi.Float(gun, "MaxRange") - .75f);
            if (distance > range)
                point = new V3(muzzle.x + dx * range / distance, point.Y, muzzle.z - band.Shift + dz * range / distance);
            V3 start = VehicleModels.V(VehicleModels.Mount(vehicle, gun));
            V3 ray = point - start;
            object profile = AccessTools.Property(gun.GetType(), "Profile")?.GetValue(gun);
            if (profile != null && VehicleApi.Bool(profile, "operatorGuidance") && ray.Length > .001f)
            {
                LocalTargetInfo picked = Ballistics.PickTarget(vehicle, start, point + ray * (.05f / ray.Length));
                if (picked.HasThing) return picked;
            }
            return new LocalTargetInfo(FloorSpace.Cell(vehicle.Map, point).ClampInsideMap(vehicle.Map));
        }

        internal static void Submit(object gun, LocalTargetInfo target)
        {
            Pawn vehicle = VehicleControl.Vehicle;
            if (!Supports(gun) || vehicle == null || !target.IsValid) return;
            if (!requests.ContainsKey(gun))
                requests.Add(gun, new Request { Vehicle = vehicle, Map = vehicle.Map, Game = Current.Game,
                    Expires = Find.TickManager.TicksGame + 1800, Mode = VehicleWeapons.CycleFor(gun).Mode });
            else if (VehicleWeapons.CycleFor(gun).Mode == VehicleManualFireMode.Automatic)
                requests[gun].Expires = Find.TickManager.TicksGame + 1800;
            requests[gun].Target = target;
            VehicleApi.Call(gun, "SetTarget", target);
            VehicleApi.Call(gun, "StartTicking");
        }

        internal static void CheckInput(object gun, bool enabled, bool pressed, bool held)
        {
            if (!Pending(gun)) return;
            if (Guiding(gun)) { GuideFrame(gun); return; }
            bool releasedAutomatic = VehicleWeapons.CycleFor(gun).Mode == VehicleManualFireMode.Automatic && !held && !pressed;
            if (!enabled || releasedAutomatic || !Valid(gun, requests[gun])) Cancel(gun);
        }

        private static bool Valid(object gun, Request request) => request.Guiding
            ? GuideContext(request) && Supports(gun) && HasGuidance(gun)
                && (bool)VehicleApi.Call(VehicleApi.Get(gun, "Controller"), "CanGuide", gun, request.Target)
            : Supports(gun) && Ballistics.Enabled && View3D.Active
            && Current.Game == request.Game && Find.CurrentMap == request.Map
            && VehicleControl.Owns(request.Vehicle) && request.Vehicle.Spawned && request.Vehicle.Map == request.Map
            && !request.Vehicle.Destroyed && !request.Vehicle.Dead && VehicleControl.CanFire && VehicleControl.Captured
            && VehicleWeapons.ManualGun(gun) && !VehicleWeapons.CycleFor(gun).FreshPressRequired
            && VehicleWeapons.CycleFor(gun).Mode == request.Mode
            && Application.isFocused && !Find.TickManager.Paused && Find.TickManager.TicksGame <= request.Expires;

        internal static void ClearTarget(object gun)
        {
            if (!Supports(gun)) return;
            object controller = VehicleApi.Get(gun, "Controller");
            VehicleApi.Call(controller, "ClearWeaponTarget", VehicleApi.TurretKey(gun));
            VehicleApi.Call(gun, "CancelBurst");
        }

        internal static void Cancel(object gun)
        {
            if (!Release(gun)) return;
            VehicleWeapons.CycleFor(gun).Interrupt();
        }

        private static bool Release(object gun)
        {
            if (gun == null || !requests.Remove(gun)) return false;
            ClearTarget(gun);
            return true;
        }

        internal static void CancelAll()
        {
            foreach (object gun in new List<object>(requests.Keys)) Cancel(gun);
        }

        private static bool BeforeFire(object __instance, out Shot __state)
        {
            __state = null;
            if (!requests.TryGetValue(__instance, out Request request)) return true;
            if (request.Guiding) { GuideFrame(__instance); return false; }
            if (!Valid(__instance, request)) { Cancel(__instance); return false; }
            if (VehicleWeapons.CycleFor(__instance).Remaining == 0) return false;
            if (!(bool)canFireQueued.Invoke(__instance, null) || !(bool)isDue.Invoke(null, new[] { __instance })) return true;
            __state = new Shot { Gun = __instance };
            VehicleWeapons.BeginGroupShot(__instance, out __state.Previous, out __state.PreviousLaunches);
            return true;
        }

        private static bool BeforeQueue(object __instance) => !Guiding(__instance);

        private static Exception AfterFire(Exception __exception, Shot __state)
        {
            if (__state == null) return __exception;
            bool launched = VehicleWeapons.EndGroupShot(__state.Gun, __state.Previous, __state.PreviousLaunches, __exception == null);
            if (__exception != null) Cancel(__state.Gun);
            else if (launched && HasGuidance(__state.Gun) && requests.TryGetValue(__state.Gun, out Request request))
            {
                request.Guiding = true;
                // Stop the native shot queue without changing the controller's
                // target/revision that the launched SACLOS missile still uses.
                VehicleApi.Call(__state.Gun, "CancelBurst");
            }
            else if (launched && VehicleWeapons.CycleFor(__state.Gun).Remaining == 0
                && VehicleWeapons.CycleFor(__state.Gun).Mode != VehicleManualFireMode.Automatic) Release(__state.Gun);
            return __exception;
        }
    }
}
