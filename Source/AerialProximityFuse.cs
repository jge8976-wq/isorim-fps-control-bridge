
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace HSK.FpsBridge
{
    internal static class AerialProximityFuse
    {
        private sealed class Fuse
        {
            internal Pawn Target;
            internal int Started, NativeTicks;
        }

        private sealed record Burst(Map Map, V3 Position, V3[] Rays, int Tick, float Radius);
        private static readonly ConditionalWeakTable<Thing, Fuse> armed = new();
        private static readonly List<Burst> visuals = new();
        private static readonly FieldInfoProxy nativeTimer = new();
        private static Func<Thing, Thing, bool> isEnabled;
        internal static int Bursts, ShardHits, ShockHits, DirectHits;
        internal static float ShockDamage, ShardDamage, StressDamage;

        private sealed class FieldInfoProxy
        {
            private readonly Type bursting = AccessTools.TypeByName("CombatExtended.ProjectileCE_Bursting");
            internal System.Reflection.FieldInfo For(Thing projectile) =>
                bursting?.IsInstanceOfType(projectile) == true ? AccessTools.Field(bursting, "ticksToBurst") : null;
        }

        internal static void Bind(Func<Thing, Thing, bool> enabled) => isEnabled = enabled;

        internal static bool Airburst(ThingDef def) => def != null &&
            (def.thingClass?.Name.Contains("Bursting", StringComparison.OrdinalIgnoreCase) == true
             || def.defName.Contains("Airburst", StringComparison.OrdinalIgnoreCase)
             || def.defName.Contains("TFuzed", StringComparison.OrdinalIgnoreCase));

        internal static void Install(Harmony harmony)
        {
            var prefix = new HarmonyMethod(typeof(AerialProximityFuse), nameof(Expose));
            foreach (Type type in new[] { typeof(Projectile), AccessTools.TypeByName("CombatExtended.ProjectileCE_Bursting") })
            {
                if (type != null)
                    harmony.Patch(AccessTools.DeclaredMethod(type, "ExposeData"), postfix: prefix);
            }
        }

        private static void Expose(Thing __instance)
        {
            if (!Airburst(__instance.def)) return;
            var state = armed.GetOrCreateValue(__instance);
            Scribe_References.Look(ref state.Target, "isorimAirDefenseFuzeTarget");
            Scribe_Values.Look(ref state.Started, "isorimAirDefenseFuzeTick");
            Scribe_Values.Look(ref state.NativeTicks, "isorimAirDefenseFuzeNativeTimer", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && state.Target == null) armed.Remove(__instance);
        }

        internal static void Arm(Thing projectile, Thing launcher, Thing equipment, LocalTargetInfo intended)
        {
            if (isEnabled == null || launcher?.Map == null || !Airburst(projectile.def)
                || projectile.def.projectile?.damageDef?.harmsHealth != true
                || !AerialCombat.Target(intended, out var plane) || !VehicleApi.IsVehicle(plane)
                || !plane.HostileTo(launcher) || !isEnabled(launcher, equipment)) return;
            var timer = nativeTimer.For(projectile);
            var state = armed.GetOrCreateValue(projectile);
            if (state.Target != null) return;
            state.Target = plane;
            state.Started = Find.TickManager.TicksGame;
            state.NativeTicks = (int?)timer?.GetValue(projectile) ?? -1;
            timer?.SetValue(projectile, -1);
        }

        internal static bool IsArmed(Thing projectile) => armed.TryGetValue(projectile, out var state) && state.Target != null;

        internal static bool TryBurst(Thing projectile, Thing launcher, V3 start, V3 end)
        {
            if (!armed.TryGetValue(projectile, out var state) || state.Target == null) return false;
            var target = state.Target;
            if (!target.Spawned || target.Dead || target.Map != projectile.Map || !AerialFlight.Airborne(target) || !target.HostileTo(launcher))
            {
                if (state.NativeTicks > 0) nativeTimer.For(projectile)?.SetValue(projectile,
                    Math.Max(1, state.NativeTicks - (Find.TickManager.TicksGame - state.Started)));
                armed.Remove(projectile);
                return false;
            }
            V3 center = VehicleModels.V(AerialTargetLock.Point(target));
            V3 step = end - start;
            float travel = V3.Dot(step, step);
            if (travel < .000001f) return false;
            float nearest = V3.Dot(center - start, step) / travel;
            if (nearest < 0f || nearest > 1f) return false;
            V3 position = V3.Lerp(start, end, nearest);
            float radius = Mathf.Clamp(CeCompat.VehicleBlastRadius(projectile.def) * 3f, 4f, 10f);
            var volumes = new List<HitVolume>();
            AerialGeometry.Volumes(target,volumes);
            V3 ahead = end + step * (radius / step.Length);
            if (volumes.Any(volume => HitMath.Enter(start, ahead, volume, out _))) return false;
            // Refine the centre crossing against the closest actual hull surface.
            float low=0f,high=1f;
            for(int i=0;i<10;i++)
            {
                float a=(2f*low+high)/3f,b=(low+2f*high)/3f;
                V3 pa=V3.Lerp(start,end,a),pb=V3.Lerp(start,end,b);
                float da=(Closest(volumes,center,pa)-pa).Length,db=(Closest(volumes,center,pb)-pb).Length;
                if(Math.Abs(da-db)<.00001f)break;
                if(da<db)high=b;else low=a;
            }
            float refined=(low+high)*.5f;
            if(refined>.02f&&refined<.98f)position=V3.Lerp(start,end,refined);
            V3 hull = Closest(volumes,center,position);
            if ((hull - position).Length > radius) return false;
            if (Ballistics.SightContact(launcher, position, hull, out var hit, false) && (hit - hull).Length > .3f) return false;
            armed.Remove(projectile);
            Detonate(projectile, launcher, target, position, radius);
            return true;
        }

        private static V3 Closest(Pawn target, V3 at)
        {
            var volumes = new List<HitVolume>();
            AerialGeometry.Volumes(target, volumes);
            return Closest(volumes,VehicleModels.V(AerialTargetLock.Point(target)),at);
        }

        private static V3 Closest(List<HitVolume> volumes,V3 center,V3 at)
        {
            V3 best = center;
            float distance = float.PositiveInfinity;
            foreach (var box in volumes)
            {
                if (box.Kind != VolumeKind.Box) continue;
                V3 delta = at - box.A;
                V3 point = box.A
                    + box.AxisRight * Math.Clamp(V3.Dot(delta, box.AxisRight), -box.B.X, box.B.X)
                    + box.AxisUp * Math.Clamp(V3.Dot(delta, box.AxisUp), -box.B.Y, box.B.Y)
                    + box.AxisForward * Math.Clamp(V3.Dot(delta, box.AxisForward), -box.B.Z, box.B.Z);
                float length = (point - at).Length;
                if (length < distance) { distance = length; best = point; }
            }
            return best;
        }

        private static void Detonate(Thing round, Thing launcher, Pawn intended, V3 position, float radius)
        {
            Map map = round.Map;
            ThingDef shell = round.def;
            float power = Math.Max(1f, shell.projectile.GetDamageAmount(null, null));
            int nativeFragments = 0;
            foreach (var comp in shell.comps ?? Enumerable.Empty<CompProperties>())
                if (comp.GetType().Name == "CompProperties_Fragments")
                    foreach (var entry in VehicleApi.List(comp,"fragments"))
                        if (entry is ThingDefCountClass fragmentSpec) nativeFragments += fragmentSpec.count;
            var rays = new V3[Math.Clamp(nativeFragments, 28, 72)];
            for (int i = 0; i < rays.Length; i++)
            {
                float vertical = 1f - 2f * (i + .5f) / rays.Length;
                float radial = MathF.Sqrt(1f - vertical * vertical);
                float phase = i * 2.39996323f;
                rays[i] = new V3(MathF.Cos(phase) * radial, vertical, MathF.Sin(phase) * radial);
            }
            V3 center = VehicleModels.V(AerialTargetLock.Point(intended));
            var contact = Closest(intended, position);
            V3 toward = V3.Lerp(contact, center, .12f) - position;
            if (toward.Length > .001f) rays[0] = toward * (1f / toward.Length);

            float intendedBefore = AircraftDamage.ComponentHealth(intended).health;
            float intendedPressure = 0f;
            var aircraft = AerialFlight.Store.Flights.Where(pair => pair.Value.Airborne && pair.Key.Spawned
                && !pair.Key.Dead && pair.Key.Map == map && VehicleApi.IsVehicle(pair.Key)).Select(pair => pair.Key).ToArray();
            foreach (var plane in aircraft)
            {
                if (plane.Destroyed || plane.Dead || !plane.Spawned) continue;
                V3 surface = Closest(plane, position);
                float distance = (surface - position).Length;
                if (distance > radius || (Ballistics.SightContact(launcher, position, surface, out var obstruction, false)
                    && (obstruction - surface).Length > .3f)) continue;
                float pressure = power * Mathf.Lerp(1f, .30f, distance / radius);
                if(plane==intended)intendedPressure=pressure;
                V3 direction = VehicleModels.V(AerialTargetLock.Point(plane)) - position;
                var shock = new DamageInfo(DamageDefOf.Bomb, pressure,
                    ExplosionPenetration(shell), Heading(direction), launcher, null, shell);
                float dealt = Hit(plane, launcher, shock, surface);
                if(dealt>0f) { ShockHits++; ShockDamage+=dealt; }
            }

            var trace = new Ballistics.ExternalTrace(launcher);
            ThingDef fragment = DefDatabase<ThingDef>.GetNamedSilentFail("Fragment_Small");
            float shardDamage = fragment?.projectile?.GetDamageAmount(null, null) ?? Math.Clamp(power * .14f, 5f, 16f);
            float shardPenetration = AccessTools.Field(fragment?.projectile?.GetType(),"armorPenetrationSharp")
                ?.GetValue(fragment.projectile) is float nativePenetration ? nativePenetration : .45f;
            for (int i = 0; i < rays.Length; i++)
            {
                V3 endpoint = position + rays[i] * (radius * 1.7f);
                if (!trace.Segment(map, position, endpoint, out float at, out var thing, out _, out _, out _)
                    || thing is not Pawn victim || !VehicleApi.IsVehicle(victim) || !AerialFlight.Airborne(victim)) continue;
                var shard = new DamageInfo(DamageDefOf.Bullet, shardDamage, shardPenetration, Heading(rays[i]), launcher, null, fragment ?? shell);
                float dealt=Hit(victim, launcher, shard, V3.Lerp(position, endpoint, at));
                if(dealt>0f){ShardHits++;ShardDamage+=dealt;}
            }
            // A blast can flex the airframe even when armour stops fragments and direct pressure damage.
            // Keep the fallback bounded and apply it only to a clear, in-range intended target.
            if(intendedPressure>0f && intended.Spawned && !intended.Dead
                && AircraftDamage.ComponentHealth(intended).health>=intendedBefore)
            {
                float stress=Math.Clamp(intendedPressure*.08f,.5f,4f);
                VehicleContacts.AirBlastStress(intended,launcher,VehicleModels.U(contact),stress,Heading(center-position));
                float loss=intendedBefore-AircraftDamage.ComponentHealth(intended).health;
                if(loss<=0f)
                {
                    VehicleContacts.AirBlastStress(intended,launcher,VehicleModels.U(center),stress,Heading(center-position));
                    loss=intendedBefore-AircraftDamage.ComponentHealth(intended).health;
                }
                if(loss>0f){ShockHits++;ShockDamage+=loss;StressDamage+=loss;}
            }
            shell.projectile.soundExplode?.PlayOneShot(new TargetInfo(round.Position, map));
            if(FpsMod.Settings.explosionQuality>0&&View3D.Active)
                Current.Game.GetComponent<ExplosionEffects>()?.Emit(map,VehicleModels.U(position),radius,round.thingIDNumber,BlastStyle.Airburst);
            visuals.Add(new Burst(map, position, rays, Find.TickManager.TicksGame, radius));
            Bursts++;
            round.Destroy();
        }

        internal static bool DirectContact(Thing projectile,Thing target)
        {
            if(target is not Pawn plane || !VehicleApi.IsVehicle(plane) || !AerialFlight.Airborne(plane)
                || nativeTimer.For(projectile)==null || !IsArmed(projectile))return false;
            armed.Remove(projectile);
            return true;
        }

        internal static void ApplyDirectContact(ThingDef definition,Pawn aircraft,Thing launcher,V3 position,V3 velocity,float amount)
        {
            var damage = new DamageInfo(definition.projectile.damageDef,amount,ExplosionPenetration(definition),
                Heading(velocity),launcher,null,definition);
            if(Hit(aircraft,launcher,damage,position)>0f)DirectHits++;
        }

        private static float Heading(V3 direction) =>
            Mathf.Repeat(Mathf.Atan2(direction.X,direction.Z)*Mathf.Rad2Deg,360f);

        // CE AmmoUtility.GetExplosionArmorPenetration uses base damage times 0.4.
        private static float ExplosionPenetration(ThingDef shell) =>
            CeCompat.NativeProjectile(shell) ? shell.projectile.GetDamageAmount(null,null) * .4f : -1f;

        private static float Hit(Pawn target, Thing launcher, DamageInfo damage, V3 at)
        {
            if (target.Destroyed || target.Dead || !target.Spawned) return 0f;
            var stats = VehicleApi.Get(target, "statHandler");
            if(stats==null)return target.TakeDamage(damage).totalDamageDealt;
            // VF's TakeDamage result doesn't report component HP. Read the actual, unrounded hull delta.
            float before=AircraftDamage.ComponentHealth(target).health;
            IntVec3 cell = FloorSpace.ContactCell(target.Map, at, 0f, target);
            cell = target.OccupiedRect().ClosestCellTo(cell);
            if(launcher!=null)VehicleApi.Call(stats, "RegisterImpacter", launcher, cell);
            try { target.TakeDamage(damage); }
            finally { if(launcher!=null)VehicleApi.Call(stats, "DeregisterImpacter", launcher); }
            return Math.Max(0,before-AircraftDamage.ComponentHealth(target).health);
        }

        internal static void Draw(Map map)
        {
            if (!View3D.Active || map == null) return;
            int now = Find.TickManager.TicksGame;
            for (int i = visuals.Count - 1; i >= 0; i--)
            {
                var burst = visuals[i];
                int age = now - burst.Tick;
                if (age < 0 || age >= 13) { visuals.RemoveAt(i); continue; }
                if (burst.Map != map || FloorSpace.Cell(map, burst.Position).Fogged(map)) continue;
                if (age < 5) Visuals.GunFlash(burst.Position, new V3(0, 1, 0), (5 - age) / 5f);
                float distance = burst.Radius * (.18f + age * .095f);
                for (int j = 0; j < burst.Rays.Length; j++)
                {
                    V3 ray = burst.Rays[j];
                    Visuals.Fragment(burst.Position + ray * distance, ray, .45f, j);
                }
            }
        }
    }
}

