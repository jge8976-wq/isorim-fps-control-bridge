using System;
using System.Linq;
using Verse;
namespace HSK.FpsBridge
{
    internal static class AerialAudio
    {
        internal static SoundDef Resolve(object gun)
        {
            var data=AerialArmament.Data(gun);if(data==null)return null;
            string weapon=data.freeFallBomb?null:data.lockableMissile?"CE_DisposableRocketLauncher":data.kind?.StartsWith("Grenade",StringComparison.OrdinalIgnoreCase)==true?"CE_GrenadeLauncher":null;
            SoundDef source=weapon==null?null:DefDatabase<ThingDef>.GetNamedSilentFail(weapon)?.Verbs?.FirstOrDefault()?.soundCast;
            if(source!=null&&!source.defName.Contains("Rifle"))return source;
            string fallback=data.freeFallBomb?"Click":data.lockableMissile||data.kind?.StartsWith("Rocket",StringComparison.OrdinalIgnoreCase)==true?"RocketswarmLauncher_Fire":data.kind?.StartsWith("Grenade",StringComparison.OrdinalIgnoreCase)==true?"ThumpCannon_Fire":null;
            return fallback==null?null:DefDatabase<SoundDef>.GetNamedSilentFail(fallback);
        }
        internal static void Apply(object gun)
        {
            var d=AerialArmament.Data(gun);if(d==null)return;
            if(d.freeFallBomb||d.lockableMissile||d.kind?.StartsWith("Rocket",StringComparison.OrdinalIgnoreCase)==true||d.kind?.StartsWith("Grenade",StringComparison.OrdinalIgnoreCase)==true)
                VehicleApi.Set(VehicleApi.Get(gun,"def"),"shotSound",Resolve(gun)); // silence is preferable to a rifle fallback
        }
    }
}
