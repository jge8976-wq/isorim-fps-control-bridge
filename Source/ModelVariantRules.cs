#nullable enable
using System;
using System.Collections.Generic;

namespace HSK.FpsBridge
{
    // Engine-free selection: use the actual subgraphic first; only unknown paths use a stable fallback.
    internal static class ModelVariantRules
    {
        public static char Letter(string? path)
        {
            if (string.IsNullOrEmpty(path)) return '\0';
            int start = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1;
            int end = path.Length;
            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) end -= 4;
            if (end - start >= 2 && path[end - 2] == '_' && char.ToLowerInvariant(path[end - 1]) == 'm') end -= 2;
            if (end <= start) return '\0';
            char letter = char.ToUpperInvariant(path[end - 1]);
            return letter >= 'A' && letter <= 'E' ? letter : '\0';
        }

        public static int Pick(IReadOnlyList<string>? ids, string? style, string? graphicPath, int seed)
        {
            if (ids == null || ids.Count == 0) return -1;
            if (!string.IsNullOrEmpty(style) && style.IndexOf("Horaxian", StringComparison.OrdinalIgnoreCase) >= 0)
                for (int i = 0; i < ids.Count; i++)
                    if (ids[i].EndsWith("__Horaxian", StringComparison.OrdinalIgnoreCase)) return i;
            char letter = Letter(graphicPath);
            int count = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                if (id.Length < 3 || id[id.Length - 3] != '_' || id[id.Length - 2] != '_') continue;
                char variant = char.ToUpperInvariant(id[id.Length - 1]);
                if (variant < 'A' || variant > 'E') continue;
                if (letter == variant) return i;
                count++;
            }
            if (count > 0)
            {
                int choice = seed % count;
                if (choice < 0) choice += count;
                for (int i = 0; i < ids.Count; i++)
                    if (ids[i].Length >= 3 && ids[i][ids[i].Length - 3] == '_' && ids[i][ids[i].Length - 2] == '_'
                        && Letter(ids[i]) != '\0' && choice-- == 0) return i;
            }
            for (int i = 0; i < ids.Count; i++)
                if (!ids[i].EndsWith("__Horaxian", StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }
    }
}
