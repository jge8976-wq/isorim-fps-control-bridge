using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HSK.FpsBridge
{
    // Waylith currently maps Mouse3/4 to forward/back; preserve Unity's physical back/forward binding contract.
    internal static class SideMouse
    {
        private static readonly bool[] held = new bool[2], down = new bool[2], up = new bool[2];
        private static int frame = -1;
        private static bool focused;
        private static Func<int, bool> read = ReadPlatform;
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);
        private static bool ReadPlatform(int index) => OperatingSystem.IsWindows()
            ? (GetAsyncKeyState(index == 0 ? 0x05 : 0x06) & 0x8000) != 0
            : Input.GetMouseButton(index + 3);
        private static void Sample()
        {
            if (frame == Time.frameCount) return;
            frame = Time.frameCount;
            bool focus = Application.isFocused;
            for (int i = 0; i < 2; i++)
            {
                bool current = focus && read(i);
                down[i] = focus && focused && current && !held[i];
                up[i] = focus && focused && !current && held[i];
                held[i] = current;
            }
            focused = focus; // A held button on focus return never becomes an entry/exit press.
        }
        internal static bool Held(KeyCode key) { Sample(); return held[key - KeyCode.Mouse3]; }
        internal static bool Down(KeyCode key) { Sample(); return down[key - KeyCode.Mouse3]; }
        internal static bool Up(KeyCode key) { Sample(); return up[key - KeyCode.Mouse3]; }
    }
}
