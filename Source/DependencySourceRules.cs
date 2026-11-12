using System;
using System.Collections.Generic;
using System.IO;

namespace HSK.FpsBridge
{
    // Source layout is separate from API validation: manager links must not disable an otherwise compatible bridge.
    internal static class DependencySourceRules
    {
        internal readonly struct Source
        {
            internal readonly string LogicalRoot, PhysicalRoot;
            internal bool Linked => !string.Equals(LogicalRoot, PhysicalRoot, StringComparison.OrdinalIgnoreCase);
            internal Source(string logical, string physical) { LogicalRoot = logical; PhysicalRoot = physical; }
        }

        internal static Source Validate(string expectedPackage, string workshopId, string package,
            string directory, bool onSteamWorkshop, bool supports17)
        {
            string problem = "Expected Workshop 1.7 dependency " + workshopId + " (" + expectedPackage + "); selected=" + directory;
            if (string.IsNullOrWhiteSpace(package) || !PackageMatches(package, expectedPackage))
                throw new InvalidOperationException(problem + "; package ID missing or mismatched");
            if (!supports17) throw new InvalidOperationException(problem + "; 1.7 support is missing");
            string physical, logical;
            var routes = new List<string>();
            try
            {
                logical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
                physical = ResolvePhysicalRoot(logical, routes);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
            { throw new InvalidOperationException(problem + "; cannot resolve dependency directory: " + e.Message, e); }
            bool correctId = new DirectoryInfo(physical).Name == workshopId;
            bool knownWorkshop = onSteamWorkshop && new DirectoryInfo(logical).Name == workshopId;
            // Keep each resolved route: RimCrow can also redirect the SteamCMD storage's parent directory.
            bool workshopRoute = routes.Exists(p => IsWorkshopItemRoot(p, workshopId));
            if (!correctId || (!knownWorkshop && !workshopRoute))
                throw new InvalidOperationException(problem + "; physical=" + physical + "; wrong Workshop item or source layout");
            return new Source(logical, physical);
        }

        private static bool PackageMatches(string actual, string expected)
        {
            if (actual.EndsWith("_steam", StringComparison.OrdinalIgnoreCase)) actual = actual.Substring(0, actual.Length - 6);
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWorkshopItemRoot(string path, string workshopId)
        {
            var item = new DirectoryInfo(path);
            return item.Name == workshopId && item.Parent?.Name == "294100"
                && string.Equals(item.Parent.Parent?.Name, "content", StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Parent.Parent?.Parent?.Name, "workshop", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolvePhysicalRoot(string input, List<string> routes)
        {
            string pending = input;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int hop = 0; hop < 64; hop++)
            {
                if (!seen.Add(pending)) throw new IOException("Directory link cycle");
                routes.Add(pending);
                string root = Path.GetPathRoot(pending);
                string[] pieces = pending.Substring(root.Length).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                string current = root;
                bool changed = false;
                for (int i = 0; i < pieces.Length; i++)
                {
                    var next = new DirectoryInfo(Path.Combine(current, pieces[i]));
                    if (!next.Exists) throw new DirectoryNotFoundException(next.FullName);
                    if ((next.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        var target = next.ResolveLinkTarget(true) as DirectoryInfo;
                        if (target == null) throw new IOException("Unresolvable directory link " + next.FullName);
                        pending = target.FullName;
                        for (int j = i + 1; j < pieces.Length; j++) pending = Path.Combine(pending, pieces[j]);
                        pending = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pending));
                        changed = true;
                        break;
                    }
                    current = next.FullName;
                }
                if (!changed) return Path.TrimEndingDirectorySeparator(current);
            }
            throw new IOException("Too many directory links");
        }
    }
}
