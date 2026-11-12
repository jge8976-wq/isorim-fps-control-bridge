#nullable enable
using System;

namespace HSK.FpsBridge
{
    // Text shown to the player, in the game's language: Chinese when the game runs in Chinese (Simplified or
    // Traditional), English otherwise. Engine-free, so the offline contracts compile it too (there it stays Chinese);
    // in the game Bootstrap hooks IsChinese to the active language.
    internal static class L
    {
        public static Func<bool>? IsChinese;
        public static string T(string zh, string en) => (IsChinese?.Invoke() ?? true) ? zh : en;
    }
}
