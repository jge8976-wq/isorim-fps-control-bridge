using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace HSK.FpsBridge
{
    // Visual fallback only. Never derives damage, verbs, fire rate, ammo or abilities from the chosen model.
    internal static class WeaponKeywordRules
    {
        private static readonly Regex acronym = new Regex(@"([A-Z]+)([A-Z][a-z])", RegexOptions.CultureInvariant);
        private static readonly Regex camel = new Regex(@"([a-z0-9])([A-Z])", RegexOptions.CultureInvariant);
        private static readonly Regex words = new Regex(@"[a-z]+[0-9]*", RegexOptions.CultureInvariant);
        private static readonly Regex mgCode = new Regex(@"^(?:lmg|mmg|hmg|gpmg|mg)[0-9]*$", RegexOptions.CultureInvariant);
        private static readonly Regex smgCode = new Regex(@"^smg[0-9]*$", RegexOptions.CultureInvariant);

        internal static string Pick(string defName, string label, string graphicPath, bool melee)
        {
            // Visible name expresses intent first. Stable defName and the actual texture basename cover other languages.
            string path = graphicPath ?? "";
            int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            path = path.Substring(slash + 1);
            foreach (string source in new[] { label, defName, path })
            {
                string pick = One(source, melee);
                if (pick != null) return pick;
            }
            return null;
        }
        private static string One(string value, bool melee)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Normalize(NormalizationForm.FormKC);
            string spaced = camel.Replace(acronym.Replace(value, "$1 $2"), "$1 $2").ToLowerInvariant();
            string han = spaced.Replace('機','机').Replace('槍','枪').Replace('鎗','枪').Replace('輕','轻')
                .Replace('衝','冲').Replace('鋒','锋').Replace('轉','转').Replace('輪','轮').Replace('權','权').Replace('關','关').Replace('禪','禅');
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            var joined = new StringBuilder();
            foreach (Match m in words.Matches(spaced)) { tokens.Add(m.Value); joined.Append(m.Value); }
            string compact = joined.ToString();
            foreach (string token in tokens)
                if (token == "staff" || token == "staves" || token == "staffs" || token.EndsWith("staff", StringComparison.Ordinal)
                    || token == "wand" || token == "wands" || token.EndsWith("wand", StringComparison.Ordinal)
                    || token == "scepter" || token == "sceptre" || token == "scepters" || token == "sceptres") return "MeleeWeapon_PsyfocusStaff";
            if (Contains(han,"法杖","魔杖","权杖","禅杖")) return "MeleeWeapon_PsyfocusStaff";
            if (melee) return null;
            // SMG and machine pistols have different silhouettes. Test them before the longer 'machine gun' phrase.
            if (Contains(han,"冲锋枪","微冲") || compact.Contains("submachinegun") || compact.Contains("tommygun")
                || Any(tokens,smgCode)) return "Gun_HeavySMG";
            if (Contains(han,"自动手枪","冲锋手枪") || compact.Contains("machinepistol")) return "Gun_MachinePistol";
            if (Contains(han,"加特林","加特琳","转轮机枪","旋转机枪","旋管机枪","多管机枪")
                || compact.Contains("minigun") || compact.Contains("gatling") || compact.Contains("rotarygun") || compact.Contains("rotarycannon")) return "Gun_Minigun";
            if (Contains(han,"机枪","机关枪") || compact.Contains("machinegun") || Any(tokens,mgCode)) return "Gun_LMG";
            return null;
        }
        private static bool Contains(string s,params string[] terms)
        { foreach (string t in terms) if (s.Contains(t,StringComparison.Ordinal)) return true; return false; }
        private static bool Any(HashSet<string> tokens,Regex pattern)
        { foreach (string t in tokens) if (pattern.IsMatch(t)) return true; return false; }
    }
}
