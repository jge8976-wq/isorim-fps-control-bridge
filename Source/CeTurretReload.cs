using System;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace HSK.FpsBridge
{
    internal sealed partial class CeTurret
    {
        private Thing reloadSource;
        private int reloadUntil = -1;
        private int nextReloadAttempt;

        internal Thing ManualReloadSource => reloadUntil >= 0 ? reloadSource : null;
        private static readonly Type NativeReload = AccessTools.TypeByName("CombatExtended.CombatExtended.Jobs.Utils.JobGiverUtils_Reload");
        private static readonly System.Reflection.MethodInfo CanNativeReload = NativeReload == null ? null
            : AccessTools.Method(NativeReload, "CanReload", new[] { typeof(Pawn), typeof(Thing), typeof(bool), typeof(bool) });
        private static readonly System.Reflection.MethodInfo MakeNativeReload = NativeReload == null ? null
            : AccessTools.Method(NativeReload, "MakeReloadJob", new[] { typeof(Pawn), typeof(Building_Turret) });

        internal void ResumeNativeReload(Pawn pawn, Thing pendingAmmo)
        {
            object[] check = { pawn, gun, false, false };
            if (CanNativeReload?.Invoke(null, check) is not true)
            {
                // CE's native turret reload reserves a stack on the map. Transfer only the rounds
                // from the operator's inventory that the interrupted first-person reload needed.
                var inventory = pawn.inventory?.innerContainer;
                var ammo = CeCompat.TurretAmmoUser(gun.AttackVerb);
                if (pendingAmmo == null || inventory?.Contains(pendingAmmo) != true || !UsesLocalAmmo(ammo)
                    || VehicleApi.Bool(ammo, "FullMagazine") || gun.IsForbidden(pawn) || gun.IsBurning()) return;
                int charges = Math.Max(1, VehicleApi.Int(pendingAmmo.def, "ammoCount"));
                int missing = VehicleApi.Int(ammo, "MissingToFullMagazine");
                int units = Mathf.CeilToInt(missing / (float)charges);
                if (units <= 0 || !inventory.TryDrop(pendingAmmo, ThingPlaceMode.Near,
                        Math.Min(units, pendingAmmo.stackCount), out var dropped)) return;
                dropped?.SetForbidden(false, false);
                if (CanNativeReload?.Invoke(null, check) is not true) return;
            }
            if (MakeNativeReload?.Invoke(null, new object[] { pawn, gun }) is Job job)
                pawn.jobs.StartJob(job, JobCondition.InterruptForced, resumeCurJobAfterwards: true);
        }

        private static bool UsesLocalAmmo(object ammo) =>
            ammo != null && VehicleApi.Bool(ammo, "UseAmmo") && VehicleApi.Bool(ammo, "HasMagazine");

        private static bool AllowNativeReload(Thing __instance) =>
            operated?.gun != __instance || operated.by == null || !UsesLocalAmmo(CeCompat.TurretAmmoUser(operated.gun.AttackVerb));

        public string Reload(Pawn pawn)
        {
            var ammo = CeCompat.TurretAmmoUser(gun.AttackVerb);
            if (by == null || !UsesLocalAmmo(ammo))
            {
                CeCompat.RequestReload(gun.AttackVerb);
                return null;
            }
            if (Reloading || VehicleApi.Bool(ammo, "FullMagazine")) return null;
            if (!CanReloadHere()) return L.T("当前无法装填炮塔。", "The turret cannot be reloaded right now.");

            var selected = VehicleApi.Get(ammo, "SelectedAmmo") as ThingDef;
            var compatible = VehicleApi.List(VehicleApi.Get(ammo, "CurAmmoSet"), "ammoTypes")
                .Select(link => VehicleApi.Get(link, "ammo") as ThingDef).ToHashSet();
            bool empty = VehicleApi.Int(ammo, "CurMagCount") == 0;
            var candidates = by.inventory.innerContainer.Concat(gun.OccupiedRect().ExpandedBy(FpsMod.Settings.turretAmmoRadius)
                .Where(cell => cell.InBounds(gun.Map)).SelectMany(cell => cell.GetThingList(gun.Map)));
            reloadSource = candidates.Where(thing => compatible.Contains(thing.def)
                    && (thing.def == selected || empty) && AmmoWithinReach(thing))
                .OrderBy(thing => thing.def != selected)
                .ThenBy(thing => thing.Spawned ? (thing.Position - gun.Position).LengthHorizontalSquared + 1 : 0)
                .FirstOrDefault();
            if (reloadSource == null)
            {
                var autoloader = AccessTools.Method(gun.GetType(), "TryReloadViaAutoLoader");
                if (autoloader?.Invoke(gun, null) is true) return null;
                return L.T("操作员库存和炮塔同层周围 ", "No usable ammo in the operator's inventory or within ")
                    + FpsMod.Settings.turretAmmoRadius + L.T(" 格内都没有可用弹药。", " cells of the turret on the same floor.");
            }

            VehicleApi.Set(ammo, "SelectedAmmo", reloadSource.def);
            var speedStat = DefDatabase<StatDef>.GetNamedSilentFail("ReloadSpeed");
            float speed = speedStat == null ? 1f : by.GetStatValue(speedStat);
            int duration = Mathf.CeilToInt(60f * VehicleApi.Float(ammo, "ReloadTime") / Mathf.Max(.01f, speed));
            reloadUntil = Find.TickManager.TicksGame + Math.Max(1, duration);
            return null;
        }

        private bool CanReloadHere() => gun.Spawned && by?.Spawned == true && by.Map == gun.Map
            && !by.Dead && !by.Downed && by.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
            && FloorSpace.Band(gun.Map, by.Position).Level == FloorSpace.Band(gun.Map, gun.Position).Level
            && (gun.OccupiedRect().ClosestCellTo(by.Position) - by.Position).LengthHorizontal <= WorkControl.Range;

        private bool AmmoWithinReach(Thing thing)
        {
            if (thing == null || thing.Destroyed || thing.stackCount <= 0
                || AccessTools.Property(thing.GetType(), "IsCookingOff")?.GetValue(thing) is true) return false;
            if (by.inventory.innerContainer.Contains(thing)) return true;
            if (!thing.Spawned || thing.Map != gun.Map || thing.IsForbidden(by.Faction) || thing.IsForbidden(by)
                || FloorSpace.Band(gun.Map, thing.Position).Level != FloorSpace.Band(gun.Map, gun.Position).Level) return false;
            var footprint = gun.OccupiedRect();
            var offset = thing.Position - footprint.ClosestCellTo(thing.Position);
            int radius = FpsMod.Settings.turretAmmoRadius;
            return Math.Abs(offset.x) <= radius && Math.Abs(offset.z) <= radius
                && by.CanReserve(thing, 1, 1) && by.CanReach(thing, PathEndMode.Touch, Danger.Some);
        }

        internal void MaintainReload(bool firing)
        {
            if (by == null || !gun.Spawned || Find.TickManager.Paused || !(bool)Get("Active")) return;
            StepReload();
            if (Reloading || Find.TickManager.TicksGame < nextReloadAttempt || !CanReloadHere()) return;
            var ammo = CeCompat.TurretAmmoUser(gun.AttackVerb);
            if (!UsesLocalAmmo(ammo)) return;
            int rounds = VehicleApi.Int(ammo, "CurMagCount");
            int capacity = VehicleApi.Int(ammo, "MagSize");
            int threshold = Mathf.FloorToInt(capacity * FpsMod.Settings.turretAutoReloadFraction);
            if (rounds == 0 || !firing && rounds <= threshold) AutoReload(false);
        }

        private void AutoReload(bool pressed)
        {
            int now = Find.TickManager.TicksGame;
            if (!pressed && now < nextReloadAttempt) return;
            nextReloadAttempt = now + 30;
            Reload(by);
        }

        private void CancelReload()
        {
            reloadUntil = -1;
            reloadSource = null;
        }

        private void StepReload()
        {
            if (reloadUntil < 0 || Find.TickManager.TicksGame < reloadUntil) return;
            Thing source = reloadSource;
            CancelReload();
            var ammo = CeCompat.TurretAmmoUser(gun.AttackVerb);
            if (!CanReloadHere() || (bool)Get("isReloading") || !UsesLocalAmmo(ammo) || !AmmoWithinReach(source)
                || VehicleApi.Get(ammo, "SelectedAmmo") != source.def || VehicleApi.Bool(ammo, "FullMagazine")) return;

            // CE's normal reload returns the old round type before loading a different one.
            if (VehicleApi.Int(ammo, "CurMagCount") > 0 && VehicleApi.Get(ammo, "CurrentAmmo") != source.def
                && AccessTools.Method(ammo.GetType(), "TryUnload", new[] { typeof(bool) }).Invoke(ammo, new object[] { true }) is false) return;

            bool single = VehicleApi.Bool(VehicleApi.Get(ammo, "Props"), "reloadOneAtATime");
            int remaining = VehicleApi.Int(ammo, "MissingToFullMagazine");
            if (remaining <= 0) return;
            int charges = Math.Max(1, VehicleApi.Int(source.def, "ammoCount"));
            int units = single ? 1 : Mathf.CeilToInt(remaining / (float)charges);
            var load = AccessTools.Method(ammo.GetType(), "LoadAmmo", new[] { typeof(Thing), typeof(bool) });
            // Pass only the rounds still needed, so topping up never consumes a full spare magazine.
            var portion = source.SplitOff(Math.Min(units, source.stackCount));
            load.Invoke(ammo, new object[] { portion, false });
            if (single && !VehicleApi.Bool(ammo, "FullMagazine")) Reload(by);
        }
    }
}
