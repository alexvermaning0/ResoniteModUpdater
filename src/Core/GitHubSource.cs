using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Text.Json;

namespace ModUpdater.Core
{
    public sealed class ReleaseAsset
    {
        public string Name;
        public string DownloadUrl;
    }

    /// <summary>Outcome of resolving a downloadable DLL asset for a release tag.</summary>
    public sealed class AssetResolution
    {
        public ReleaseAsset Asset;          // null when not resolvable
        public ModStatus FailureStatus;     // ManualOnly / Ambiguous / Error when Asset is null
        public string Detail;
        public bool Ok => Asset != null;
    }

    /// <summary>
    /// GitHub access split to avoid rate limits: the version check reads the public
    /// <c>releases.atom</c> RSS feed (no auth, not subject to the REST 60/hour cap), so checking many
    /// mods costs no API quota. The REST API is only touched when actually downloading a chosen
    /// release's .dll — rare, and the only place a token helps.
    /// </summary>
    public sealed class GitHubSource
    {
        private static readonly HttpClient Http = CreateClient();
        private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("ResoniteModUpdater/1.0 (+https://github.com)");
            return c;
        }

        /// <summary>Recent release tags newest-first from the RSS feed (one request, no auth/quota).</summary>
        public async Task<List<string>> GetReleaseTagsAsync(string owner, string repo, CancellationToken ct)
        {
            var tags = new List<string>();
            var url = $"https://github.com/{owner}/{repo}/releases.atom";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return tags;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct).ConfigureAwait(false);
            foreach (var entry in doc.Root?.Elements(Atom + "entry") ?? Enumerable.Empty<XElement>())
            {
                var tag = TagFromEntry(entry);
                if (!string.IsNullOrEmpty(tag) && !tags.Contains(tag)) tags.Add(tag);
            }
            return tags;
        }

        private static string TagFromEntry(XElement entry)
        {
            var href = entry.Elements(Atom + "link")
                            .Select(l => (string)l.Attribute("href"))
                            .FirstOrDefault(h => !string.IsNullOrEmpty(h) && h.Contains("/releases/tag/"));
            if (href != null)
            {
                var idx = href.IndexOf("/releases/tag/", StringComparison.OrdinalIgnoreCase);
                var tag = href.Substring(idx + "/releases/tag/".Length).Trim('/');
                if (!string.IsNullOrEmpty(tag)) return Uri.UnescapeDataString(tag);
            }
            var id = (string)entry.Element(Atom + "id");
            if (!string.IsNullOrEmpty(id))
            {
                var slash = id.LastIndexOf('/');
                if (slash >= 0 && slash < id.Length - 1) return id.Substring(slash + 1);
            }
            return null;
        }

        /// <summary>Heuristic: does a tag look like an alpha/beta/rc/prerelease (so "Latest" skips it)?</summary>
        public static bool IsPrereleaseTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;
            var t = tag.ToLowerInvariant();
            return t.Contains("alpha") || t.Contains("beta") || t.Contains("-rc") || t.Contains(".rc")
                || t.Contains("rc.") || t.Contains("pre") || t.Contains("snapshot") || t.Contains("-dev")
                || t.Contains("nightly") || t.Contains("canary");
        }

        /// <summary>
        /// Resolve which .dll asset to download for a specific release tag (one REST call). Prefers an
        /// asset whose file name matches the installed dll; otherwise the single .dll; else Ambiguous.
        /// </summary>
        public async Task<AssetResolution> ResolveAssetAsync(
            string owner, string repo, string tag, string preferredFileName, string extension, string token, CancellationToken ct)
        {
            extension = string.IsNullOrEmpty(extension) ? ".dll" : extension;
            var url = $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{Uri.EscapeDataString(tag)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden &&
                    resp.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) && rem.FirstOrDefault() == "0")
                    return new AssetResolution { FailureStatus = ModStatus.Error, Detail = "API rate limit — add a GitHub token" };
                return new AssetResolution { FailureStatus = ModStatus.Error, Detail = $"GitHub API {(int)resp.StatusCode}" };
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

            if (!json.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return new AssetResolution { FailureStatus = ModStatus.ManualOnly, Detail = "no assets" };

            var matches = new List<ReleaseAsset>();
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var dl = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                if (name != null && dl != null && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    matches.Add(new ReleaseAsset { Name = name, DownloadUrl = dl });
            }

            if (matches.Count == 0)
                return new AssetResolution { FailureStatus = ModStatus.ManualOnly, Detail = $"release has no {extension} asset" };

            var exact = matches.FirstOrDefault(d => string.Equals(d.Name, preferredFileName, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return new AssetResolution { Asset = exact };
            if (matches.Count == 1) return new AssetResolution { Asset = matches[0] };
            return new AssetResolution { FailureStatus = ModStatus.Ambiguous, Detail = $"{matches.Count} {extension} assets; none matches {preferredFileName}" };
        }

        /// <summary>Download an asset's bytes.</summary>
        public async Task<byte[]> DownloadAsync(string downloadUrl, string token, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            req.Headers.Accept.ParseAdd("application/octet-stream");
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
    }
}
