using System;
using System.Text.RegularExpressions;

namespace ModUpdater.Core
{
    /// <summary>Extracts owner/repo from a mod's GitHub Link in its many forms.</summary>
    public static class LinkParser
    {
        // Matches https://github.com/Owner/Repo with optional .git, trailing path (/releases, /tree/x), query, slash.
        private static readonly Regex GitHub = new Regex(
            @"github\.com[/:]+(?<owner>[^/\s]+)/(?<repo>[^/\s#?]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>True if owner/repo could be parsed from <paramref name="link"/>.</summary>
        public static bool TryParse(string link, out string owner, out string repo)
        {
            owner = repo = null;
            if (string.IsNullOrWhiteSpace(link)) return false;

            var m = GitHub.Match(link);
            if (!m.Success) return false;

            owner = m.Groups["owner"].Value.Trim();
            repo = m.Groups["repo"].Value.Trim();

            // strip a trailing ".git"
            if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                repo = repo.Substring(0, repo.Length - 4);

            return owner.Length > 0 && repo.Length > 0;
        }
    }
}
