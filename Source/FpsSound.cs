using System.Linq;
using Verse;
using Verse.Sound;

namespace HSK.FpsBridge
{
    internal static class FpsSound
    {
        internal static void Local(SoundDef sound, Pawn source)
        {
            if (sound == null) return;
            if (sound.subSounds?.Any(s => s.onCamera) == true) sound.PlayOneShotOnCamera(source?.Map);
            else if (source?.Spawned == true) sound.PlayOneShot(SoundInfo.InMap(source));
        }
    }
}
