using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static partial class CeCompat
    {
        private static FieldInfo nativeVelocity, nativeExactPosition, nativeFlightTicks, nativeOrigin;
        private static PropertyInfo nativeWorker;
        private static MethodInfo nativeMotionLaunch;
        private static Type ballisticWorkerType;
        private static object ballisticMotionWorker;
        private static MethodInfo nativeLayoutOf, nativeLayoutToWorld, nativeLayoutFromWorld;
        private static PropertyInfo nativeUnitScale;
        private static VectorAccess layoutVector;
        [ThreadStatic] private static Map nativePreviewMap;
        private static readonly Dictionary<Type, MethodInfo> nativeMoves = new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<Type, MethodInfo> nativeNotifications = new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<Type, MethodInfo> nativeAimAngles = new Dictionary<Type, MethodInfo>();

        private static void InstallNativeMotion(MethodInfo launch, Harmony harmony)
        {
            nativeMotionLaunch = launch;
            nativeVelocity = AccessTools.Field(projectileType, "velocity");
            nativeExactPosition = AccessTools.Field(projectileType, "exactPosition");
            nativeFlightTicks = AccessTools.Field(projectileType, "FlightTicks");
            nativeOrigin = AccessTools.Field(projectileType, "origin");
            nativeWorker = Property(projectileType, "TrajectoryWorker");
            ballisticWorkerType = Named("BallisticsTrajectoryWorker");
            if (nativeVelocity?.FieldType != exactPos.PropertyType || nativeExactPosition?.FieldType != exactPos.PropertyType
                || nativeFlightTicks?.FieldType != typeof(int) || nativeOrigin == null || ballisticWorkerType == null)
                throw new MissingMemberException("CE native trajectory velocity, position, time or origin contract");
            ballisticMotionWorker = Activator.CreateInstance(ballisticWorkerType);
            Type layout = AccessTools.TypeByName("IsoRimCEPackZ.Layout");
            if (layout != null)
            {
                nativeLayoutOf = AccessTools.Method(layout, "Of", new[] { typeof(Map) });
                nativeLayoutToWorld = AccessTools.Method(layout, "ToVirtual", new[] { exactPos.PropertyType, typeof(int) });
                nativeLayoutFromWorld = AccessTools.Method(layout, "FromVirtual", new[] { exactPos.PropertyType, typeof(int) });
                nativeUnitScale = AccessTools.Property(layout, "CellsPerUnit");
                if (nativeLayoutOf == null || nativeLayoutToWorld == null || nativeLayoutFromWorld == null || nativeUnitScale == null)
                    throw new MissingMethodException("CEPackZ Layout.Of/ToVirtual/FromVirtual/CellsPerUnit");
                layoutVector = new VectorAccess(nativeLayoutToWorld.ReturnType, 3);
                harmony.Patch(nativeLayoutOf, prefix: new HarmonyMethod(typeof(CeCompat), nameof(PreviewLayoutMap)));
            }
            else
                nativeUnitScale = AccessTools.Property(AccessTools.TypeByName("IsoRimCEPack.ProjectileCE_DrawAt"), "CellsPerUnit");
        }
        private static void PreviewLayoutMap(ref Map __0)
        {
            if (__0 == null && nativePreviewMap != null) __0 = nativePreviewMap;
        }

        internal static float NativeVerticalScale => nativeUnitScale == null ? Api.WallHeight / 2f : Convert.ToSingle(nativeUnitScale.GetValue(null));
        internal static V3 NativeVelocityToWorld(V3 value) => new V3(value.X, value.Y * NativeVerticalScale, value.Z);
        internal static V3 WorldVelocityToNative(V3 value) => new V3(value.X, value.Y / NativeVerticalScale, value.Z);
        internal static V3 NativeShotVelocity(V3 worldDirection, float nativeSpeed)
        {
            V3 native = WorldVelocityToNative(worldDirection);
            return NativeVelocityToWorld(native * (nativeSpeed / Math.Max(.00001f, native.Length)));
        }
        internal static V3 NativePointToWorld(Map map, Vector3 native, FloorBand band)
        {
            object layout = nativeLayoutOf?.Invoke(null, new object[] { map });
            if (layout != null)
                return VehicleModels.V(layoutVector.Read3(nativeLayoutToWorld.Invoke(layout, new object[] { layoutVector.Make3(native), band.Level })));
            return new V3(native.x, band.Height + native.y * NativeVerticalScale, native.z - band.Shift);
        }
        internal static Vector3 WorldPointToNative(Map map, V3 world, FloorBand band)
        {
            object layout = nativeLayoutOf?.Invoke(null, new object[] { map });
            if (layout != null)
                return layoutVector.Read3(nativeLayoutFromWorld.Invoke(layout, new object[] { layoutVector.Make3(VehicleModels.U(world)), band.Level }));
            return new Vector3(world.X, (world.Y - band.Height) / NativeVerticalScale, world.Z + band.Shift);
        }
        private static object MotionWorker(Thing projectile, bool velocityMotion)
        {
            object worker = nativeWorker.GetValue(projectile);
            return velocityMotion && !ballisticWorkerType.IsInstanceOfType(worker) ? ballisticMotionWorker : worker;
        }

        private static void PrepareNativeVelocity(Thing projectile, V3 velocity) =>
            nativeVelocity.SetValue(projectile, vector3.Make3(VehicleModels.U(WorldVelocityToNative(velocity))));

        private static V3 ReadNativeVelocity(Thing projectile) => NativeVelocityToWorld(VehicleModels.V(vector3.Read3(nativeVelocity.GetValue(projectile))));

        internal static FloorBand NativeOriginBand(Thing projectile)
        {
            object origin = nativeOrigin.GetValue(projectile);
            float x = (float)AccessTools.Field(origin.GetType(), "x").GetValue(origin);
            float z = (float)AccessTools.Field(origin.GetType(), "y").GetValue(origin);
            return FloorSpace.Band(projectile.Map, new IntVec3(Mathf.FloorToInt(x), 0, Mathf.FloorToInt(z)));
        }

        internal static V3 NativeMotionStep(Thing projectile, Map map, V3 position, ref V3 velocity, FloorBand band, bool velocityMotion)
        {
            object worker = MotionWorker(projectile, velocityMotion);
            // FPS seeker steering and free-fall bomb release are velocity commands.
            // Use CE's ballistic worker for those commands when a lerped worker
            // would otherwise ignore velocity and return its old target interpolation.
            Type type = worker.GetType();
            if (!nativeMoves.TryGetValue(type, out MethodInfo move))
            {
                move = AccessTools.Method(type, "MoveForward", new[] { projectileType });
                if (move == null || move.ReturnType != exactPos.PropertyType)
                    throw new MissingMethodException(type.FullName, "MoveForward(ProjectileCE)");
                nativeMoves.Add(type, move);
            }
            Vector3 from = WorldPointToNative(map, position, band);
            object previousPosition = nativeExactPosition.GetValue(projectile);
            PrepareNativeVelocity(projectile, velocity);
            nativeExactPosition.SetValue(projectile, vector3.Make3(from));
            try
            {
                Vector3 next = vector3.Read3(move.Invoke(worker, new object[] { projectile }));
                V3 result = NativePointToWorld(map, next, band);
                velocity = ballisticWorkerType.IsInstanceOfType(worker)
                    ? ReadNativeVelocity(projectile) : result - position;
                if (!result.Finite || !velocity.Finite) throw new InvalidOperationException("CE native trajectory produced a nonfinite step.");
                return result;
            }
            finally { nativeExactPosition.SetValue(projectile, previousPosition); }
        }

        internal sealed class NativeMotionPreview
        {
            internal Thing Projectile;
            internal Map Map;
            internal V3 Position, Velocity;
            internal FloorBand Band;
            internal bool VelocityMotion;
            internal V3 Step(V3 commandedVelocity)
            {
                if (Projectile.Spawned) throw new InvalidOperationException("A native trajectory preview unexpectedly became spawned.");
                object worker = MotionWorker(Projectile, VelocityMotion);
                Type type = worker.GetType();
                if (!nativeNotifications.TryGetValue(type, out MethodInfo notify))
                {
                    notify = AccessTools.Method(type, "NotifyTicked", new[] { projectileType });
                    if (notify == null) throw new MissingMethodException(type.FullName, "NotifyTicked(ProjectileCE)");
                    nativeNotifications.Add(type, notify);
                }
                Map previous = nativePreviewMap;
                nativePreviewMap = Map;
                try
                {
                    PrepareNativeVelocity(Projectile, commandedVelocity);
                    notify.Invoke(worker, new object[] { Projectile });
                    int tick = (int)nativeFlightTicks.GetValue(Projectile);
                    nativeFlightTicks.SetValue(Projectile, tick + 1);
                    Velocity = ReadNativeVelocity(Projectile);
                    Position = NativeMotionStep(Projectile, Map, Position, ref Velocity, Band, VelocityMotion);
                    return Position;
                }
                finally { nativePreviewMap = previous; }
            }
        }

        internal static V3 NativeAim(Map map, FloorBand band, ThingDef definition, V3 source, V3 target, float speed)
        {
            object props = definition.projectile;
            object worker = Property(props.GetType(), "TrajectoryWorker").GetValue(props);
            if (!nativeAimAngles.TryGetValue(worker.GetType(), out MethodInfo angle))
            {
                angle = AccessTools.Method(worker.GetType(), "ShotAngle", new[] { Named("ProjectilePropertiesCE"), exactPos.PropertyType, exactPos.PropertyType, typeof(float?) });
                if (angle == null) throw new MissingMethodException(worker.GetType().FullName, "ShotAngle(ProjectilePropertiesCE,Vector3,Vector3,float?)");
                nativeAimAngles.Add(worker.GetType(), angle);
            }
            float elevation = Convert.ToSingle(angle.Invoke(worker, new object[] { props, vector3.Make3(WorldPointToNative(map, source, band)), vector3.Make3(WorldPointToNative(map, target, band)), (float?)(speed * 60f) }));
            V3 delta = target - source;
            V3 direction = NativeVelocityToWorld(HitMath.Direction(MathF.Atan2(delta.X, delta.Z) * Mathf.Rad2Deg, -elevation * Mathf.Rad2Deg));
            return direction * (1f / Math.Max(.00001f, direction.Length));
        }

        internal static NativeMotionPreview CreateNativeMotionPreview(Pawn launcher, ThingDef definition, V3 start, V3 velocity, Thing target, bool velocityMotion, LocalTargetInfo? intendedTarget = null, float? launchSpeed = null)
        {
            if (!NativeProjectile(definition)) return null;
            var preview = new NativeMotionPreview
            {
                Projectile = (Thing)Activator.CreateInstance(definition.thingClass),
                Map = launcher.Map, Position = start, Velocity = velocity, Band = FloorSpace.Band(launcher.Map, launcher.Position), VelocityMotion = velocityMotion
            };
            preview.Projectile.def = definition;
            AccessTools.Field(projectileType, "intendedTarget").SetValue(preview.Projectile, target == null ? intendedTarget ?? LocalTargetInfo.Invalid : new LocalTargetInfo(target));
            V3 nativeVelocityValue = WorldVelocityToNative(velocity);
            CeNativeRules.TryAngles(nativeVelocityValue.X, nativeVelocityValue.Y, nativeVelocityValue.Z, out float angle, out float rotation);
            Vector3 origin = WorldPointToNative(launcher.Map, start, preview.Band);
            // CE checks this cell against the launcher's roof grid before its
            // inner Launch assigns ExactPosition. An unspawned Thing's setter
            // stores only the cell and does not register it with any map grid.
            preview.Projectile.Position = new IntVec3(Mathf.FloorToInt(origin.x), 0, Mathf.FloorToInt(origin.z)).ClampInsideMap(launcher.Map);
            Thing previousCePackLaunch = cePackLaunch;
            cePackLaunch = preview.Projectile;
            Rand.PushState(0);
            try
            {
                nativeMotionLaunch.Invoke(preview.Projectile, new object[] { launcher, vector2.Make2(new Vector2(origin.x, origin.z)), angle, rotation,
                    Math.Max(.001f, origin.y), (launchSpeed ?? nativeVelocityValue.Length) * 60f, launcher, -1f });
            }
            finally { Rand.PopState(); cePackLaunch = previousCePackLaunch; }
            if (preview.Projectile.Spawned) throw new InvalidOperationException("A ballistic preview must remain unspawned.");
            PrepareNativeVelocity(preview.Projectile, velocity);
            return preview;
        }
    }
}
