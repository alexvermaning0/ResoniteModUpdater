using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace ModUpdater.Core
{
    public sealed class ManifestArtifact
    {
        public string Url;
        public string Sha256;
    }

    /// <summary>One mod entry from the community resonite-mod-manifest.</summary>
    public sealed class ManifestEntry
    {
        public string Name;
        public string SourceLocation;                                  // GitHub repo URL
        public List<string> Versions = new();                          // version keys, newest-first
        public Dictionary<string, List<ManifestArtifact>> Artifacts =
            new(StringComparer.OrdinalIgnoreCase);                     // version -> artifacts
    }

    /// <summary>
    /// Loads the curated resonite-mod-manifest and indexes it by mod name. The manifest gives direct
    /// artifact download URLs (+ sha256), so updating from it needs no GitHub API at all. Used as the
    /// preferred source for any mod found in it (falling back to the mod's own Link otherwise).
    /// </summary>
    public sealed class ManifestSource
    {
        public const string DefaultUrl =
            "https://raw.githubusercontent.com/resonite-modding-group/resonite-mod-manifest/main/manifest.json";

        private static readonly HttpClient Http = CreateClient();
        private Dictionary<string, ManifestEntry> _byName;
        private bool _loaded;

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("ResoniteModUpdater/1.0 (+https://github.com)");
            return c;
        }

        /// <summary>Fetch &amp; parse the manifest once. Failures are swallowed (manifest is optional).</summary>
        public async Task EnsureLoadedAsync(string url, CancellationToken ct)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var u = string.IsNullOrWhiteSpace(url) ? DefaultUrl : url;
                using var resp = await Http.GetAsync(u, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return;
                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
                Parse(doc);
            }
            catch { /* manifest is a best-effort fallback */ }
        }

        private void Parse(JsonDocument doc)
        {
            var map = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
            if (!doc.RootElement.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Object)
                return;

            foreach (var author in objects.EnumerateObject())
            {
                if (!author.Value.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var e in entries.EnumerateObject())
                {
                    var v = e.Value;
                    var entry = new ManifestEntry
                    {
                        Name = v.TryGetProperty("name", out var n) ? n.GetString() : e.Name,
                        SourceLocation = v.TryGetProperty("sourceLocation", out var sl) ? sl.GetString() : null,
                    };

                    if (v.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var ver in versions.EnumerateObject())
                        {
                            var arts = new List<ManifestArtifact>();
                            if (ver.Value.TryGetProperty("artifacts", out var aarr) && aarr.ValueKind == JsonValueKind.Array)
                                foreach (var a in aarr.EnumerateArray())
                                    arts.Add(new ManifestArtifact
                                    {
                                        Url = a.TryGetProperty("url", out var url) ? url.GetString() : null,
                                        Sha256 = a.TryGetProperty("sha256", out var sh) ? sh.GetString() : null,
                                    });
                            entry.Artifacts[ver.Name] = arts;
                            entry.Versions.Add(ver.Name);
                        }
                    }

                    // Newest version first.
                    entry.Versions.Sort((a, b) =>
                        VersionUtil.IsNewer(a, b, out _) ? -1 : (VersionUtil.IsNewer(b, a, out _) ? 1 : 0));

                    if (!string.IsNullOrEmpty(entry.Name))
                        map[entry.Name] = entry;
                }
            }
            _byName = map;
        }

        public bool TryGet(string name, out ManifestEntry entry)
        {
            entry = null;
            return _byName != null && !string.IsNullOrEmpty(name) && _byName.TryGetValue(name, out entry);
        }
    }
}
