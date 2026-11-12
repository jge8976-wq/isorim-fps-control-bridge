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
    public enum TurretTracerStyle
    {
        Automatic, Off, Amber, Light, Heavy, WhiteAP, Red, Green, Cyan, Blue, Violet, Flechette, Incendiary, HighExplosive
    }

    // Put on a projectile, turret gun, or turret building. A projectile setting takes precedence.
    public sealed class TurretTracerExtension : DefModExtension
    {
        public TurretTracerStyle style;
    }

    internal static class TurretTracers
    {
        internal sealed record Profile(Color Glow, Color Core, float Width, float Length, float Head);
        private static readonly Profile[] profiles =
        {
            new(new Color(1f,.78f,.06f,.57f), new Color(1f,.99f,.7f), 1f, 3.2f, .055f),
            new(new Color(1f,.9f,.32f,.53f), new Color(1f,1f,.83f), .78f, 2.4f, .04f),
            new(new Color(1f,.56f,.08f,.66f), new Color(1f,.95f,.65f), 1.35f, 4.4f, .084f),
            new(new Color(.74f,.86f,1f,.52f), new Color(1f,1f,1f), .82f, 4.8f, .06f),
            new(new Color(1f,.1f,.12f,.64f), new Color(1f,.65f,.56f), 1f, 3.3f, .058f),
            new(new Color(.24f,1f,.19f,.56f), new Color(.8f,1f,.64f), 1f, 3.3f, .06f),
            new(new Color(.1f,.86f,1f,.61f), new Color(.7f,1f,1f), 1.1f, 3.7f, .065f),
            new(new Color(.26f,.45f,1f,.65f), new Color(.73f,.88f,1f), 1.1f, 3.9f, .068f),
            new(new Color(.72f,.32f,1f,.64f), new Color(.95f,.78f,1f), 1.15f, 3.5f, .075f),
            new(new Color(.9f,.96f,.52f,.4f), new Color(1f,1f,.85f), .65f, 1.35f, .034f),
            new(new Color(1f,.27f,.035f,.65f), new Color(1f,.84f,.36f), 1.2f, 2.65f, .075f),
            new(new Color(1f,.52f,.16f,.55f), new Color(1f,.92f,.72f), 1.5f, 3.1f, .095f)
        };

        private sealed class Flight
        {
            internal Thing Round;
            internal Profile Style;
            internal bool Ce;
            internal V3 Origin, Head, Direction;
            internal float Scale;
        }

        private static readonly Lazy<Type> ceBullet = new(() => AccessTools.TypeByName("CombatExtended.BulletCE"));
        private static readonly Dictionary<(ThingDef Projectile, ThingDef Weapon, ThingDef Mount, bool Ce), TurretTracerStyle> styles = new();
        private const int MaxTracked = 512, MaxPerFrame = 144;
        private static readonly ConditionalWeakTable<Map, List<Flight>> flights = new();
        private static readonly ConditionalWeakTable<Thing, Flight> tracked = new();
        internal static int NativeVanillaDraws, NativeCeDraws;

        internal static Profile Of(TurretTracerStyle style)
            => style >= TurretTracerStyle.Amber && style <= TurretTracerStyle.HighExplosive ? profiles[(int)style - 2] : null;

        internal static bool IsMount(Thing source) => source is Building_Turret || VehicleApi.IsVehicle(source);

        internal static TurretTracerStyle Classify(ThingDef projectile, ThingDef weapon, ThingDef mount, bool ce)
        {
            var key = (projectile, weapon, mount, ce);
            if (!styles.TryGetValue(key, out var result)) styles[key] = result = ClassifyUncached(projectile, weapon, mount, ce);
            return result;
        }

        private static TurretTracerStyle ClassifyUncached(ThingDef projectile, ThingDef weapon, ThingDef mount, bool ce)
        {
            var properties = projectile?.projectile;
            if (properties == null || projectile.thingClass == null || properties.flyOverhead
                || WeaponCompatibility.Pulse(projectile)) return TurretTracerStyle.Off;
            var explicitStyle = Explicit(projectile, weapon, mount);
            if (explicitStyle == TurretTracerStyle.Off) return explicitStyle;
            if (ce)
            {
                var bullet = ceBullet.Value;
                if (!CeCompat.NativeProjectile(projectile) || CeCompat.IsInstantRound(projectile)
                    || (explicitStyle == TurretTracerStyle.Automatic && (bullet == null || !bullet.IsAssignableFrom(projectile.thingClass))))
                    return TurretTracerStyle.Off;
            }
            else if (!typeof(Projectile).IsAssignableFrom(projectile.thingClass)
                || (explicitStyle == TurretTracerStyle.Automatic && !typeof(Bullet).IsAssignableFrom(projectile.thingClass)))
                return TurretTracerStyle.Off;
            if (explicitStyle != TurretTracerStyle.Automatic) return explicitStyle;

            string name = projectile.defName;
            if (Includes(name, "rocket") || Includes(name, "missile") || Includes(name, "beam")
                || Includes(name, "flamethrower") || Includes(name, "grenade") || Includes(name, "torpedo"))
                return TurretTracerStyle.Off;
            if (properties.damageDef == DamageDefOf.EMP || Includes(name, "_EMP")) return TurretTracerStyle.Violet;
            if (properties.damageDef == DamageDefOf.Flame || Includes(name, "incendiary")) return TurretTracerStyle.Incendiary;
            if (Includes(name, "flechette") || Includes(name, "buckshot")) return TurretTracerStyle.Flechette;
            if (CeCompat.IsExplosiveDef(projectile)) return TurretTracerStyle.HighExplosive;
            if (Includes(name, "_AP") || Includes(name, "armorpiercing")) return TurretTracerStyle.WhiteAP;
            float damage = properties.GetDamageAmount(null, null);
            return damage >= 35f ? TurretTracerStyle.Heavy : damage <= 10f ? TurretTracerStyle.Light : TurretTracerStyle.Amber;
        }

        private static bool Includes(string name, string marker) => name.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;

        private static TurretTracerStyle Explicit(ThingDef projectile, ThingDef weapon, ThingDef mount)
        {
            var style = projectile.GetModExtension<TurretTracerExtension>()?.style ?? TurretTracerStyle.Automatic;
            if (style == TurretTracerStyle.Automatic) style = weapon?.GetModExtension<TurretTracerExtension>()?.style ?? style;
            return style == TurretTracerStyle.Automatic ? mount?.GetModExtension<TurretTracerExtension>()?.style ?? style : style;
        }

        internal static void ObserveVanilla(Projectile round)
        {
            if (!FpsMod.Settings.turretTracers || !round.Spawned || !IsMount(round.Launcher) || tracked.TryGetValue(round,out _)) return;
            var style = Of(Classify(round.def, round.EquipmentDef, round.Launcher.def, false));
            if (style == null) return;
            var start = Api.DrawnStart(round);
            start.z -= FloorSpace.Band(round.Map, round.Launcher.Position).Shift;
            Add(round, style, VehicleModels.V(start), AerialCombat.TracerScale(round.Launcher, round.intendedTarget), false);
        }

        internal static bool ObserveCe(Thing round, Thing launcher, ThingDef weapon, V3 start, LocalTargetInfo target)
        {
            if (!FpsMod.Settings.turretTracers || !round.Spawned || !IsMount(launcher)) return false;
            if (tracked.TryGetValue(round, out _)) return true;
            var style = Of(Classify(round.def, weapon, launcher.def, true));
            if (style == null) return false;
            Add(round, style, start, AerialCombat.TracerScale(launcher, target), true);
            return true;
        }

        private static void Add(Thing round, Profile style, V3 start, float scale, bool ce)
        {
            if (!start.Finite) return;
            var list = flights.GetOrCreateValue(round.Map);
            if (list.Count >= MaxTracked)
            {
                tracked.Remove(list[0].Round);
                list.RemoveAt(0);
            }
            var flight = new Flight { Round = round, Style = style, Ce = ce, Origin = start, Head = start, Scale = scale };
            tracked.Add(round, flight);
            list.Add(flight);
        }

        internal static bool DrawOwned(Thing round, Thing launcher, ThingDef weapon, V3 point, V3 velocity, float traveled, float scale, bool ce)
        {
            if (!FpsMod.Settings.turretTracers || !IsMount(launcher)) return false;
            var style = Classify(round.def, weapon, launcher.def, ce);
            return style == TurretTracerStyle.Off
                ? Explicit(round.def, weapon, launcher.def) == TurretTracerStyle.Off
                : Visuals.StyledTracer(point, velocity, traveled, scale, Of(style));
        }

        internal static void DrawNative(Map map)
        {
            if (map == null || !flights.TryGetValue(map, out var list)) return;
            int budget = MaxPerFrame;
            bool visible = View3D.Active && FpsMod.Settings.turretTracers;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var flight = list[i];
                var round = flight.Round;
                if (!round.Spawned || round.Map != map || (flight.Ce ? CeCompat.OwnsTurretTracerFlight(round) : Ballistics.OwnsTurretTracerFlight((Projectile)round)))
                {
                    tracked.Remove(round);
                    int last = list.Count - 1;
                    list[i] = list[last];
                    list.RemoveAt(last);
                    continue;
                }
                if (!visible || budget <= 0) continue;
                V3 point;
                if (flight.Ce)
                {
                    if (!CeCompat.NativeTurretTracerPosition(round, out point)) continue;
                }
                else
                {
                    var projectile = (Projectile)round;
                    var draw = projectile.ExactPosition;
                    point = new V3(draw.x, Api.ProjectileHeight(projectile, draw), draw.z - FloorSpace.Band(map, round.Position).Shift);
                }
                if (!point.Finite) continue;
                V3 delta = point - flight.Head;
                if (delta.Length > .005f) flight.Direction = delta;
                flight.Head = point;
                var cell = FloorSpace.Cell(map, point);
                if (!cell.InBounds(map) || cell.Fogged(map)) continue;
                Vector2 screen = Api.WorldToScreen(VehicleModels.U(point));
                if (screen.x < -140 || screen.y < -140 || screen.x > Screen.width + 140 || screen.y > Screen.height + 140) continue;
                if (!Visuals.StyledTracer(point, flight.Direction, (point - flight.Origin).Length, flight.Scale, flight.Style)) continue;
                if (flight.Ce) NativeCeDraws++; else NativeVanillaDraws++;
                budget--;
            }
        }
    }
}
