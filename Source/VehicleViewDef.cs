using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Optional art-pack Def. All offsets are cells, X right / Y up / Z vehicle-forward, before IsoRim squash.
    // Empty model paths retain the framework renderer. Role and turret keys come from the actual vehicle.
    public sealed class VehicleViewDef : Def
    {
        public string vehicleDef;
        public string bodyModel;
        public bool exactCrewAnchors;
        public VehicleHudTemplate hudTemplate = VehicleHudTemplate.Auto;
        public Vector3 modelScale = Vector3.one;
        public float hullHeight = 1.8f;
        public Vector3 driverEye = new Vector3(-.45f, 2.05f, .65f);
        public Vector3 commanderEye = new Vector3(.4f, 2.6f, -.3f);
        public float shoulderDistance = 8f;
        public List<VehicleSeatView> seats = new List<VehicleSeatView>();
        public List<VehicleWeaponView> weapons = new List<VehicleWeaponView>();
        public List<VehicleAttachmentView> attachments = new List<VehicleAttachmentView>();
        public List<VehicleRotorView> rotors = new List<VehicleRotorView>();
        public List<VehicleHitBox> hitBoxes = new List<VehicleHitBox>();
        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            if (string.IsNullOrEmpty(vehicleDef)) yield return "vehicleDef is required";
            if (!float.IsFinite(hullHeight) || !float.IsFinite(shoulderDistance) || hullHeight <= 0 || shoulderDistance < 1
                || !Finite(driverEye) || !Finite(commanderEye) || !Finite(modelScale) || modelScale.x <= 0 || modelScale.y <= 0 || modelScale.z <= 0)
                yield return "Vehicle view dimensions must be finite and positive";
            var keys = new HashSet<string>();
            foreach (VehicleSeatView seat in seats)
                if (seat == null || string.IsNullOrEmpty(seat.roleKey) || !keys.Add(seat.roleKey) || !Finite(seat.eye)) yield return "Invalid or duplicate roleKey/eye";
            keys.Clear();
            foreach (VehicleWeaponView weapon in weapons)
                if (weapon == null || string.IsNullOrEmpty(weapon.turretKey) || !keys.Add(weapon.turretKey + ":" + weapon.turretDef) || !Finite(weapon.mount) || !Finite(weapon.muzzle)
                    || !Finite(weapon.sight) || !Finite(weapon.barrelPivot) || !Finite(weapon.barrelRestAngles) || !Finite(weapon.boreLocalDirection) || weapon.boreLocalDirection.sqrMagnitude < .000001f || !float.IsFinite(weapon.minPitch) || !float.IsFinite(weapon.maxPitch)
                    || weapon.minPitch > weapon.maxPitch || weapon.minPitch < -89 || weapon.maxPitch > 89
                    || !System.Enum.IsDefined(weapon.scopeTemplate)) yield return "Invalid or duplicate turret profile";
            keys.Clear();
            foreach (VehicleAttachmentView part in attachments)
                if (part == null || string.IsNullOrEmpty(part.graphicPath) || string.IsNullOrEmpty(part.model) || !keys.Add(part.graphicPath)) yield return "Invalid or duplicate attachment";
            foreach (VehicleHitBox box in hitBoxes)
                if (box == null || !Finite(box.center) || !Finite(box.size) || box.size.x <= 0 || box.size.y <= 0 || box.size.z <= 0) yield return "Invalid hit box";
        }
        internal static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    }
    public sealed class VehicleSeatView
    {
        public string roleKey;
        public Vector3 eye;
        public bool scope;
        public string turretKey;
        public Vector3 turretEyeOffset;
    }
    public sealed class VehicleWeaponView
    {
        public string turretKey;
        public string turretDef; // Optional native definition: weapon replacements can keep the same role key.
        public VehicleScopeTemplate scopeTemplate = VehicleScopeTemplate.Auto;
        public string turretModel, barrelModel;
        public bool sightFollowsBarrel = true;
        public bool modelInParent;
        public Vector3 boreLocalDirection = Vector3.forward;
        public bool boreFollowsParentPitch;
        public bool boreFixedToParent;
        public List<Vector3> boreMuzzles = new List<Vector3>();
        // Optional hollow bearing sleeve in turret-local space, sharing its mount and rotation.
        public string mountRingModel;
        public string parentTurretKey; // Mount is parent-turret local when set, otherwise hull local.
        public Vector3 mount = new Vector3(0f, 1.8f, 0f);
        public Vector3 muzzle = new Vector3(0f, 0f, 2f);
        public Vector3 sight = new Vector3(.18f, .2f, .15f);
        public Vector3 barrelPivot;
        public Vector3 barrelRestAngles;
        public float minPitch = -35f, maxPitch = 12f;
    }
    public sealed class VehicleAttachmentView
    {
        public string graphicPath, model;
    }
    public sealed class VehicleRotorView
    {
        public string model;
        public int overlayOrdinal;
        public Vector3 mount;
    }
    public sealed class VehicleHitBox
    {
        public Vector3 center, size = Vector3.one;
        public string turretKey;
    }
}
