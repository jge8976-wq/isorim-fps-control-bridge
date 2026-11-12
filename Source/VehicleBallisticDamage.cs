using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    public sealed class VehicleCrewVolumesDef : Def
    {
        public string vehicleDef;
        public List<VehicleCrewVolume> volumes = new List<VehicleCrewVolume>();

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (var error in base.ConfigErrors()) yield return error;
            if (string.IsNullOrEmpty(vehicleDef)) yield return "A vehicleDef is required.";
            foreach (var volume in volumes)
                if (volume == null || string.IsNullOrEmpty(volume.roleKey)
                    || !VehicleViewDef.Finite(volume.center) || !VehicleViewDef.Finite(volume.size)
                    || volume.size.x <= 0 || volume.size.y <= 0 || volume.size.z <= 0)
                    yield return "A crew volume needs a roleKey and finite, positive dimensions.";
        }
    }

    public sealed class VehicleCrewVolume
    {
        public string roleKey;
        public Vector3 center, size;
    }

    internal static class VehicleBallisticDamage
    {
        internal sealed class Contact : IDisposable
        {
            internal Contact Previous;
            internal Pawn Vehicle;
            internal Thing Launcher;
            internal DamageDef Damage;
            internal Vector3 Point, Direction;
            internal IntVec2 Cell, CrewCell;
            internal CellRect Grid;
            internal object Stats, Handler;
            internal bool Entered, CrewMapped, Penetrated, CrewAttempted, CeArmor;

            public void Dispose() => pending = Previous;
        }

        [ThreadStatic] private static Contact pending, active;
        [ThreadStatic] private static Contact selecting;
        private static MethodInfo hitPawn;
        private static FieldInfo ceSettings, ceArmorEnabled;
        private static object internalDepth;
        private static readonly Dictionary<ThingDef, VehicleCrewVolumesDef> profiles = new Dictionary<ThingDef, VehicleCrewVolumesDef>();
        internal static bool Ready { get; private set; }

        internal static void Install(Harmony harmony)
        {
            var stats = AccessTools.TypeByName("Vehicles.VehicleStatHandler") ?? throw new TypeLoadException("Vehicles.VehicleStatHandler");
            var component = AccessTools.TypeByName("Vehicles.VehicleComponent") ?? throw new TypeLoadException("Vehicles.VehicleComponent");
            var depth = AccessTools.Inner(component, "VehiclePartDepth");
            internalDepth = Enum.Parse(depth, "Internal");
            hitPawn = AccessTools.DeclaredMethod(stats, "HitPawn") ?? throw new MissingMethodException(stats.FullName, "HitPawn");
            var select = AccessTools.DeclaredMethod(stats, "TrySelectHandler") ?? throw new MissingMethodException(stats.FullName, "TrySelectHandler");
            var apply = AccessTools.DeclaredMethod(stats, "ApplyDamage", new[] { typeof(DamageInfo), typeof(IntVec2) })
                ?? throw new MissingMethodException(stats.FullName, "ApplyDamage");
            var take = AccessTools.DeclaredMethod(component, "TakeDamage", new[] { VehicleApi.PawnType, typeof(DamageInfo).MakeByRefType(), typeof(bool) })
                ?? throw new MissingMethodException(component.FullName, "TakeDamage(ref DamageInfo)");
            HarmonyMethod Hook(string name, int priority = Priority.Normal) =>
                new HarmonyMethod(typeof(VehicleBallisticDamage), name) { priority = priority };
            harmony.Patch(apply, prefix: Hook(nameof(BeforeApply), Priority.First + 500), finalizer: Hook(nameof(AfterApply)));
            harmony.Patch(hitPawn, prefix: Hook(nameof(BeforeHitPawn), Priority.First), finalizer: Hook(nameof(AfterHitPawn)));
            harmony.Patch(select, prefix: Hook(nameof(SelectHandler), Priority.First));
            var afterComponent = AccessTools.DeclaredMethod(typeof(VehicleBallisticDamage), nameof(AfterComponent)).MakeGenericMethod(take.ReturnType);
            harmony.Patch(take, postfix: new HarmonyMethod(afterComponent));

            var ce = AccessTools.TypeByName("CombatExtended.Compatibility.VehiclesCompat.VehicleArmorPatch");
            var penetrate = ce == null ? null : AccessTools.DeclaredMethod(ce, "TryPenetrateComponents");
            var controller = AccessTools.TypeByName("CombatExtended.Controller");
            ceSettings = controller == null ? null : AccessTools.Field(controller, "settings");
            ceArmorEnabled = ceSettings == null ? null : AccessTools.Field(ceSettings.FieldType, "patchArmorDamage");
            if (penetrate != null && ceArmorEnabled != null)
                harmony.Patch(penetrate, prefix: Hook(nameof(BeforeCePenetration)), postfix: Hook(nameof(AfterCePenetration)));
            Ready = true;
            Log.Message("[FpsBridge] VEHICLE_BALLISTIC_DAMAGE READY; local VF cells; CE adapter=" + (penetrate != null && ceArmorEnabled != null));
        }

        internal static Contact Begin(Thing projectile, Thing target, Thing launcher, V3 point, V3 direction)
        {
            if (!Ready || target is not Pawn vehicle || !VehicleApi.IsVehicle(vehicle) || projectile?.def.projectile?.damageDef is not DamageDef damage
                || !damage.harmsHealth || damage.isExplosive || direction.Length < .0001f
                || VehicleApi.Get(vehicle, "statHandler") is not object stats
                || VehicleApi.Get(vehicle, "Hitbox") is not CellRect grid) return null;

            var pose = VehicleGeometry.Pose(vehicle);
            var inverse = Quaternion.Inverse(AerialGeometry.Rotation(pose));
            Vector3 local = inverse * VehicleModels.U(point - pose.Position);
            Vector3 incoming = (inverse * VehicleModels.U(direction)).normalized;
            var contact = new Contact { Previous = pending, Vehicle = vehicle, Stats = stats, Launcher = launcher,
                Damage = damage, Point = local, Direction = incoming, Grid = grid, Cell = LocalCell(local, grid) };
            pending = contact;
            return contact;
        }

        private static IntVec2 LocalCell(Vector3 point, CellRect grid) => new IntVec2(
            Mathf.Clamp(Mathf.FloorToInt(point.x + .5f), grid.minX, grid.maxX),
            Mathf.Clamp(Mathf.FloorToInt(point.z + .5f), grid.minZ, grid.maxZ));

        internal static VehicleCrewVolumesDef ExplicitCrewProfile(Pawn pawn)
        {
            if (!profiles.TryGetValue(pawn.def, out var profile))
                profiles[pawn.def] = profile = DefDatabase<VehicleCrewVolumesDef>.AllDefsListForReading
                    .FirstOrDefault(def => def.vehicleDef == pawn.def.defName);
            return profile;
        }

        internal static bool TryCrewProfile(Pawn vehicle, out VehicleCrewVolumesDef profile, out object[] handlers)
        {
            profile = Ready ? ExplicitCrewProfile(vehicle) : null;
            handlers = null;
            if (profile == null || profile.volumes.Count == 0) return false;
            var volumes = profile.volumes;
            handlers = VehicleApi.Handlers(vehicle)
                .Where(h => !VehicleApi.Bool(VehicleApi.Role(h), "Exposed")).ToArray();
            return handlers.All(handler => volumes.Any(volume => volume.roleKey == VehicleApi.RoleKey(handler)));
        }

        private static void MapCrew(Contact contact)
        {
            if (!TryCrewProfile(contact.Vehicle, out var profile, out var handlers)) return;
            contact.CrewMapped = true;
            V3 from = VehicleModels.V(contact.Point - contact.Direction * .015f);
            V3 to = VehicleModels.V(contact.Point + contact.Direction * (contact.Vehicle.def.size.x + contact.Vehicle.def.size.z + 2));
            float nearest = float.PositiveInfinity;
            foreach (var volume in profile.volumes)
            {
                var handler = handlers.FirstOrDefault(h => VehicleApi.RoleKey(h) == volume.roleKey
                    && VehicleApi.List(h, "thingOwner").OfType<Pawn>().Any(p => !p.Dead));
                if (handler == null) continue;
                var box = new HitVolume { Kind = VolumeKind.Box, Region = HitRegion.Solid,
                    A = VehicleModels.V(volume.center), B = VehicleModels.V(volume.size * .5f) };
                if (!HitMath.Enter(from, to, box, out var fraction) || fraction >= nearest) continue;
                nearest = fraction;
                contact.Handler = handler;
                contact.CrewCell = LocalCell(VehicleModels.U(V3.Lerp(from, to, fraction)) + contact.Direction * .02f, contact.Grid);
            }
        }

        private static bool UsesCeArmor() => ceArmorEnabled != null && ceSettings.GetValue(null) is object settings
            && ceArmorEnabled.GetValue(settings) is true;

        // The installed CE patch reflects the X/Z pair on East and West before walking its native grid.
        internal static Vector3 AngleForEngine(Vector3 local, Rot4 rotation, bool ceArmor)
        {
            if (ceArmor && rotation == Rot4.East) return new Vector3(-local.z, 0, -local.x);
            if (ceArmor && rotation == Rot4.West) return new Vector3(local.z, 0, local.x);
            return new Vector3(local.x, 0, local.z);
        }

        public static void BeforeApply(object __instance, ref DamageInfo dinfo, ref IntVec2 hitCell, out Contact __state)
        {
            __state = active;
            active = null;
            if (pending is not { Entered: false } contact || contact.Stats != __instance
                || dinfo.Instigator != contact.Launcher || dinfo.Def != contact.Damage) return;
            contact.Entered = true;
            contact.CeArmor = UsesCeArmor();
            hitCell = contact.Cell;
            Vector3 heading = AngleForEngine(contact.Direction, contact.Vehicle.Rotation, contact.CeArmor);
            if (heading.sqrMagnitude > .000001f) dinfo.SetAngle(heading);
            MapCrew(contact);
            active = contact;
        }

        public static void AfterApply(Contact __state) => active = __state;

        public static void BeforeCePenetration(object __0, IList __2, object __3, out bool __state)
        {
            __state = active is { CrewMapped: true, Penetrated: false, CeArmor: true } contact
                && contact.Stats == __0 && External(__3) && __2.Cast<object>().Any(component =>
                    External(VehicleApi.Get(component, "Depth")) && VehicleApi.Float(component, "HealthPercent") > 0f);
        }

        public static void AfterCePenetration(ref DamageInfo __1, bool __result, bool __state)
        {
            if (__state && __result && __1.Amount > 0f && active != null) active.Penetrated = true;
        }

        private static bool External(object depth) => depth?.ToString() == "External" || depth is int value && value == 1;

        public static void AfterComponent<T>(object __instance, ref DamageInfo __1, T __result) where T : struct, Enum
        {
            if (active is not { CrewMapped: true, CeArmor: false, Penetrated: false } contact || __1.Amount <= 0f
                || __result.ToString() != "Penetrated" || VehicleApi.Get(__instance, "vehicle") != contact.Vehicle
                || !External(VehicleApi.Get(__instance, "Depth"))) return;
            contact.Penetrated = true;

            // VF can keep iterating external components until their HP is gone. Apply the remaining
            // native damage to the selected seat at this penetration, with the native crew chance and apparel.
            if (contact.Handler == null) return;
            float angle = Mathf.Repeat(Mathf.Atan2(contact.Direction.x, contact.Direction.z) * Mathf.Rad2Deg, 360f);
            var args = new object[] { __1, internalDepth, contact.Cell, Rot4.FromAngleFlat(angle), null, null };
            if ((bool)hitPawn.Invoke(contact.Stats, args)) __1.SetAmount(0f);
        }

        public static bool BeforeHitPawn(object __instance, object[] __args, ref bool __result, out Contact __state)
        {
            __state = selecting;
            selecting = null;
            if (active is not { CrewMapped: true } contact || contact.Stats != __instance || External(__args[1])) return true;
            if (!contact.Penetrated || contact.CrewAttempted || contact.Handler == null
                || contact.CeArmor && (IntVec2)__args[2] != contact.CrewCell)
            {
                __args[4] = null;
                __result = false;
                return false;
            }
            contact.CrewAttempted = true;
            selecting = contact;
            return true;
        }

        public static void AfterHitPawn(Contact __state) => selecting = __state;

        public static bool SelectHandler(object __instance, object[] __args, ref bool __result)
        {
            if (selecting is not Contact contact || contact.Stats != __instance || (bool)__args[2]) return true;
            __args[1] = contact.Handler;
            __result = true;
            return false;
        }
    }
}
