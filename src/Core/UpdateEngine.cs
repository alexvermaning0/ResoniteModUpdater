using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ResoniteModLoader;

namespace ModUpdater.Core
{
    /// <summary>Snapshot of the user's settings for a single check run.</summary>
    public sealed class UpdaterSettings
    {
        public bool Enabled = true;
        public bool AutoDownload = true;
        public string GitHubToken = "";
        // mod name -> version pin ("latest" / "hold" / a tag). Absent = "latest".
        public Dictionary<string, string> Pins = new(StringComparer.OrdinalIgnoreCase);
        public string OwnAssemblyPath = "";   // the updater's own dll — never auto-update ourselves
        public string ManifestUrl = "";       // empty = the default community manifest

        public string GetPin(string modName) =>
            Pins.TryGetValue(modName, out var p) && !string.IsNullOrWhiteSpace(p) ? p : Core.Pins.Latest;
    }

    /// <summary>
    /// Enumerates loaded RML mods, checks each against its GitHub releases, and (optionally) stages
    /// updates. Pure orchestration over <see cref="GitHubSource"/> / <see cref="DllInstaller"/>; no
    /// FrooxEngine dependency, so it can run entirely on a background thread.
    /// </summary>
    public sealed class UpdateEngine
    {
        private readonly GitHubSource _gh = new GitHubSource();
        private readonly ManifestSource _manifest = new ManifestSource();
        private readonly Action<string> _log;
        private const int MaxConcurrency = 4;

        public UpdateEngine(Action<string> log) => _log = log ?? (_ => { });

        /// <summary>Currently-loaded version string for a mod by name (for pruning applied updates).</summary>
        public static string LoadedVersionFor(string name)
        {
            foreach (var m in ModLoader.Mods())
                if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                    return m.Version;
            return null;
        }

        /// <summary>Snapshot the loaded mods into our model (must run while the engine is alive).</summary>
        public List<ModInfo> Enumerate(UpdaterSettings settings)
        {
            var list = new List<ModInfo>();
            foreach (var mod in ModLoader.Mods())
            {
                string dll = "";
                try { dll = mod.GetType().Assembly.Location ?? ""; } catch { /* leave empty */ }

                var info = new ModInfo
                {
                    Name = mod.Name,
                    Author = mod.Author,
                    InstalledVersion = mod.Version,
                    Link = mod.Link,
                    DllPath = dll,
                    HasBackup = !string.IsNullOrEmpty(dll) && DllInstaller.HasBackup(dll, mod.Name),
                };

                info.Pin = settings.GetPin(mod.Name);

                if (!string.IsNullOrEmpty(settings.OwnAssemblyPath) &&
                    string.Equals(dll, settings.OwnAssemblyPath, StringComparison.OrdinalIgnoreCase))
                {
                    info.Status = ModStatus.Skipped;
                    info.Detail = "the updater itself";
                }
                else if (string.IsNullOrEmpty(dll))
                {
                    info.Status = ModStatus.Skipped;
                    info.Detail = "could not locate dll on disk";
                }
                else if (LinkParser.TryParse(mod.Link, out var owner, out var repo))
                {
                    info.Owner = owner;
                    info.Repo = repo;
                }
                // else: no usable Link — leave Status Unknown; the manifest may still provide a source.
                list.Add(info);
            }
            list.AddRange(EnumerateMonkeyLoaderMods(settings));
            var loader = EnumerateLoader(settings);
            if (loader != null) list.Add(loader);
            return list;
        }

        /// <summary>
        /// MonkeyLoader itself as a notify-only entry. The Resonite install is the GamePack
        /// (ResoniteModdingGroup/MonkeyLoader.GamePacks.Resonite), updated by extracting its release zip —
        /// so we track that GamePack's version, and never auto-replace the loader.
        /// </summary>
        public ModInfo EnumerateLoader(UpdaterSettings settings)
        {
            var modsDir = MonkeyLoaderModsDir(settings.OwnAssemblyPath);
            if (modsDir == null) return null;
            var mlDir = Path.GetDirectoryName(modsDir);                 // ...\MonkeyLoader
            var gamepack = Path.Combine(mlDir, "GamePacks", "MonkeyLoader.GamePacks.Resonite.nupkg");
            if (!File.Exists(gamepack)) return null;
            if (!NupkgReader.TryRead(gamepack, out _, out var ver, out _)) return null;

            var info = new ModInfo
            {
                Kind = ModKind.Loader,
                NotifyOnly = true,
                Name = "MonkeyLoader",
                Author = "ResoniteModdingGroup",
                InstalledVersion = ver,
                Link = "https://github.com/ResoniteModdingGroup/MonkeyLoader.GamePacks.Resonite",
                DllPath = gamepack,
                Pin = Pins.Latest,
            };
            if (LinkParser.TryParse(info.Link, out var o, out var r)) { info.Owner = o; info.Repo = r; }
            return info;
        }

