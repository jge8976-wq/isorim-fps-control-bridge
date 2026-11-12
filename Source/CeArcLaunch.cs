using System;
using HarmonyLib;
using Verse;

namespace HSK.FpsBridge
{
    internal static partial class CeCompat
    {
        private sealed class ArcLaunchScope
        {
            internal Thing Launcher, Equipment;
            internal LaunchOwner Owner;
        }
        [ThreadStatic] private static ArcLaunchScope arcLaunch;
        private static readonly Type arcDispatcherType = AccessTools.TypeByName("MotorizationCE.ProjectileCE_ArcSpread");

        internal static object ArcDefinition(ThingDef definition)
        {
            if (arcDispatcherType == null || definition?.thingClass == null || !arcDispatcherType.IsAssignableFrom(definition.thingClass)) return null;
            foreach (DefModExtension extension in definition.modExtensions)
                if (extension.GetType().FullName == "Motorization.RotationalArcSpreadExtension") return extension;
            throw new InvalidOperationException("Native CE arc dispatcher has no RotationalArcSpreadExtension.");
        }

        internal static bool ArcPayload(ThingDef definition, out ThingDef payload)
        {
            object extension = ArcDefinition(definition);
            payload = extension == null ? null : VehicleApi.Get(extension, "projectile") as ThingDef;
            if (extension != null && payload == null) throw new InvalidOperationException("Native CE arc dispatcher has no payload projectile.");
            return payload != null;
        }
    }
}
