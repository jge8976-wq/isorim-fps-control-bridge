using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // Explicit axes and pipe-end centers measured from the delivered embedded meshes.
    public sealed class VehicleBoreBindingDef : Def
    {
        public string profileDefName, turretKey, turretDef, parentTurretKey;
        public Vector3 localDirection = Vector3.forward;
        public List<Vector3> muzzles = new List<Vector3>();
        public bool fixedToParent = true;
        public bool followsParentPitch;
        public Vector3 barrelRestAngles;
        public string barrelModel;
        public Vector3 modelLocalDirection = Vector3.forward;
        public List<Vector3> modelMuzzles = new List<Vector3>();
    }
}
