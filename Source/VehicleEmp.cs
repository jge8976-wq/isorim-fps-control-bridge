using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class VehicleEmp
    {
        private const float TicksPerDamage = 12f;
        private const int MaximumStunTicks = 1800;
        private const float DamageToElectronics = .35f;
        private static MethodInfo damagePart;
        private static readonly AccessTools.FieldRef<StunHandler, bool> fromEmp =
            AccessTools.FieldRefAccess<StunHandler, bool>("stunFromEMP");

        internal static void Install(Harmony harmony, Type statHandler)
        {
            Type component = AccessTools.TypeByName("Vehicles.VehicleComponent");
            Type vehicle = AccessTools.TypeByName("Vehicles.VehiclePawn");
            damagePart = AccessTools.Method(component, "TakeDamage", new[] { vehicle, typeof(DamageInfo), typeof(bool) });
            var apply = AccessTools.Method(statHandler, "ApplyDamage", new[] { typeof(DamageInfo), typeof(IntVec2) });
            harmony.Patch(apply, postfix: new HarmonyMethod(typeof(VehicleEmp), nameof(AfterNativeDamage)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(statHandler, "ElectrifyAllComponents"),
                prefix: new HarmonyMethod(typeof(VehicleEmp), nameof(SkipLegacyEmp)));
        }

        private static bool SkipLegacyEmp() => false;

        private static void AfterNativeDamage(object __instance, DamageInfo dinfo, IntVec2 hitCell)
        {
            if (dinfo.Def != DamageDefOf.EMP || !float.IsFinite(dinfo.Amount) || dinfo.Amount <= 0f
                || !DebugSettings.enableDamage || VehicleApi.Get(__instance, "vehicle") is not Pawn { Spawned: true, Dead: false } vehicle
                || !DebugSettings.enablePlayerDamage && vehicle.Faction == Faction.OfPlayer
                || vehicle.stances?.stunner is not StunHandler stun) return;

            int duration = Mathf.Clamp(Mathf.CeilToInt(dinfo.Amount * TicksPerDamage), 1, MaximumStunTicks);
            VehicleApi.Set(__instance, "OverrideStunPatch", true);
            try
            {
                fromEmp(stun) = true;
                stun.StunFor(duration, dinfo.Instigator, disableRotation: true);
            }
            finally { VehicleApi.Set(__instance, "OverrideStunPatch", false); }

            var components = VehicleApi.List(__instance, "components")
                .Where(part => VehicleApi.Float(part, "Health") > 0f).ToArray();
            var locations = VehicleApi.Get(__instance, "componentLocations") as IDictionary;
            var struck = (locations?[hitCell] as IEnumerable)?.Cast<object>().ToArray() ?? Array.Empty<object>();
            var electronics = components.Where(part => Convert.ToInt32(
                VehicleApi.Get(VehicleApi.Get(part, "props"), "empSeverity")) > 0).ToArray();
            if (electronics.Length == 0)
            {
                electronics = components.Where(part => VehicleApi.Get(VehicleApi.Get(part, "props"), "key")
                    is "Servos" or "Dashboard" or "Engine" or "Transmission").ToArray();
            }
            var impacted = electronics.Intersect(struck).ToArray();
            if (impacted.Length > 0) electronics = impacted;
            if (electronics.Length == 0)
                electronics = struck.Intersect(components).Concat(components).Take(1).ToArray();
            if (electronics.Length > 0)
            {
                var hit = dinfo;
                hit.SetAmount(dinfo.Amount * DamageToElectronics / electronics.Length);
                foreach (var part in electronics)
                {
                    if (!vehicle.Spawned || vehicle.Dead) break;
                    damagePart.Invoke(part, new object[] { vehicle, hit, true });
                }
            }
        }
    }
}
