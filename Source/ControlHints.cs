using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace HSK.FpsBridge
{
    // Only control-hint strings use this adapter. Names, item descriptions and localization data are left intact.
    internal static class ControlHints
    {
        private static readonly Dictionary<string, ControlAction> tokens = new Dictionary<string, ControlAction>
        {
            { "Q", ControlAction.LeanLeft }, { "E", ControlAction.InteractLeanRight }, { "Tab", ControlAction.NextWeapon },
            { "I", ControlAction.Inventory }, { "F", ControlAction.ManualWork }, { "H", ControlAction.AutoWork },
            { "B", ControlAction.FireMode }, { "R", ControlAction.Reload }, { "T", ControlAction.Social },
            { "G", ControlAction.MechAbility }, { "Enter", ControlAction.UseItem }, { "~", ControlAction.Capture },
            { "Shift", ControlAction.Sprint }, { "Alt", ControlAction.Overlay }, { "LMB", ControlAction.Fire }, { "RMB", ControlAction.Aim }
        };
        private static readonly Regex keyPattern = new Regex(@"(?<![A-Za-z0-9_])(?:Enter|Shift|Alt|Tab|LMB|RMB|Q|E|I|F|H|B|R|T|G|~)(?![A-Za-z0-9_])");
        private static readonly Dictionary<string, string> cache = new Dictionary<string, string>();
        private static int frame = -1, stamp;
        public static string For(string hint)
        {
            if (hint == null) return null;
            if (frame != Time.frameCount)
            {
                frame = Time.frameCount;
                int current = 17;
                foreach (ControlAction a in ControlKeys.Actions)
                    if (ControlKeys.Keys(a, out var k)) current = unchecked(current * 31 + (int)k.keyBindingA * 397 + (int)k.keyBindingB);
                if (current != stamp) { stamp = current; cache.Clear(); }
            }
            if (cache.TryGetValue(hint, out string mapped)) return mapped;
            mapped = hint.Replace(L.T("鼠标前侧键", "forward mouse side button"), ControlKeys.Label(ControlAction.ViewCycle)).Replace(L.T("前侧键", "forward button"), ControlKeys.Label(ControlAction.ViewCycle))
                .Replace(L.T("鼠标后侧键", "back mouse side button"), ControlKeys.Label(ControlAction.ExitRecall)).Replace(L.T("后侧键", "back button"), ControlKeys.Label(ControlAction.ExitRecall))
                .Replace("forward button", ControlKeys.Label(ControlAction.ViewCycle)).Replace("back button", ControlKeys.Label(ControlAction.ExitRecall));
            mapped = mapped.Replace(L.T("左键", "LMB"), ControlKeys.Label(ControlAction.Fire))
                .Replace(L.T("右键", "RMB"), ControlKeys.Label(ControlAction.Aim));
            mapped = keyPattern.Replace(mapped, m => ControlKeys.Label(tokens[m.Value]));
            if (cache.Count > 128) cache.Clear();
            cache[hint] = mapped;
            return mapped;
        }
    }
}