        private static string MonkeyLoaderModsDir(string ownAssemblyPath)
        {
            if (string.IsNullOrEmpty(ownAssemblyPath)) return null;
            var rml = Path.GetDirectoryName(ownAssemblyPath);   // ...\rml_mods
            var root = Path.GetDirectoryName(rml);              // Resonite install root
            return root == null ? null : Path.Combine(root, "MonkeyLoader", "Mods");
        }

        /// <summary>Scan MonkeyLoader/Mods/*.nupkg and model them like RML mods (Kind = MlNupkg).</summary>
        public List<ModInfo> EnumerateMonkeyLoaderMods(UpdaterSettings settings)
        {
            var list = new List<ModInfo>();
            var dir = MonkeyLoaderModsDir(settings.OwnAssemblyPath);
            if (dir == null || !Directory.Exists(dir)) return list;

            foreach (var nupkg in Directory.EnumerateFiles(dir, "*.nupkg", SearchOption.AllDirectories))
            {
                if (nupkg.IndexOf("_ModUpdater", StringComparison.OrdinalIgnoreCase) >= 0) continue;   // our backups
                if (!NupkgReader.TryRead(nupkg, out var id, out var ver, out var repo)) continue;

                var info = new ModInfo
                {
                    Kind = ModKind.MlNupkg,
                    Name = id,
                    InstalledVersion = ver,
                    Link = repo,
                    DllPath = nupkg,
                    Pin = settings.GetPin(id),
                    HasBackup = DllInstaller.HasBackup(nupkg, id),
                };
                if (LinkParser.TryParse(repo, out var owner, out var rp)) { info.Owner = owner; info.Repo = rp; }
                list.Add(info);
            }
            return list;
        }

