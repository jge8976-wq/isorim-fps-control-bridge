using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    internal static class PauseChord
    {
        internal static bool IsDown => (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
            && Input.GetKeyDown(KeyCode.Space);

        internal static bool Frame()
        {
            if (!IsDown || Find.TickManager == null || Current.ProgramState != ProgramState.Playing
                || Find.WindowStack == null || Find.WindowStack.AnyWindowAbsorbingAllInput || GUIUtility.keyboardControl != 0)
                return false;
            Find.TickManager.CurTimeSpeed = Find.TickManager.Paused ? TimeSpeed.Normal : TimeSpeed.Paused;
            return true;
        }
    }
}
