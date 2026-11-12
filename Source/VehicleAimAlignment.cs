using UnityEngine;
using Verse;
using System.Linq;
using System.Collections.Generic;

namespace HSK.FpsBridge
{
    // VF stores turret bearings in its quantized native hull frame. The standalone
    // renderer adds the continuous FPS hull offset when it submits the same turret.
    internal static class VehicleAimAlignment
    {
        private static readonly HashSet<VehicleWeaponView> boundProfiles = new HashSet<VehicleWeaponView>();
        internal static VehicleWeaponView ApplyProfile(Pawn vehicle, VehicleWeaponView weapon)
        {
            if (!boundProfiles.Add(weapon)) return weapon;
            string profile = VehicleModels.Profile(vehicle).defName;
            VehicleBoreBindingDef binding = DefDatabase<VehicleBoreBindingDef>.AllDefsListForReading.FirstOrDefault(
                item => item.profileDefName == profile && item.turretKey == weapon.turretKey && item.turretDef == weapon.turretDef);
            if (binding == null) return weapon;
            weapon.boreFixedToParent = binding.fixedToParent;
            weapon.boreFollowsParentPitch |= binding.followsParentPitch;
            weapon.boreLocalDirection = binding.localDirection.normalized;
            if (binding.fixedToParent) weapon.parentTurretKey = binding.parentTurretKey;
            weapon.boreMuzzles = binding.muzzles;
            weapon.barrelRestAngles = binding.barrelRestAngles;
            if (binding.muzzles.Count > 0) weapon.muzzle = binding.muzzles[0];
            return weapon;
        }
        internal static Vector3 MuzzleLocal(object turret, VehicleWeaponView part)
        {
            int index = VehicleApi.Int(turret, "CurrentTurretFiring");
            if (part.boreMuzzles.Count > 0)
                return part.boreMuzzles[((index % part.boreMuzzles.Count) + part.boreMuzzles.Count) % part.boreMuzzles.Count];
            var shifts = VehicleApi.List(VehicleApi.Get(turret, "def"), "projectileShifting").ToArray();
            float shift = shifts.Length == 0 ? 0f : System.Convert.ToSingle(shifts[index < 0 || index >= shifts.Length ? 0 : index]);
            return part.muzzle + Vector3.right * shift;
        }
        internal static float WorldYaw(float nativeTurretYaw, float hullYaw, float nativeHullYaw)
            => Mathf.Repeat(nativeTurretYaw + Mathf.DeltaAngle(nativeHullYaw, hullYaw), 360f);

        internal static float NativeYaw(float worldTurretYaw, float hullYaw, float nativeHullYaw)
            => Mathf.Repeat(worldTurretYaw - Mathf.DeltaAngle(nativeHullYaw, hullYaw), 360f);

        internal static float TurretYaw(Pawn vehicle, object turret)
        {
            float native = VehicleApi.Float(turret, "TurretRotation");
            return AerialFlight.Supports(vehicle) ? native : WorldYaw(native, VehicleApi.Yaw(vehicle), VehicleApi.NativeYaw(vehicle));
        }

        internal static float NativeBearing(Pawn vehicle, float worldBearing)
            => AerialFlight.Supports(vehicle) ? worldBearing : NativeYaw(worldBearing, VehicleApi.Yaw(vehicle), VehicleApi.NativeYaw(vehicle));

        internal static object BoreParent(Pawn vehicle, object turret)
        {
            VehicleWeaponView profile = VehicleModels.Weapon(vehicle, turret);
            if (!profile.boreFollowsParentPitch || string.IsNullOrEmpty(profile.parentTurretKey)) return null;
            return VehicleApi.Turrets(vehicle).FirstOrDefault(candidate => candidate != turret && VehicleApi.TurretKey(candidate) == profile.parentTurretKey);
        }

        internal static object FixedParent(Pawn vehicle, object turret)
        {
            VehicleWeaponView profile = VehicleModels.Weapon(vehicle, turret);
            if (!profile.boreFixedToParent || string.IsNullOrEmpty(profile.parentTurretKey)) return null;
            return VehicleApi.Turrets(vehicle).FirstOrDefault(candidate => candidate != turret && VehicleApi.TurretKey(candidate) == profile.parentTurretKey);
        }

        internal static V3 BoreDirection(Pawn vehicle, object turret)
        {
            VehicleWeaponView profile = VehicleModels.Weapon(vehicle, turret);
            object parent = profile.boreFixedToParent ? FixedParent(vehicle, turret) : BoreParent(vehicle, turret);
            object axis = parent ?? turret;
            Vector3 local = profile.boreLocalDirection;
            if (!VehicleViewDef.Finite(local) || local.sqrMagnitude < .000001f) local = Vector3.forward;
            float yaw = profile.boreFixedToParent && parent == null ? VehicleApi.Yaw(vehicle) : TurretYaw(vehicle, axis);
            Quaternion rotation = Quaternion.Euler(profile.boreFixedToParent ? 0f : VehicleWeapons.PitchOf(axis), yaw, 0f);
            return VehicleModels.V((rotation * local).normalized);
        }

        internal static bool ParentMuzzle(Pawn vehicle, object turret, out Vector3 muzzle)
        {
            muzzle = default;
            VehicleWeaponView child = VehicleModels.Weapon(vehicle, turret);
            if (child.boreFixedToParent)
            {
                object fixedParent = FixedParent(vehicle, turret);
                Vector3 origin = fixedParent == null ? VehicleModels.World(vehicle,Vector3.zero) : VehicleModels.Mount(vehicle,fixedParent);
                float yaw = fixedParent == null ? VehicleApi.Yaw(vehicle) : TurretYaw(vehicle,fixedParent);
                int shot=VehicleApi.Int(turret,"CurrentTurretFiring");
                Vector3 localMuzzle=child.boreMuzzles.Count==0?child.muzzle:child.boreMuzzles[((shot%child.boreMuzzles.Count)+child.boreMuzzles.Count)%child.boreMuzzles.Count];
                muzzle = origin + Quaternion.Euler(0f,yaw,0f) * (child.mount+localMuzzle);
                return true;
            }
            object parent = BoreParent(vehicle, turret);
            if (parent == null) return false;
            muzzle = ParentPoint(vehicle, parent, child.mount + MuzzleLocal(turret, child));
            return true;
        }
        internal static Vector3 ParentPoint(Pawn vehicle, object parent, Vector3 point)
        {
            VehicleWeaponView basis = VehicleModels.Weapon(vehicle, parent);
            bool air = AerialFlight.TryGet(vehicle, out _);
            Quaternion frame = air ? AerialGeometry.TurretRotation(vehicle, parent) : Quaternion.Euler(0f, TurretYaw(vehicle, parent), 0f);
            Quaternion aim = air ? AerialGeometry.Aim(parent) : Quaternion.Euler(VehicleWeapons.PitchOf(parent), TurretYaw(vehicle, parent), 0f);
            return VehicleModels.Mount(vehicle, parent) + frame * basis.barrelPivot + aim * (point - basis.barrelPivot);
        }
    }
}