        /// <summary>
        /// Check each candidate against GitHub and, when <paramref name="autoDownload"/> is set,
        /// stage newer DLLs. Returns pending updates produced this run for state tracking.
        /// </summary>
        public async Task<List<PendingUpdate>> CheckAsync(
            List<ModInfo> mods, bool autoDownload, string token, string manifestUrl, CancellationToken ct)
        {
            var pending = new List<PendingUpdate>();
            var pendingLock = new object();
            using var gate = new SemaphoreSlim(MaxConcurrency);

            // Prefer the community manifest when a mod is listed there (direct artifact URLs, no API).
            await _manifest.EnsureLoadedAsync(manifestUrl, ct).ConfigureAwait(false);
            foreach (var info in mods)
            {
                if (info.Status == ModStatus.Skipped || info.Kind == ModKind.Loader) continue;
                if (_manifest.TryGet(info.Name, out var entry))
                {
                    info.Manifest = entry;
                    if (info.Owner == null && LinkParser.TryParse(entry.SourceLocation, out var o, out var r))
                    { info.Owner = o; info.Repo = r; }
                }
            }

            bool HasSource(ModInfo m) => m.Manifest != null || (m.Owner != null && m.Repo != null);

            // Mods with no source at all (no usable Link and not in the manifest).
            foreach (var m in mods.Where(m => m.Status != ModStatus.Skipped && !HasSource(m)))
            {
                m.Status = ModStatus.NoSource;
                if (string.IsNullOrEmpty(m.Detail)) m.Detail = "no source";
            }

            var candidates = mods.Where(m => m.Status != ModStatus.Skipped && HasSource(m)).ToList();
            var tasks = candidates.Select(async info =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await CheckOneAsync(info, autoDownload, token, pending, pendingLock, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    info.Status = ModStatus.Error;
                    info.Detail = ex.Message;
                    _log($"[{info.Name}] error: {ex}");
                }
                finally { gate.Release(); }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            return pending;
        }

        /// <summary>
        /// Pure (no-network) decision of what version a mod should be on, from its current Pin plus the
        /// already-fetched AvailableTags/LatestTag. Sets Status/TargetTag/IsDowngrade/Detail. Call after a
        /// check, or right after the user changes a pin, to refresh the row without re-hitting GitHub.
        /// </summary>
        public static void EvaluateTarget(ModInfo info)
        {
            info.IsDowngrade = false;
            if (info.AvailableTags == null || info.AvailableTags.Count == 0)
            {
                info.Status = ModStatus.NoSource;
                info.TargetTag = null;
                return;
            }

            if (string.Equals(info.Pin, Pins.Hold, StringComparison.OrdinalIgnoreCase))
            {
                info.Status = ModStatus.Held;
                info.Detail = "held on current";
                info.TargetTag = null;
                return;
            }

            bool pinnedToLatest = string.Equals(info.Pin, Pins.Latest, StringComparison.OrdinalIgnoreCase);
            string targetTag = pinnedToLatest
                ? info.LatestTag
                : info.AvailableTags.FirstOrDefault(t => string.Equals(t, info.Pin, StringComparison.OrdinalIgnoreCase)) ?? info.Pin;
            info.TargetTag = targetTag;

            if (pinnedToLatest)
            {
                if (!VersionUtil.IsNewer(targetTag, info.InstalledVersion, out var comparable))
                {
                    info.Status = comparable ? ModStatus.UpToDate : ModStatus.Unknown;
                    info.Detail = comparable ? null : $"can't compare '{info.InstalledVersion}' to '{targetTag}'";
                    return;
                }
            }
            else
            {
                var installedN = VersionUtil.Normalize(info.InstalledVersion);
                var targetN = VersionUtil.Normalize(targetTag);
                if (installedN != null && targetN != null &&
                    string.Equals(installedN, targetN, StringComparison.OrdinalIgnoreCase))
                {
                    info.Status = ModStatus.Pinned;
                    info.Detail = $"pinned to {targetTag}";
                    return;
                }
            }

            info.IsDowngrade = !pinnedToLatest && VersionUtil.IsNewer(info.InstalledVersion, targetTag, out _);
            info.Status = ModStatus.Outdated;
            info.Detail = pinnedToLatest ? null : (info.IsDowngrade ? $"downgrade to {targetTag}" : $"pinned to {targetTag}");
        }

        /// <summary>
        /// Download &amp; stage a single mod's target version on demand (independent of AutoDownload).
        /// Requires the mod to have been checked already (Owner/Repo/TargetTag populated).
        /// </summary>
        public async Task<(bool ok, string detail)> StageAsync(ModInfo info, string token, CancellationToken ct)
        {
            if (info.NotifyOnly) return (false, "notify-only");
            if (string.IsNullOrEmpty(info.TargetTag)) return (false, "no update target");

            var (bytes, fail, detail) = await ResolveAndDownloadAsync(info, info.TargetTag, token, ct).ConfigureAwait(false);
            if (bytes == null) return (false, detail);

            string targetVersion = VersionUtil.Normalize(info.TargetTag) ?? info.TargetTag;
            var inst = PendingQueue.StageOrQueue(info.DllPath, info.Name, targetVersion, bytes);
            if (!inst.Ok) return (false, inst.Detail);

            info.Status = ModStatus.Updated;
            info.HasBackup = true;
            _log($"[{info.Name}] {(inst.Queued ? "queued" : "staged")} {targetVersion}");
            return (true, inst.Queued ? targetVersion + " (queued — closes to apply)" : targetVersion);
        }

        /// <summary>
        /// Merged versions newest-first from BOTH the mod's GitHub releases and the manifest (if either
        /// is available), deduped by normalized version — so the newer of the two always wins. The raw
        /// GitHub tags are recorded on the info for exact API lookups later.
        /// </summary>
        private async Task<List<string>> GetVersionsAsync(ModInfo info, CancellationToken ct)
        {
            var merged = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(IEnumerable<string> tags)
            {
                foreach (var t in tags)
                {
                    var norm = VersionUtil.Normalize(t) ?? t;
                    if (seen.Add(norm)) merged.Add(t);
                }
            }

            info.GitHubTags = new List<string>();
            if (info.Owner != null && info.Repo != null)
            {
                try { info.GitHubTags = await _gh.GetReleaseTagsAsync(info.Owner, info.Repo, ct).ConfigureAwait(false); }
                catch { /* e.g. transient/RSS failure — fall back to the manifest below */ }
            }
            Add(info.GitHubTags);
            if (info.Manifest != null) Add(info.Manifest.Versions);

            merged.Sort((a, b) => VersionUtil.IsNewer(a, b, out _) ? -1 : (VersionUtil.IsNewer(b, a, out _) ? 1 : 0));
            return merged;
        }

        /// <summary>
        /// Resolve and download the artifact bytes for a target version, preferring the manifest's direct
        /// URL (rate-limit-free, sha256-checked) when that version is listed, else the GitHub release asset.
        /// </summary>
        private async Task<(byte[] bytes, ModStatus fail, string detail)> ResolveAndDownloadAsync(
            ModInfo info, string targetTag, string token, CancellationToken ct)
        {
            // 1) Manifest, if it has this exact version.
            if (info.Manifest != null)
            {
                var key = info.Manifest.Versions.FirstOrDefault(v => VersionUtil.Same(v, targetTag));
                if (key != null && info.Manifest.Artifacts.TryGetValue(key, out var arts) && arts.Count > 0)
                {
                    var art = arts.FirstOrDefault(a => a.Url != null && a.Url.EndsWith(info.AssetExtension, StringComparison.OrdinalIgnoreCase))
                              ?? arts.FirstOrDefault(a => a.Url != null);
                    if (art != null)
                    {
                        var data = await _gh.DownloadAsync(art.Url, token, ct).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(art.Sha256) && !Sha256Matches(data, art.Sha256))
                            return (null, ModStatus.Error, "sha256 mismatch");
                        return (data, ModStatus.Updated, null);
                    }
                }
            }

            // 2) GitHub release asset for the matching tag.
            if (info.Owner != null && info.Repo != null)
            {
                var ghTag = info.GitHubTags.FirstOrDefault(t => VersionUtil.Same(t, targetTag)) ?? targetTag;
                var res = await _gh.ResolveAssetAsync(info.Owner, info.Repo, ghTag, info.FileName, info.AssetExtension, token, ct).ConfigureAwait(false);
                if (!res.Ok) return (null, res.FailureStatus, res.Detail);
                var bytes = await _gh.DownloadAsync(res.Asset.DownloadUrl, token, ct).ConfigureAwait(false);
                return (bytes, ModStatus.Updated, null);
            }

            return (null, ModStatus.ManualOnly, $"no source has version {targetTag}");
        }

