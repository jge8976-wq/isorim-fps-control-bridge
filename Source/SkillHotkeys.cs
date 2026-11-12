using UnityEngine;
using Verse;

namespace HSK.FpsBridge
{
    // KeyPrefs owns persistence, rebinding and conflict UI. Read current slots every time: never overwrite prefs
    // at startup and never route these skills through the pawn's paginated mod-ability list.
    internal static class SkillHotkeys
    {
        private static KeyBindingDef sandevistan, deathEye;
        private static readonly CyberSkills.Kind[] kinds = { CyberSkills.Kind.Sandevistan, CyberSkills.Kind.DeathEye };
        private static KeyBindingDef Binding(CyberSkills.Kind kind) => kind == CyberSkills.Kind.Sandevistan
            ? sandevistan ??= KeyBindingDef.Named("FPS_Sandevistan") : deathEye ??= KeyBindingDef.Named("FPS_DeathEye");

        internal static bool IsBinding(KeyBindingDef def) => def != null
            && (def == Binding(CyberSkills.Kind.Sandevistan) || def == Binding(CyberSkills.Kind.DeathEye));

        private static bool Keys(CyberSkills.Kind kind, out KeyBindingData keys)
        {
            keys = default;
            var def = Binding(kind);
            return def != null && KeyPrefs.KeyPrefsData != null && KeyPrefs.KeyPrefsData.keyPrefs.TryGetValue(def, out keys);
        }

        private static bool Down(KeyCode key) => ControlKeys.RawDown(key);

        private static bool Pressed(Pawn pawn, CyberSkills.Kind kind) => pawn != null && !TurretControl.Manned
            && CyberSkills.Has(pawn, kind) && Keys(kind, out var keys) && (Down(keys.keyBindingA) || Down(keys.keyBindingB));

        internal static bool Pressed(Pawn pawn) => Pressed(pawn, CyberSkills.Kind.Sandevistan) || Pressed(pawn, CyberSkills.Kind.DeathEye);

        internal static bool Owns(KeyCode key)
        {
            Pawn pawn = Bridge.ShootingPawn;
            if (key == KeyCode.None || pawn == null || TurretControl.Manned) return false;
            foreach (var kind in kinds)
                if (CyberSkills.Has(pawn, kind) && Keys(kind, out var keys) && (key == keys.keyBindingA || key == keys.keyBindingB)) return true;
            return false;
        }

        internal static bool Frame(Pawn pawn)
        {
            foreach (var kind in kinds)
                if (Pressed(pawn, kind)) { CyberSkills.Use(pawn, kind); return true; }
            return false;
        }

        internal static string Label(CyberSkills.Kind kind)
        {
            KeyBindingDef def = Binding(kind);
            return def == null || def.MainKey == KeyCode.None ? L.T("未绑定", "Unbound") : def.MainKeyLabel;
        }
    }
}
