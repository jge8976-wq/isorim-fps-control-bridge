using System;
using System.Collections.Generic;
using IsoRim;
using UnityEngine;
using Verse;
namespace HSK.FpsBridge
{
    // Main thread, visual-only. Anchors and directions use RAW model coordinates.
    public sealed class HeldWeaponModel
    {
        public Model model;
        public bool rawObjCoordinates;
        public string id;
        public Vector3 forward=Vector3.forward, up=Vector3.up;
        public Vector3? grip, muzzle, rearSight, frontSight, scopeEye, scopeForward;
        public string kind="Gun", scope="Auto";
        public float rearDistance=.22f, magnification=1.35f;
        public float scopeZoom;
    }
    public sealed class HeldWeaponModelExtension : DefModExtension
    {
        public bool disableAutomatic, overrideOrientation, hasGrip, hasMuzzle, hasSights, hasScopeEye;
        public bool rawObjCoordinates=true;
        public Vector3 forward=Vector3.forward,up=Vector3.up,grip,muzzle,rearSight,frontSight;
        public Vector3 scopeEye,scopeForward;
        public string scope="Auto",kind;
        public float rearDistance=.22f,magnification=1.35f;
        public float scopeZoom;
    }
    public static class HeldWeaponModelsApi
    {
        public const int Version=3;
        private static readonly List<KeyValuePair<string,Func<Thing,HeldWeaponModel>>> sources=new();
        public static int Generation {get;private set;}
        public static void Register(string owner,Func<Thing,HeldWeaponModel> source)
        {
            if(string.IsNullOrWhiteSpace(owner)||source==null)throw new ArgumentException("owner and source are required");
            Unregister(owner);sources.Add(new(owner,source));Generation++;
        }
        public static void Unregister(string owner){if(sources.RemoveAll(p=>p.Key==owner)>0)Generation++;}
        public static void Invalidate(){Generation++;}
        internal static HeldWeaponModel Provided(Thing weapon)
        {
            for(int i=sources.Count-1;i>=0;i--)
                try{var m=sources[i].Value(weapon);if(m?.model?.positions?.Length>=3)return m;}
                catch(Exception e){Log.WarningOnce("[FpsBridge] Held model provider "+sources[i].Key+": "+e.Message,sources[i].Key.GetHashCode());}
            return null;
        }
    }
}
