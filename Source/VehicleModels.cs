using System;
using System.Collections.Generic;
using System.Linq;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class VehicleModels
    {
        private static readonly Dictionary<ThingDef, VehicleViewDef> profiles = new Dictionary<ThingDef, VehicleViewDef>();
        private static readonly Dictionary<(VehicleViewDef, string), ModelHandle> models = new Dictionary<(VehicleViewDef, string), ModelHandle>();
        private static readonly Dictionary<object, VehicleWeaponView> fallbackWeapons = new Dictionary<object, VehicleWeaponView>();
        internal static Vector3 U(V3 v) => new Vector3(v.X, v.Y, v.Z);
        internal static V3 V(Vector3 v) => new V3(v.x, v.y, v.z);
        internal static VehicleViewDef Profile(Pawn v)
        {
            if (profiles.TryGetValue(v.def, out VehicleViewDef result)) return result;
            result = DefDatabase<VehicleViewDef>.AllDefsListForReading.FirstOrDefault(d => d.vehicleDef == v.def.defName);
            if (result == null)
            {
                float height = Mathf.Clamp(Math.Min(v.def.size.x, v.def.size.z) * .55f, 1.1f, 3.5f);
                result = new VehicleViewDef { vehicleDef = v.def.defName, hullHeight = height,
                    driverEye = new Vector3(-Math.Min(.45f, v.def.size.x * .2f), height + .25f, v.def.size.z * .25f),
                    commanderEye = new Vector3(.35f, height + .75f, 0f), shoulderDistance = Math.Max(6f, v.def.size.z * 1.5f) };
            }
            profiles[v.def] = result; return result;
        }
        internal static VehicleSeatView Seat(Pawn v, object h) => Profile(v).seats.FirstOrDefault(s => s.roleKey == VehicleApi.RoleKey(h));
        internal static VehicleWeaponView ExplicitWeapon(Pawn v, object turret)
        {
            string defName = (VehicleApi.Get(turret, "def") as Def)?.defName;
            var choices = Profile(v).weapons.Where(w => w.turretKey == VehicleApi.TurretKey(turret));
            return choices.FirstOrDefault(w => w.turretDef == defName) ?? choices.FirstOrDefault(w => string.IsNullOrEmpty(w.turretDef));
        }
        internal static VehicleWeaponView Weapon(Pawn v, object turret)
        {
            VehicleWeaponView p = AerialArmament.View(v,turret) ?? ExplicitWeapon(v, turret);
            if (p != null) return VehicleAimAlignment.ApplyProfile(v,p);
            object def = VehicleApi.Get(turret, "def");
            if (!fallbackWeapons.TryGetValue(def, out p)) fallbackWeapons[def] = p = new VehicleWeaponView {
                turretKey = VehicleApi.TurretKey(turret), muzzle = new Vector3(0, 0, Math.Max(.15f, VehicleApi.Float(def, "projectileOffset"))) };
            return p;
        }
        internal static Vector3 World(Pawn v, Vector3 local) => AerialFlight.TryGet(v, out _) ? AerialGeometry.World(v, local) : U(FloorSpace.Spot(v)) + Quaternion.Euler(0f, VehicleApi.Yaw(v), 0f) * local;
        internal static Vector3 CrewEye(Pawn v, object handler, bool driver)
        {
            if (AerialFlight.TryGet(v, out _))
            {
                var p = Profile(v); var seat = handler == null ? null : Seat(v, handler);
                object turret = seat?.turretKey == null ? null : VehicleApi.Turrets(v).FirstOrDefault(t => VehicleApi.TurretKey(t) == seat.turretKey);
                if (p.exactCrewAnchors && turret != null) return AerialGeometry.Mount(v,turret) + AerialGeometry.TurretRotation(v,turret)*seat.turretEyeOffset;
                return World(v, seat?.eye ?? (driver ? p.driverEye : p.commanderEye));
            }
            VehicleViewDef profile = Profile(v);
            Vector3 local = (handler == null ? null : Seat(v, handler))?.eye ?? (driver ? profile.driverEye : profile.commanderEye);
            // Imported tanks have no modeled cabin. Put crew eyes above armour rather than inside opaque geometry.
            float roof = profile.hullHeight;
            ModelHandle hull = Load(profile, profile.bodyModel);
            if (hull != null) roof = Math.Max(roof, hull.Max.y * profile.modelScale.y);
            Vector3 eye = World(v, new Vector3(local.x, Math.Max(local.y, roof + (driver ? .15f : .4f)), local.z));
            if (!driver)
            {
                object turret = VehicleWeapons.Current;
                if (handler != null)
                {
                    string key = Seat(v, handler)?.turretKey;
                    if (!string.IsNullOrEmpty(key)) turret = VehicleApi.Turrets(v).FirstOrDefault(t => VehicleApi.TurretKey(t) == key) ?? turret;
                }
                if (turret != null)
                {
                    VehicleWeaponView part = ExplicitWeapon(v, turret);
                    ModelHandle top = part == null ? null : Load(profile, part.turretModel);
                    float turretRoof = Mount(v, turret).y + (top == null ? .4f : top.Max.y * profile.modelScale.y);
                    eye.y = Math.Max(eye.y, turretRoof + .2f);
                }
            }
            return eye;
        }
        internal static Vector3 Mount(Pawn v, object turret) => AerialFlight.TryGet(v, out _) ? AerialGeometry.Mount(v, turret) : Mount(v, turret, 0);
        private static Vector3 Mount(Pawn v, object turret, int depth)
        {
            VehicleWeaponView spec = ExplicitWeapon(v, turret);
            if (spec != null)
            {
                if (!string.IsNullOrEmpty(spec.parentTurretKey) && depth < 8)
                {
                    object parent = VehicleApi.Turrets(v).FirstOrDefault(t => VehicleApi.TurretKey(t) == spec.parentTurretKey);
                    if (parent != null && parent != turret)
                    {
                        Vector3 local = spec.mount;
                        if (spec.boreFollowsParentPitch)
                        {
                            Vector3 pivot = Weapon(v, parent).barrelPivot;
                            local = pivot + Quaternion.Euler(VehicleWeapons.PitchOf(parent), 0f, 0f) * (local - pivot);
                        }
                        return Mount(v, parent, depth + 1) + Quaternion.Euler(0f, VehicleAimAlignment.TurretYaw(v, parent), 0f) * local;
                    }
                }
                return World(v, spec.mount);
            }
            Vector3 native = (Vector3)VehicleApi.Get(turret, "TurretLocation"), offset = FloorSpace.Offset(v);
            return new Vector3(native.x + offset.x, offset.y + Profile(v).hullHeight, native.z + offset.z);
        }
        internal static Vector3 Muzzle(Pawn v, object turret)
        {
            if (VehicleAimAlignment.ParentMuzzle(v,turret,out Vector3 inherited)) return inherited;
            if (AerialFlight.TryGet(v, out _)) return AerialGeometry.Muzzle(v, turret);
            VehicleWeaponView part = Weapon(v, turret);
            Vector3 local = part.barrelPivot + Quaternion.Euler(VehicleWeapons.PitchOf(turret), 0f, 0f) * (VehicleAimAlignment.MuzzleLocal(turret, part) - part.barrelPivot);
            return Mount(v, turret) + Quaternion.Euler(0f, VehicleAimAlignment.TurretYaw(v, turret), 0f) * local;
        }
        internal static Vector3 Sight(Pawn v, object turret)
        {
            if (AerialFlight.TryGet(v, out _)) return AerialGeometry.Sight(v, turret);
            VehicleWeaponView part = Weapon(v, turret);
            object parent = VehicleAimAlignment.BoreParent(v, turret);
            if (parent != null) return part.sightFollowsBarrel ? VehicleAimAlignment.ParentPoint(v, parent, part.mount + part.sight)
                : Mount(v, parent) + Quaternion.Euler(0f, VehicleAimAlignment.TurretYaw(v, parent), 0f) * (part.mount + part.sight);
            Vector3 local = part.sightFollowsBarrel ? part.barrelPivot + Quaternion.Euler(VehicleWeapons.PitchOf(turret), 0f, 0f) * (part.sight - part.barrelPivot) : part.sight;
            return Mount(v, turret) + Quaternion.Euler(0f, VehicleAimAlignment.TurretYaw(v, turret), 0f) * local;
        }
        internal static ModelHandle Load(VehicleViewDef profile, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || profile.modContentPack == null) return null;
            var key = (profile, path);
            if (!models.TryGetValue(key, out ModelHandle model))
            {
                try { model = Api.LoadModel(profile.modContentPack, path); }
                catch (Exception e) { Log.Warning("[FpsBridge] Vehicle model " + path + ": " + e.Message); }
                models[key] = model;
            }
            return model;
        }
        // No grey proxy art is generated. An incomplete art pack keeps the original vehicle visible.
        public static bool DrawVehicle(Pawn __instance, DrawPhase phase)
        {
            if (phase != DrawPhase.Draw) return true; // native preparation may run off the main thread
            if (!VehicleApi.Ready || !View3D.Active || !Api.ModelsOn || !__instance.Spawned || __instance.Position.Fogged(__instance.Map)) return true;
            var parts=new List<VehicleGeometry.Part>();
            if(!VehicleGeometry.Parts(__instance,VehicleGeometry.Pose(__instance),parts))return true;
            VehicleGeometry.Draw(__instance,parts);return false;
        }
        internal static void Volumes(Pawn v, List<HitVolume> into)
        {
            if (AerialFlight.TryGet(v, out _)) { AerialGeometry.Volumes(v, into); return; }
            VehicleViewDef profile = Profile(v);
            if (profile.hitBoxes.Count > 0)
            {
                foreach (VehicleHitBox box in profile.hitBoxes)
                {
                    object turret = string.IsNullOrEmpty(box.turretKey) ? null : VehicleApi.Turrets(v).FirstOrDefault(t => VehicleApi.TurretKey(t) == box.turretKey);
                    if (!string.IsNullOrEmpty(box.turretKey) && turret == null) continue;
                    float yaw = turret == null ? VehicleApi.Yaw(v) : VehicleApi.Float(turret, "TurretRotation");
                    Vector3 center = turret == null ? World(v, box.center) : Mount(v, turret) + Quaternion.Euler(0f, yaw, 0f) * box.center;
                    Box(into, center, box.size, yaw);
                }
                return;
            }
            ModelHandle model = Load(profile, profile.bodyModel);
            if (model != null)
            {
                Vector3 center = Vector3.Scale((model.Min + model.Max) * .5f, profile.modelScale);
                Vector3 size = Vector3.Scale(model.Max - model.Min, profile.modelScale);
                Box(into, World(v, center), size, VehicleApi.Yaw(v));
            }
            else Box(into, World(v, new Vector3(0f, profile.hullHeight * .5f, 0f)), new Vector3(v.def.size.x, profile.hullHeight, v.def.size.z), VehicleApi.Yaw(v));
            foreach (object t in VehicleApi.Turrets(v))
            {
                VehicleWeaponView part = ExplicitWeapon(v, t);
                ModelHandle top = part == null ? null : Load(profile, part.turretModel);
                if (top == null) continue;
                float yaw = VehicleApi.Float(t, "TurretRotation");
                Vector3 center = Mount(v, t) + Quaternion.Euler(0f, yaw, 0f) * Vector3.Scale((top.Min + top.Max) * .5f, profile.modelScale);
                Box(into, center, Vector3.Scale(top.Max - top.Min, profile.modelScale), yaw);
                ModelHandle ring = Load(profile, part.mountRingModel);
                if (ring != null)
                {
                    Vector3 rc = Vector3.Scale((ring.Min + ring.Max) * .5f, profile.modelScale);
                    Box(into, Mount(v, t) + Quaternion.Euler(0f, yaw, 0f) * rc, Vector3.Scale(ring.Max - ring.Min, profile.modelScale), yaw);
                }
                ModelHandle barrel = Load(profile, part.barrelModel);
                if (barrel != null)
                {
                    float pitch = VehicleWeapons.PitchOf(t);
                    Quaternion rotation = Quaternion.Euler(pitch, 0f, 0f);
                    Vector3 bc = part.barrelPivot + rotation * Vector3.Scale((barrel.Min + barrel.Max) * .5f, profile.modelScale);
                    Vector3 bs = Vector3.Scale(barrel.Max - barrel.Min, profile.modelScale);
                    float sin = Mathf.Abs(Mathf.Sin(pitch * Mathf.Deg2Rad)), cos = Mathf.Abs(Mathf.Cos(pitch * Mathf.Deg2Rad));
                    Box(into, Mount(v, t) + Quaternion.Euler(0f, yaw, 0f) * bc, new Vector3(bs.x, bs.y * cos + bs.z * sin, bs.z * cos + bs.y * sin), yaw);
                }
            }
        }
        private static void Box(List<HitVolume> into, Vector3 center, Vector3 size, float yaw) => into.Add(new HitVolume {
            Kind = VolumeKind.Box, Region = HitRegion.Solid, Part = -1, Leg = -1, A = V(center), B = V(size * .5f), Yaw = yaw });
    }
}
