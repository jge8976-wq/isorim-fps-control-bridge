using System;
using HarmonyLib;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class VehicleScopeCamera
    {
        private const float MeshClearance = .1f;
        private delegate bool SecondaryAnchor(Pawn vehicle, object turret, bool sight, out Vector3 point);
        private static readonly SecondaryAnchor anchor = Bind<SecondaryAnchor>("HSK.VehicleModels.SecondaryWeapons", "Anchor",
            typeof(Pawn), typeof(object), typeof(bool), typeof(Vector3).MakeByRefType());
        private static readonly Func<Pawn, Vector3> modelBase = Bind<Func<Pawn, Vector3>>("HSK.VehicleModels.VehicleRead", "Base", typeof(Pawn));

        private static T Bind<T>(string typeName, string method, params Type[] arguments) where T : Delegate
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null) return null;
            try { return AccessTools.DeclaredMethod(type, method, arguments)?.CreateDelegate<T>(); }
            catch (ArgumentException e)
            {
                Log.WarningOnce("[FpsBridge] Secondary weapon model API mismatch: " + e.Message, 19271978);
                return null;
            }
        }

        internal static bool TrySecondaryAnchor(Pawn vehicle, object turret, bool sight, out Vector3 point)
        {
            point = default;
            if (AerialFlight.Supports(vehicle)) return false;
            if (anchor == null || modelBase == null) return false;
            try
            {
                if (!anchor(vehicle, turret, sight, out var raw)) return false;
                // The standalone renderer uses the native map position. Convert to
                // the same physical floor and vehicle origin as the other FPS eyes.
                point = raw + VehicleModels.World(vehicle, Vector3.zero) - modelBase(vehicle);
                return VehicleViewDef.Finite(point);
            }
            catch (Exception e)
            {
                Log.WarningOnce("[FpsBridge] Secondary weapon anchor unavailable: " + e.Message, 19271979);
                return false;
            }
        }

        internal static Vector3 Eye(Pawn vehicle, object turret)
        {
            if (AerialFlight.Supports(vehicle)) return VehicleModels.Sight(vehicle, turret);
            if(VehicleModels.Weapon(vehicle,turret).boreFixedToParent)
                return VehicleModels.Muzzle(vehicle,turret)+VehicleModels.U(VehicleWeapons.Direction(vehicle,turret))*.15f;
            bool secondary = TrySecondaryAnchor(vehicle, turret, true, out Vector3 eye);
            if (!secondary)
            {
                eye = VehicleModels.Sight(vehicle, turret);
            }

            if (vehicle.Map is Map map)
            {
                eye.x = Mathf.Clamp(eye.x, .1f, map.Size.x - .1f);
                eye.z = Mathf.Clamp(eye.z, .1f, FloorSpace.Bands(map)[0].Rows - .1f);
            }
            Vector3 forward = Quaternion.Euler(FirstPerson.Pitch, FirstPerson.Yaw, 0f) * Vector3.forward;
            return eye + Vector3.up * OwnMeshLift(vehicle, eye, forward);
        }

        internal static float OwnMeshLift(Pawn vehicle, Vector3 eye, Vector3 forward)
        {
            float near = Math.Max(.2f, View3D.NearCard);
            float lift = 0f;
            for (int i = 0; i < 3; i++)
            {
                Vector3 point = eye + forward * (near * i * .5f);
                if (VehicleModelPicking.TryOwnMeshTop(vehicle, point, out float top))
                    lift = Math.Max(lift, top + MeshClearance - point.y);
            }
            if (VehicleModelPicking.TryOwnMeshTop(vehicle, eye, eye + forward * near, out float crossedTop))
                lift = Math.Max(lift, crossedTop + MeshClearance - eye.y);
            return lift;
        }
    }
}
