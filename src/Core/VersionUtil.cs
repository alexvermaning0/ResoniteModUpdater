using System;
using System.Text.RegularExpressions;

namespace ModUpdater.Core
{
    /// <summary>
    /// Normalizes release tags / mod versions and compares them. Mods declare versions like
    /// "1.2.3"; release tags are often "v1.2.3", "1.2.3-beta", "release-1.2.3", etc.
    /// </summary>
    public static class VersionUtil
    {
        private static readonly Regex VersionInString = new Regex(
            @"(?<v>\d+(?:\.\d+){0,3})", RegexOptions.Compiled);

        /// <summary>Pull a comparable dotted-number version out of an arbitrary tag/string.</summary>
        public static string Normalize(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var m = VersionInString.Match(raw);
            return m.Success ? m.Groups["v"].Value : null;
        }

        /// <summary>
        /// True when <paramref name="latestTag"/> represents a strictly newer version than
        /// <paramref name="installed"/>. Returns false (never auto-updates) if either side is
        /// unparseable, via <paramref name="comparable"/> the caller can detect that case.
        /// </summary>
        public static bool IsNewer(string latestTag, string installed, out bool comparable)
        {
            comparable = false;
            var l = Normalize(latestTag);
            var i = Normalize(installed);
            if (l == null || i == null) return false;

            if (TryVersion(l, out var lv) && TryVersion(i, out var iv))
            {
                comparable = true;
                return lv > iv;
            }
            return false;
        }

        /// <summary>True when two tags/version strings normalize to the same version.</summary>
        public static bool Same(string a, string b)
        {
            var na = Normalize(a);
            var nb = Normalize(b);
            return na != null && nb != null && string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryVersion(string s, out Version v)
        {
            // System.Version needs at least major.minor; pad "5" -> "5.0".
            if (!s.Contains('.')) s += ".0";
            return Version.TryParse(s, out v);
        }
    }
}
