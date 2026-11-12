using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HSK.FpsBridge
{
    internal static class RatkinTurretShots
    {
        internal const string BoltClass = "NewRatkin.Projectile_BallistaBoltAP";
        internal const string ShellClass = "NewRatkin.Projectile_RatkinCannonShell";
        internal static bool Enabled { get; private set; }
        internal static bool IsTurret(string def) => def == "RK_Building_Ballista" || def == "RK_Building_Cannon";
        internal static bool Supported(Verb verb) => Enabled && verb?.Caster is Building_TurretGun gun && IsTurret(gun.def.defName)
            && (verb as Verb_LaunchProjectile)?.Projectile is ThingDef p && p.projectile != null && !p.projectile.flyOverhead
            && (p.thingClass?.FullName == BoltClass || p.thingClass?.FullName == ShellClass);
        internal static void Install(Harmony harmony)
        {
            Type bolt = AccessTools.TypeByName(BoltClass), shell = AccessTools.TypeByName(ShellClass);
            if (bolt == null && shell == null) return;
            if (bolt == null || shell == null) throw new MissingMemberException("Ratkin projectile types");
            Type props = AccessTools.TypeByName("NewRatkin.ProjectileProperties_BallistaBoltAP");
            if (AccessTools.Field(bolt,"currentPenetrationCount")?.FieldType != typeof(int)
                || props==null || AccessTools.Field(props,"maxPenetrationCount")?.FieldType != typeof(int))
                throw new MissingFieldException("Ratkin native penetration counters");
            int hooks = 0;
            foreach (Type type in new[] { bolt, shell })
                foreach (MethodInfo m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (m.Name == "Tick" && m.GetParameters().Length == 0 || m.Name == "TickInterval" && m.GetParameters().Length == 1)
                    { harmony.Patch(m, prefix: new HarmonyMethod(typeof(Ballistics), nameof(Ballistics.BeforeTick)) { priority = Priority.First }); hooks++; }
            if (hooks < 2) throw new MissingMethodException("Ratkin custom projectile tick overrides");
            Enabled = true;
            Log.Message("[FpsBridge] RATKIN_TURRETS READY; native-impact=ON; direct-3d=ON; custom-tick-hooks=" + hooks);
        }
    }
}
