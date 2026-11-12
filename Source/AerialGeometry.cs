using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialGeometry
    {
        private sealed class FrameStamp { internal int value = -1; }
        private static readonly ConditionalWeakTable<Pawn, FrameStamp> drawn = new();

        internal static void InstallDrawFallback(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(DynamicDrawManager), "DrawDynamicThings"),
                postfix: new HarmonyMethod(typeof(AerialGeometry), nameof(DrawFirstPersonAircraft)));
        }

        private static void DrawFirstPersonAircraft(DynamicDrawManager __instance)
        {
            var map = Find.CurrentMap;
            if (!Api.ModelsOn || map == null || map.dynamicDrawManager != __instance
                || !(FirstPerson.On || VehicleControl.DriverCamera || VehicleControl.Scoped)
                || AerialFlight.Store == null) return;
            foreach (var entry in AerialFlight.Store.Flights)
            {
                Pawn aircraft = entry.Key;
                if (!entry.Value.Airborne || !aircraft.Spawned || aircraft.Map != map || aircraft.Position.Fogged(map)
                    || aircraft == VehicleControl.Vehicle || drawn.GetOrCreateValue(aircraft).value == Time.frameCount) continue;
                if (!IffMarkers.Project(AerialTargetLock.Point(aircraft), out Vector2 screen, out _)
                    || screen.x < -180 || screen.y < -180 || screen.x > UI.screenWidth + 180 || screen.y > UI.screenHeight + 180) continue;
                DrawVehicle(aircraft, DrawPhase.Draw);
            }
        }

        internal static Quaternion Rotation(AerialMotor m) => Quaternion.Euler(m.Pitch, m.Yaw, m.Roll);
        private static Vector3 Root(Pawn v)
        {
            if(AerialFlight.TryGet(v,out var r))return VehicleModels.U(r.Motor.Position);
            Vector3 at=(Vector3)VehicleApi.Call(v,"TrueCenter");
            FloorBand band=FloorSpace.Band(v.Map,v.Position);
            return new Vector3(at.x,band.Height-LocalBounds(v).min.y,at.z-band.Shift);
        }
        internal static Quaternion SourceRotation(Pawn v)=>Quaternion.FromToRotation(AerialDeparture.ModelForward(v),Vector3.forward);
        internal static Quaternion Hull(Pawn v) => (AerialFlight.TryGet(v,out var r)?Rotation(r.Motor):Quaternion.Euler(0,VehicleApi.Yaw(v),0))*SourceRotation(v);
        internal static Bounds LocalBounds(Pawn v)
        {
            var profile = VehicleModels.Profile(v);
            ModelHandle body = VehicleModels.Load(profile, profile.bodyModel);
            if (body != null)
            {
                Vector3 min = Vector3.Scale(body.Min, profile.modelScale), max = Vector3.Scale(body.Max, profile.modelScale);
                return new Bounds((min + max) * .5f, max - min);
            }
            return new Bounds(Vector3.up * profile.hullHeight * .5f, new Vector3(v.def.size.x, profile.hullHeight, v.def.size.z));
        }
        internal static Bounds BoundsAt(Pawn v, AerialMotor motor)
        {
            Bounds local = LocalBounds(v); Quaternion q = Rotation(motor)*SourceRotation(v); Vector3 at = VehicleModels.U(motor.Position);
            Vector3 right = q * Vector3.right, up = q * Vector3.up, forward = q * Vector3.forward, e = local.extents;
            Vector3 half = new Vector3(Math.Abs(right.x)*e.x+Math.Abs(up.x)*e.y+Math.Abs(forward.x)*e.z,
                Math.Abs(right.y)*e.x+Math.Abs(up.y)*e.y+Math.Abs(forward.y)*e.z, Math.Abs(right.z)*e.x+Math.Abs(up.z)*e.y+Math.Abs(forward.z)*e.z);
            return new Bounds(at + q * local.center, half * 2f);
        }
        internal static Vector3 World(Pawn v, Vector3 local)
        { return Root(v)+Hull(v)*local; }
        internal static Quaternion TurretRotation(Pawn v, object t)
        {
            if(AerialMounts.Fixed(v,t))return AerialMounts.Rotation(v);
            float yaw=AerialFlight.TryGet(v,out var r)?r.Motor.Yaw:VehicleApi.Yaw(v);
            return Hull(v)*Quaternion.Euler(0f,Mathf.DeltaAngle(yaw,VehicleApi.Float(t,"TurretRotation")),0f);
        }
        internal static Vector3 Mount(Pawn v, object t, int depth = 0)
        {
            var part = VehicleModels.Weapon(v, t);
            if (!string.IsNullOrEmpty(part.parentTurretKey) && depth < 8)
            {
                object parent = VehicleApi.Turrets(v).FirstOrDefault(p => VehicleApi.TurretKey(p) == part.parentTurretKey);
                if (parent != null && parent != t) return Mount(v, parent, depth + 1) + TurretRotation(v, parent) * part.mount;
            }
            return World(v, part.mount);
        }
        // A stabilized gun retains the player's world aim while its mounting follows the moving airframe.
        internal static Quaternion Aim(object t) => AerialMounts.Fixed(VehicleApi.Get(t,"vehicle") as Pawn,t) ? AerialMounts.Rotation(VehicleApi.Get(t,"vehicle") as Pawn) : Quaternion.Euler(VehicleWeapons.PitchOf(t), VehicleApi.Float(t, "TurretRotation"), 0f);
        internal static Vector3 Muzzle(Pawn v, object t)
        {
            var part = VehicleModels.Weapon(v, t);
            var shifts = VehicleApi.List(VehicleApi.Get(t, "def"), "projectileShifting").ToList();
            int i = VehicleApi.Int(t, "CurrentTurretFiring");
            float shift = shifts.Count == 0 ? 0f : Convert.ToSingle(shifts[i < 0 || i >= shifts.Count ? 0 : i]);
            return Mount(v, t) + TurretRotation(v, t) * part.barrelPivot + Aim(t) * (part.muzzle - part.barrelPivot + Vector3.right * shift);
        }
        internal static Vector3 Sight(Pawn v, object t)
        {
            if(AerialFlight.Supports(v))return VehicleModels.Muzzle(v,t)+VehicleModels.U(VehicleWeapons.Direction(v,t))*.25f;
            var part = VehicleModels.Weapon(v, t);
            return part.sightFollowsBarrel ? Mount(v, t) + TurretRotation(v, t) * part.barrelPivot + Aim(t) * (part.sight - part.barrelPivot)
                : Mount(v,t) + TurretRotation(v,t)*part.sight;
        }
        private static void Draw(Pawn v, ModelHandle model, Vector3 at, Quaternion rotation, Matrix4x4 local)
        { if (model != null) Api.DrawModel(model, AerialFlight.Raw(v, at), 0f, Matrix4x4.Rotate(rotation) * local, v.DrawColor); }
        public static bool DrawVehicle(Pawn __instance, DrawPhase phase)
        {
            if (phase != DrawPhase.Draw || !Api.ViewReady || !Api.ModelsOn || !__instance.Spawned || !AerialFlight.Supports(__instance)) return true;
            AerialFlight.TryGet(__instance,out var r);
            if (__instance.Position.Fogged(__instance.Map)) return false;
            // A weapon optic is an external sensor: own airframe/pods cannot occlude it.
            if(VehicleControl.Vehicle==__instance&&VehicleControl.Scoped)return false;
            if(AerialCockpit.Draw(__instance))return false;
            var parts=new List<VehicleGeometry.Part>();
            AerialMotor physical=r==null?VehicleGeometry.Pose(__instance):r.Motor;
            if(!VehicleGeometry.Parts(__instance,physical,parts))return true;
            drawn.GetOrCreateValue(__instance).value = Time.frameCount;
            VehicleGeometry.Draw(__instance,parts);
            if(r!=null)AerialEffects.Draw(__instance,r);
            return false;
        }
        private static HitVolume Box(Vector3 center, Vector3 size, Quaternion q) => new HitVolume { Kind = VolumeKind.Box, Region = HitRegion.Solid,
            A = VehicleModels.V(center), B = VehicleModels.V(size * .5f), Part = -1, Leg = -1, Oriented = true,
            AxisRight = VehicleModels.V(q * Vector3.right), AxisUp = VehicleModels.V(q * Vector3.up), AxisForward = VehicleModels.V(q * Vector3.forward) };
        internal static void Volumes(Pawn v, List<HitVolume> into)
        {
            var profile = VehicleModels.Profile(v);
            var m = AerialFlight.Store.Flights[v].Motor;
            if (profile.hitBoxes.Count == 0)
            {
                Bounds b = LocalBounds(v); into.Add(Box(World(v, b.center), b.size, Hull(v))); return;
            }
            foreach (var b in profile.hitBoxes)
            {
                object t = string.IsNullOrEmpty(b.turretKey) ? null : VehicleApi.Turrets(v).FirstOrDefault(t => VehicleApi.TurretKey(t) == b.turretKey);
                if (!string.IsNullOrEmpty(b.turretKey) && t == null) continue;
                Quaternion q = t == null ? Hull(v) : TurretRotation(v, t);
                Vector3 at = t == null ? World(v, b.center) : Mount(v, t) + q * b.center;
                into.Add(Box(at, b.size, q));
            }
        }
    }
}