        private static bool Sha256Matches(byte[] data, string expectedHex)
        {
            using var sha = SHA256.Create();
            var hex = Convert.ToHexString(sha.ComputeHash(data));
            return string.Equals(hex, expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private async Task CheckOneAsync(
            ModInfo info, bool autoDownload, string token,
            List<PendingUpdate> pending, object pendingLock, CancellationToken ct)
        {
            // Version list: from the manifest (no API) when listed, else the repo's RSS feed.
            var tags = await GetVersionsAsync(info, ct).ConfigureAwait(false);
            info.AvailableTags = tags;
            info.PrereleaseTags = tags.Where(GitHubSource.IsPrereleaseTag).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (tags.Count == 0)
            {
                info.Status = ModStatus.NoSource;
                info.Detail = "no releases found";
                return;
            }

            // "Latest" means the newest non-prerelease-looking tag (falling back to newest of any kind).
            string latestTag = tags.FirstOrDefault(t => !GitHubSource.IsPrereleaseTag(t)) ?? tags[0];
            info.LatestTag = latestTag;
            info.LatestVersion = VersionUtil.Normalize(latestTag) ?? latestTag;

            // Decide target/status from the pin (pure, no network).
            EvaluateTarget(info);
            if (info.Status != ModStatus.Outdated) return;   // Hold / UpToDate / Pinned / Unknown — nothing to fetch

            string targetTag = info.TargetTag;
            string targetVersion = VersionUtil.Normalize(targetTag) ?? targetTag;
            _log($"[{info.Name}] target {targetVersion} (installed {info.InstalledVersion}){(info.IsDowngrade ? " [downgrade]" : "")}");

            if (info.NotifyOnly) { info.Detail = $"update available: {targetVersion}"; return; }  // loader: never auto-stage
            if (!autoDownload) return;

            var (bytes, fail, detail) = await ResolveAndDownloadAsync(info, targetTag, token, ct).ConfigureAwait(false);
            if (bytes == null)
            {
                info.Status = fail;
                info.Detail = detail;
                _log($"[{info.Name}] {info.Status}: {info.Detail}");
                return;
            }

            var result = PendingQueue.StageOrQueue(info.DllPath, info.Name, targetVersion, bytes);
            if (!result.Ok)
            {
                info.Status = ModStatus.Error;
                info.Detail = result.Detail;
                _log($"[{info.Name}] install failed: {result.Detail}");
                return;
            }

            info.Status = ModStatus.Updated;
            info.HasBackup = true;
            info.Detail = result.Queued ? "queued — applies when you close Resonite"
                         : (info.IsDowngrade ? "downgraded, restart pending" : null);
            var p = new PendingUpdate
            {
                Name = info.Name,
                FromVersion = info.InstalledVersion,
                ToVersion = targetVersion,
                IsRevert = false,
                At = DateTime.Now.ToString("o"),
            };
            lock (pendingLock) pending.Add(p);
            _log($"[{info.Name}] staged {targetVersion} (restart to apply)");
        }
    }
}
