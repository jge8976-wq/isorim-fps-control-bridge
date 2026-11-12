using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class AerialTurretTargeting
    {
        private sealed class Lead
        {
            internal int Tick = -1, LastTurnTick = -1;
            internal Pawn Target;
            internal ThingDef Projectile;
            internal V3 Direction;
        }
        private sealed class AirTargets
        {
            internal int Tick = -1, RecordCount = -1;
            internal readonly List<Pawn> Planes = new();
        }
        private static readonly ConditionalWeakTable<Building_Turret, Lead> aim = new();
        private static readonly ConditionalWeakTable<Map, AirTargets> airTargets = new();
        private static Type ceTurret;

        private static List<Pawn> Candidates(Map map)
        {
            var scene = airTargets.GetOrCreateValue(map);
            int tick = Find.TickManager.TicksGame;
            var flights = AerialFlight.Store.Flights;
            if (scene.Tick == tick && scene.RecordCount == flights.Count) return scene.Planes;
            scene.Tick = tick;
            scene.RecordCount = flights.Count;
            scene.Planes.Clear();
            foreach (var entry in flights)
            {
                Pawn plane = entry.Key;
                if (entry.Value.Airborne && plane.Spawned && !plane.Dead && plane.Map == map
                    && VehicleApi.IsVehicle(plane) && !plane.IsPsychologicallyInvisible()) scene.Planes.Add(plane);
            }
            return scene.Planes;
        }
        internal static void Install(Harmony harmony)
        {
            var find = new HarmonyMethod(typeof(AerialTurretTargeting), nameof(AfterFind));
            var methods = new HashSet<System.Reflection.MethodInfo>();
            foreach (Type type in GenTypes.AllTypes)
            {
                if (!typeof(Building_Turret).IsAssignableFrom(type)) continue;
                var method = AccessTools.DeclaredMethod(type, "TryFindNewTarget", Type.EmptyTypes);
                if (method != null && !method.IsAbstract && method.ReturnType == typeof(LocalTargetInfo) && methods.Add(method))
                    harmony.Patch(method, postfix: find);
            }
            var ce = ceTurret = AccessTools.TypeByName("CombatExtended.Building_TurretGunCE");
            if (ce != null)
            {
                harmony.Patch(AccessTools.Method(ce, "TryStartShootSomething", new[] { typeof(bool) }),
                    prefix: new HarmonyMethod(typeof(AerialTurretTargeting), nameof(ContinueTraverse)));
                var delta = AccessTools.PropertyGetter(ce, "DeltaAngle");
                if (delta != null) harmony.Patch(delta, postfix: new HarmonyMethod(typeof(AerialTurretTargeting), nameof(AfterAngle)));
            }
            harmony.Patch(AccessTools.Method(typeof(TurretTop), nameof(TurretTop.TurretTopTick)),
                postfix: new HarmonyMethod(typeof(AerialTurretTargeting), nameof(AfterTop)));
            harmony.Patch(AccessTools.Method(typeof(GenUI), nameof(GenUI.TargetsAtMouse)),
                postfix: new HarmonyMethod(typeof(AerialTurretTargeting), nameof(AfterMouseTargets)));
            harmony.Patch(AccessTools.Method(typeof(Verb), "TryFindShootLineFromTo", new[]
                { typeof(IntVec3), typeof(LocalTargetInfo), typeof(ShootLine).MakeByRefType(), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(AerialTurretTargeting), nameof(BeforeLine)) { priority = Priority.First + 300 });
        }

        internal static float ShotSpeed(Verb verb, ThingDef projectile)
        {
            float speed = CeCompat.Owns(verb) ? VehicleApi.Float(verb, "ShotSpeed") / 60f : 0f;
            if (!float.IsFinite(speed) || speed <= 0f) speed = projectile.projectile.SpeedTilesPerTick;
            return ViewRules.Speed(speed, FpsMod.Settings.speed);
        }

        internal static bool TryAim(Building_Turret turret, out V3 direction)
        {
            direction = default;
            if (turret == null || TurretControl.Current?.Thing == turret
                || !AerialCombat.Target(turret.CurrentTarget, out var plane)) return false;
            var verb = turret.AttackVerb;
            if (!DirectTurret(verb)) return false;
            var projectile = CeCompat.ProjectileFor(verb) ?? (verb as Verb_LaunchProjectile)?.Projectile;
            var cached = aim.GetOrCreateValue(turret);
            int tick = Find.TickManager.TicksGame;
            if (cached.Tick != tick || cached.Target != plane || cached.Projectile != projectile)
            {
                cached.Tick = tick;
                cached.Target = plane;
                cached.Projectile = projectile;
                V3 start = VehicleModels.V(AerialCombat.Origin(turret));
                V3 point = VehicleModels.V(AerialTargetLock.Point(plane));
                float speed = ShotSpeed(verb, projectile);
                if (!AerialCombat.Intercept(plane, start, point, speed, FpsMod.Settings.antiAirLead, out var bore))
                    bore = point - start;
                start = AerialTurretFire.Muzzle(turret, bore);
                if (!AerialCombat.Intercept(plane, start, point, speed, FpsMod.Settings.antiAirLead, out cached.Direction))
                    cached.Direction = point - start;
            }
            direction = cached.Direction;
            return direction.Length > .001f;
        }

        private static float Heading(V3 direction) => Mathf.Repeat(Mathf.Atan2(direction.X, direction.Z) * Mathf.Rad2Deg, 360f);

        private static void AfterTop(TurretTop __instance, Building_Turret ___parentTurret)
        {
            if (!TryAim(___parentTurret, out var direction)) return;
            float heading = Heading(direction);
            __instance.CurRotation = heading;
            if (ceTurret?.IsInstanceOfType(___parentTurret) != true
                || VehicleApi.Get(___parentTurret, "NonSnapExtension") is not object traverse) return;
            var cached = aim.GetOrCreateValue(___parentTurret);
            int tick = Find.TickManager.TicksGame;
            if (cached.LastTurnTick == tick) return;
            cached.LastTurnTick = tick;
            // CE adds its own traverse after this callback. Help slow emplacements keep up with air targets.
            float extra = Math.Max(0f, .65f - VehicleApi.Float(traverse, "speed"));
            float current = VehicleApi.Float(___parentTurret, "NonSnapTurretRot");
            VehicleApi.Set(___parentTurret, "NonSnapTurretRot", Mathf.MoveTowardsAngle(current, heading, extra));
        }

        private static bool ContinueTraverse(Building_Turret __instance)
        {
            if (VehicleApi.Int(__instance, "burstWarmupTicksLeft") != 1
                || VehicleApi.Bool(__instance, "holdFire") || !AerialCombat.Target(__instance.CurrentTarget, out var target)
                || VehicleApi.Get(__instance, "NonSnapExtension") is not object traverse
                || Math.Abs(VehicleApi.Float(__instance, "DeltaAngle")) <= VehicleApi.Float(traverse, "speed")) return true;
            // Keep the final warmup tick while the top catches up instead of restarting the entire warmup.
            return !CanEngage(__instance.AttackVerb, target, __instance.Position);
        }

        private static void AfterAngle(Building_Turret __instance, ref float __result)
        {
            if (TryAim(__instance, out var direction))
                __result = Mathf.DeltaAngle(VehicleApi.Float(__instance, "NonSnapTurretRot"), Heading(direction));
        }

        internal static bool DirectTurret(Verb verb)
        {
            if (verb?.Caster is not Building_Turret turret || turret.def.building?.IsMortar == true) return false;
            var projectile = CeCompat.ProjectileFor(verb) ?? (verb as Verb_LaunchProjectile)?.Projectile;
            return projectile?.projectile is { flyOverhead: false };
        }

        internal static bool CanEngage(Verb verb, Pawn plane, IntVec3 root)
        {
            if (!AerialCombat.InReach(verb, plane, root) || plane.Position.Fogged(plane.Map)) return false;
            var turret = (Building_Turret)verb.Caster;
            Vector3 origin = AerialCombat.Origin(turret);
            var own = FloorSpace.Band(turret.Map, turret.Position);
            var band = FloorSpace.Band(turret.Map, root);
            origin += new Vector3(root.x - turret.Position.x, band.Height - own.Height,
                root.z - band.Shift - (turret.Position.z - own.Shift));
            Vector3 point = AerialTargetLock.Point(plane);
            if ((point - origin).magnitude < verb.verbProps.minRange) return false;
            V3 end = VehicleModels.V(point);
            return !Ballistics.SightContact(turret, VehicleModels.V(origin), end, out V3 hit, false)
                || (hit - end).Length < .25f;
        }

        private static void AfterFind(Building_Turret __instance, ref LocalTargetInfo __result)
        {
            if (__result.IsValid || !__instance.Spawned || !DirectTurret(__instance.AttackVerb)
                || !__instance.AttackVerb.Available() || AerialFlight.Store == null) return;
            float best = float.PositiveInfinity;
            Vector3 origin = AerialCombat.Origin(__instance);
            foreach (Pawn plane in Candidates(__instance.Map))
            {
                if (!plane.HostileTo(__instance)) continue;
                float distance = (AerialTargetLock.Point(plane) - origin).sqrMagnitude;
                if (distance >= best || !CanEngage(__instance.AttackVerb, plane, __instance.Position)) continue;
                var target = new LocalTargetInfo(plane);
                var arc = AccessTools.Property(__instance.GetType(), "CompFireArc")?.GetValue(__instance);
                if (arc != null && VehicleApi.Call(arc, "WithinFireArc", target) is false) continue;
                best = distance;
                __result = target;
            }
        }

        private static bool BeforeLine(Verb __instance, IntVec3 __0, LocalTargetInfo __1, ref ShootLine __2, ref bool __result)
        {
            if (!DirectTurret(__instance) || !AerialCombat.Target(__1, out var plane)) return true;
            __2 = new ShootLine(__0, __1.Cell);
            __result = CanEngage(__instance, plane, __0);
            return false;
        }

        private static void AfterMouseTargets(TargetingParameters clickParams, ITargetingSource source,
            ref IEnumerable<LocalTargetInfo> __result)
        {
            if (!View3D.Active || !DirectTurret(source?.GetVerb) || AerialFlight.Store == null) return;
            Pawn plane = VehicleModelPicking.Pick();
            if (plane == null) plane = PickAircraft(source, clickParams);
            if (plane == null || !AerialFlight.Airborne(plane) || !plane.HostileTo(source.Caster)
                || !clickParams.CanTarget(plane, source)) return;
            __result = First(new LocalTargetInfo(plane), __result);
        }

        private static Pawn PickAircraft(ITargetingSource source, TargetingParameters parameters)
        {
            var map = source.Caster.Map;
            if (map == null) return null;
            VehicleModelPicking.MouseRay(out Vector3 eye, out Vector3 direction);
            var start = VehicleMeshContact.N3(eye);
            var ray = VehicleMeshContact.N3(direction.normalized);
            float best = float.PositiveInfinity;
            Pawn selected = null;
            var volumes = new List<HitVolume>();
            foreach (var aircraft in Candidates(map))
            {
                if (!aircraft.HostileTo(source.Caster) || aircraft.Position.Fogged(map)
                    || !parameters.CanTarget(aircraft, source)
                    || !AerialFlight.TryGet(aircraft, out var record)) continue;
                Bounds bounds = AerialGeometry.BoundsAt(aircraft, record.Motor);
                var box = new MeshContactMath.Box(VehicleMeshContact.N3(bounds.min), VehicleMeshContact.N3(bounds.max));
                if (!MeshContactMath.RayBox(start, ray, box, View3D.NearCard * .5f, best)) continue;
                volumes.Clear();
                AerialGeometry.Volumes(aircraft, volumes);
                V3 from = VehicleModels.V(eye), to = from + VehicleModels.V(direction.normalized) * 600f;
                foreach (var volume in volumes)
                    if (HitMath.Enter(from, to, volume, out float fraction) && fraction > .0001f && fraction * 600f < best)
                    {
                        best = fraction * 600f;
                        selected = aircraft;
                    }
            }
            return selected;
        }

        private static IEnumerable<LocalTargetInfo> First(LocalTargetInfo aircraft, IEnumerable<LocalTargetInfo> other)
        {
            yield return aircraft;
            foreach (var target in other) if (target.Thing != aircraft.Thing) yield return target;
        }
    }
}
