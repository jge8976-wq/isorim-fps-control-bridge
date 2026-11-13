using System;
using System.Collections.Generic;
using HSK.FpsBridge.Interop;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class ImpactMinimap
    {
        internal readonly record struct Impact(V3 Point, IntVec3 Cell, float BlastRadius, float Spread = 0f);

        internal static bool TryPosition(Thing viewer, Impact impact, Rect area, bool wholeMap, out Vector2 center)
        {
            center = default;
            if (viewer?.Map is not Map map || !impact.Point.Finite || !impact.Cell.InBounds(map)) return false;
            if (wholeMap) return DubsMinimap.TryMinimapPosition(map, impact.Point, impact.Cell, area, out center);
            FloorBand floor = FloorSpace.Band(map, viewer.Position);
            if (FloorSpace.Band(map, impact.Cell).Level != floor.Level) return false;
            var p = MinimapRules.PixelPosition(impact.Point.X, impact.Point.Z, map.Size.x, floor.Rows, area.x, area.y, area.width, area.height);
            center = new Vector2(p.x, p.y);
            return true;
        }

        internal static void Draw(Thing viewer, Rect area, bool wholeMap)
        {
            if (VehicleControl.On)
            {
                VehiclePrediction.DrawMinimap(area, wholeMap);
                return;
            }
            if (Event.current?.type != EventType.Repaint || FirstPersonTransition.Active) return;
            if (TryPredict(Bridge.ShootingPawn, TurretControl.Current, out Impact impact))
                DrawMarker(viewer, impact, area, wholeMap);
        }

        internal static void DrawMarker(Thing viewer, Impact impact, Rect area, bool wholeMap)
        {
            if (!TryPosition(viewer, impact, area, wholeMap, out Vector2 center)) return;
            Map map = viewer.Map;
            float xScale = area.width / map.Size.x;
            int rows = wholeMap ? DubsMinimap.ViewSize(map).z : FloorSpace.Band(map, viewer.Position).Rows;
            float yScale = area.height / rows;
            Color tint = VehiclePrediction.Tint;
            Ring(center, impact.BlastRadius, xScale, yScale, tint);
            if (impact.Spread > 0f)
                Ring(center, impact.Spread, xScale, yScale, new Color(1f, .75f, .25f, .85f));
            Widgets.DrawLine(center - new Vector2(3f, 0f), center + new Vector2(3f, 0f), tint, 1f);
            Widgets.DrawLine(center - new Vector2(0f, 3f), center + new Vector2(0f, 3f), tint, 1f);
        }

        private static void Ring(Vector2 center, float radius, float xScale, float yScale, Color tint)
        {
            float rx = Math.Max(3f, radius * xScale), ry = Math.Max(3f, radius * yScale);
            Vector2 previous = center + new Vector2(rx, 0f);
            for (int i = 1; i <= 40; i++)
            {
                float angle = i * Mathf.PI * 2f / 40f;
                Vector2 next = center + new Vector2(Mathf.Cos(angle) * rx, Mathf.Sin(angle) * ry);
                Widgets.DrawLine(previous, next, tint, 2f);
                previous = next;
            }
        }

        internal static bool TryPredict(Pawn actor, IManualTurret turret, out Impact impact)
        {
            impact = default;
            Thing source = turret?.Thing ?? actor;
            if (!Ballistics.Enabled || source?.Spawned != true || source.Map != Find.CurrentMap) return false;
            Verb verb = turret == null ? actor.equipment?.PrimaryEq?.PrimaryVerb : (source as Building_Turret)?.AttackVerb;
            ThingDef ce = CeCompat.ProjectileFor(verb);
            ThingDef projectile = ce ?? (turret as VanillaTurret)?.ShellProjectile ?? (verb as Verb_LaunchProjectile)?.Projectile;
            float blast = projectile != null ? CeCompat.VehicleBlastRadius(projectile) : (turret as IArtilleryTurret)?.BlastRadius ?? 0f;
            if (turret == null && ce != null && CeCompat.IsGrenade(verb))
                return CeCompat.TryThrowImpact(actor, verb, out impact);
            if (turret == null && verb is Verb_LaunchProjectile hand && Ballistics.IsGrenade(hand) && !SpecialWeapons.Uses(hand))
            {
                if (!Grenades.TryImpact(actor, hand, out V3 landing, out IntVec3 cell)) return false;
                impact = new Impact(landing, cell, blast);
                return true;
            }
            if (!CeCompat.IsExplosiveDef(projectile) && blast <= 0f) return false;

            if (turret != null)
            {
                if (turret is IArtilleryTurret artillery && Artillery.On)
                {
                    LocalTargetInfo aim = Artillery.Target();
                    if (!aim.Cell.InBounds(source.Map)) return false;
                    float distance = (aim.Cell - source.Position).LengthHorizontal;
                    if (distance < turret.MinRange || distance > turret.Range) return false;
                    if (ce != null) return CeCompat.TryTurretImpact(source, actor, verb, ce, aim, out impact);
                    return TargetImpact(source.Map, aim, blast, artillery.Spread(aim.Cell, actor), out impact);
                }
                if (ce != null) return CeCompat.TryTurretImpact(source, actor, verb, ce, TurretControl.AimTarget(actor), out impact);
                if (TurretControl.DirectProjectile(verb))
                    return TraceVanilla(source, actor, verb, projectile, TurretControl.Muzzle(out V3 direction), direction, out impact);
                LocalTargetInfo target = TurretControl.AimTarget(actor);
                if (!target.IsValid || (target.Cell - source.Position).LengthHorizontal > turret.Range) return false;
                return TargetImpact(source.Map, target, blast, NativeSpread(verb, target, source, actor), out impact);
            }

            if (ce != null) return CeCompat.TryWeaponImpact(actor, verb, ce, out impact);
            if (Ballistics.IsRound(verb) && !SpecialWeapons.Uses(verb))
                return TraceVanilla(actor, null, verb, projectile, Ballistics.Eye(actor), ControlCamera.ShotDirection(actor), out impact);
            LocalTargetInfo native = Bridge.NativeAimTarget(actor, verb);
            return native.IsValid && verb.CanHitTarget(native)
                && TargetImpact(source.Map, native, blast, NativeSpread(verb, native, source, actor), out impact);
        }

        private static float NativeSpread(Verb verb, LocalTargetInfo target, Thing source, Pawn actor)
        {
            if (verb == null) return 0f;
            float radius = verb.verbProps.ForcedMissRadius;
            if (radius <= .5f) return 0f;
            if (actor != null) radius *= verb.verbProps.GetForceMissFactorFor(source, actor);
            return VerbUtility.CalculateAdjustedForcedMiss(radius, target.Cell - source.Position);
        }

        private static bool TargetImpact(Map map, LocalTargetInfo target, float blast, float spread, out Impact impact)
        {
            impact = default;
            if (!target.Cell.InBounds(map)) return false;
            FloorBand floor = FloorSpace.Band(map, target.Cell);
            impact = new Impact(new V3(target.Cell.x + .5f, floor.Height, target.Cell.z + .5f - floor.Shift), target.Cell, blast, spread);
            return true;
        }

        private static bool TraceVanilla(Thing source, Pawn actor, Verb verb, ThingDef projectile, V3 origin, V3 direction, out Impact impact)
        {
            float speed = ViewRules.Speed(BulletSpeeds.ForWeapon(verb.EquipmentSource?.def.defName, projectile.projectile.SpeedTilesPerTick), FpsMod.Settings.speed);
            return Trace(source, actor, projectile, origin, direction * speed, ViewRules.Gravity(FpsMod.Settings.gravity), out impact);
        }

        internal static bool Trace(Thing source, Pawn actor, ThingDef projectile, V3 position, V3 velocity, float gravity,
            out Impact impact, CeCompat.NativeMotionPreview native = null, bool ceThrow = false, List<V3> path = null)
        {
            impact = default;
            Map map = source.Map;
            if (!position.Finite || !velocity.Finite || !FloorSpace.InWorld(map, position)) return false;
            var trace = new Ballistics.ExternalTrace(source, actor);
            path?.Add(position);
            bool bounce = ceThrow;
            for (int tick = 0; tick < 2400; tick++)
            {
                V3 next;
                if (native == null)
                {
                    velocity.Y -= gravity;
                    next = position + velocity;
                }
                else
                {
                    next = native.Step(velocity);
                    velocity = native.Velocity;
                }
                if (trace.Segment(map, position, next, out float fraction, out Thing hit, out _, out _, out V3 normal))
                {
                    position = V3.Lerp(position, next, fraction);
                    if (bounce && hit != null && !(hit is Pawn) && Math.Abs(normal.Y) < .5f)
                    {
                        bounce = false;
                        CeCompat.BounceThrow(ref position, ref velocity, normal);
                        path?.Add(position);
                        continue;
                    }
                    path?.Add(position);
                    IntVec3 cell = FloorSpace.ContactCell(map, position, velocity.Y, hit);
                    if (!cell.InBounds(map)) return false;
                    impact = new Impact(position, cell, CeCompat.VehicleBlastRadius(projectile));
                    return true;
                }
                if (!FloorSpace.InWorld(map, next)) return false;
                position = next;
                path?.Add(position);
            }
            return false;
        }
    }
}
