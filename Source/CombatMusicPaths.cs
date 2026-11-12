using System;
using System.IO;

namespace HSK.FpsBridge
{
    internal static class CombatMusicPaths
    {
        internal const string Default = "CombatMusic/SweetDreams_Combat";
        internal static bool IsBundled(string root, string setting) => string.Equals(Resolve(root,setting),
            Path.GetFullPath(Path.Combine(root,"Sounds","CombatMusic","SweetDreams_Combat.ogg")),StringComparison.OrdinalIgnoreCase);
        internal static string Resolve(string root, string setting)
        {
            setting = string.IsNullOrWhiteSpace(setting) ? Default : setting.Trim();
            if (Path.IsPathRooted(setting)) return Path.GetFullPath(setting);
            string normalized = setting.Replace('\\', '/');
            // Older candidates used an original track which is preserved separately.
            // Only migrate that known default, never a player's custom path.
            if ((normalized.Equals("CombatMusic/FpsBattleOriginal", StringComparison.OrdinalIgnoreCase) ||
                 normalized.Equals("CombatMusic/FpsBattleOriginal.ogg", StringComparison.OrdinalIgnoreCase)) &&
                !File.Exists(Path.Combine(root, "Sounds", "CombatMusic", "FpsBattleOriginal.ogg"))) setting = Default;
            string ext = Path.GetExtension(setting);
            if (string.IsNullOrEmpty(ext)) setting += ".ogg";
            return Path.GetFullPath(Path.Combine(root, "Sounds", setting));
        }
    }
}
