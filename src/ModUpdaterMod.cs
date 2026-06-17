using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FrooxEngine;
using HarmonyLib;
using ResoniteModLoader;
using ModUpdater.Core;
using ModUpdater.UI;

namespace ModUpdater
{
    public class ModUpdaterMod : ResoniteMod
    {
        public override string Name => "ModUpdater";
        public override string Author => "alexvermaning0";
        public override string Version => "1.1.0";
        public override string Link => "https://github.com/alexvermaning0/ResoniteModUpdater";

        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<bool> ENABLED =
            new("Enabled", "Master switch for the updater", () => true);

        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<bool> AUTO_DOWNLOAD =
            new("AutoDownload", "Download & stage target DLLs automatically (off = notify only). Off by default so the first run never overwrites a modified/forked mod before you can set it to Hold.", () => false);

        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<bool> CHECK_ON_STARTUP =
            new("CheckOnStartup", "Check for updates shortly after Resonite starts", () => true);

        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<int> STARTUP_DELAY =
            new("StartupDelaySeconds", "Seconds to wait after launch before the first check", () => 30);

        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<string> VERSION_PINS =
            new("VersionPins", "Per-mod version selection as JSON (managed by the panel: latest/hold/tag)", () => "");

        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<string> GITHUB_TOKEN =
            new("GitHubToken", "Optional GitHub token to raise the API rate limit", () => "");

        internal static ModUpdaterMod Instance;
        internal static ModConfiguration Config;
        internal static List<ModInfo> LastResults = new();

        private readonly UpdateEngine _engine = new(msg => Msg(msg));
        private int _running; // 0/1 guard so checks never overlap

        public override void OnEngineInit()
        {
            Instance = this;
            Config = GetConfiguration();

            // Adds the "Mod Updater" tab to the userspace dash (see UpdaterScreen).
            new Harmony("dev.alex.modupdater").PatchAll();

            // Set up the close-and-apply helper queue (for mods whose files are locked all session).
            Core.PendingQueue.Init(SafeLocation());

            if (!Config.GetValue(ENABLED))
            {
                Msg("ModUpdater disabled via config; not scheduling a check.");
                return;
            }

            if (Config.GetValue(CHECK_ON_STARTUP))
                _ = ScheduleStartupCheckAsync();
            else
                Msg("ModUpdater loaded (startup check disabled).");
        }

        private async Task ScheduleStartupCheckAsync()
        {
            try
            {
                // Wait for the userspace world to exist, then honour the configured delay.
                while (Userspace.UserspaceWorld == null)
                    await Task.Delay(500).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, Config.GetValue(STARTUP_DELAY)))).ConfigureAwait(false);

                var results = await RunCheckAsync().ConfigureAwait(false);
                MarshalToUserspace(() =>
                {
                    Notify.Summary(results);
                    UpdaterScreen.Refresh();   // update the dash tab with the results
                });
            }
            catch (Exception ex) { Error("startup check failed: " + ex); }
        }

        internal UpdaterSettings BuildSettings()
        {
            return new UpdaterSettings
            {
                Enabled = Config.GetValue(ENABLED),
                AutoDownload = Config.GetValue(AUTO_DOWNLOAD),
                GitHubToken = Config.GetValue(GITHUB_TOKEN) ?? "",
                Pins = LoadPins(),
                OwnAssemblyPath = SafeLocation(),
            };
        }

        private Dictionary<string, string> LoadPins()
        {
            try
            {
                var s = Config.GetValue(VERSION_PINS);
                if (!string.IsNullOrWhiteSpace(s))
                {
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(s);
                    if (parsed != null) return new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { /* fall through to empty */ }
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static string SafeLocation()
        {
            try { return typeof(ModUpdaterMod).Assembly.Location ?? ""; } catch { return ""; }
        }

        /// <summary>Run a full check (and stage updates if AutoDownload). Background-thread safe.</summary>
        internal async Task<List<ModInfo>> RunCheckAsync()
        {
            if (Interlocked.Exchange(ref _running, 1) == 1)
            {
                Msg("a check is already running; ignoring re-entry.");
                return LastResults;
            }
            try
            {
                var settings = BuildSettings();
                var mods = _engine.Enumerate(settings);

                var state = UpdaterState.Load(ModsDir(mods));
                state.PruneApplied(UpdateEngine.LoadedVersionFor); // clear updates that already loaded

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var pending = await _engine.CheckAsync(mods, settings.AutoDownload, settings.GitHubToken, settings.ManifestUrl, cts.Token)
                                           .ConfigureAwait(false);

                // Mark mods that are still waiting for a restart from this or a prior session.
                foreach (var p in pending) state.AddOrReplacePending(p);
                foreach (var info in mods)
                    if (info.Status != ModStatus.Updated &&
                        state.Pending.Any(p => string.Equals(p.Name, info.Name, StringComparison.OrdinalIgnoreCase)))
                        info.Detail = (info.Detail == null ? "" : info.Detail + "; ") + "staged, restart pending";

                state.LastCheck = DateTime.Now.ToString("o");
                state.Save(ModsDir(mods));

                LastResults = mods;
                LogSummary(mods);
                return mods;
            }
            finally { Interlocked.Exchange(ref _running, 0); }
        }

        /// <summary>Whether auto-download is currently enabled.</summary>
        internal bool AutoDownloadEnabled => Config.GetValue(AUTO_DOWNLOAD);

        /// <summary>Toggle auto-download and persist.</summary>
        internal void SetAutoDownload(bool on)
        {
            Config.Set(AUTO_DOWNLOAD, on);
            Config.Save(true);
            Msg($"auto-download {(on ? "enabled" : "disabled")}");
        }

        /// <summary>Current version pin for a mod ("latest" / "hold" / a tag).</summary>
        internal string GetPin(string modName)
        {
            var pins = LoadPins();
            return pins.TryGetValue(modName, out var p) && !string.IsNullOrWhiteSpace(p) ? p : Pins.Latest;
        }

        /// <summary>Set (or clear, when "latest") a mod's version pin and persist.</summary>
        internal void SetPin(string modName, string pin)
        {
            var pins = LoadPins();
            if (string.IsNullOrWhiteSpace(pin) || string.Equals(pin, Pins.Latest, StringComparison.OrdinalIgnoreCase))
                pins.Remove(modName);          // "latest" is the default; store nothing
            else
                pins[modName] = pin;

            Config.Set(VERSION_PINS, System.Text.Json.JsonSerializer.Serialize(pins));
            Config.Save(true);
            Msg($"{modName} version set to '{pin}'");
        }

        /// <summary>Download &amp; stage a single mod's target version now (ignores the AutoDownload toggle).</summary>
        internal void UpdateOne(ModInfo info)
        {
            UI.Notify.Toast($"Updating {info.Name}…", Elements.Core.colorX.Cyan);
            Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    var (ok, detail) = await _engine.StageAsync(info, Config.GetValue(GITHUB_TOKEN) ?? "", cts.Token).ConfigureAwait(false);
                    var modsDir = System.IO.Path.GetDirectoryName(info.DllPath);
                    if (ok)
                    {
                        var state = UpdaterState.Load(modsDir);
                        state.AddOrReplacePending(new PendingUpdate
                        {
                            Name = info.Name, FromVersion = info.InstalledVersion, ToVersion = detail,
                            IsRevert = false, At = DateTime.Now.ToString("o"),
                        });
                        state.Save(modsDir);
                    }
                    MarshalToUserspace(() =>
                    {
                        if (ok) UI.Notify.Toast($"Staged {info.Name} {detail} — restart to apply", Elements.Core.colorX.Green);
                        else UI.Notify.Toast($"Update {info.Name} failed: {detail}", Elements.Core.colorX.Red);
                        UI.UpdaterScreen.Refresh();
                    });
                }
                catch (Exception ex) { Error($"UpdateOne {info.Name} failed: " + ex); }
            });
        }

        /// <summary>Revert a mod to its most recent backup (effective next restart).</summary>
        internal void Revert(ModInfo info)
        {
            if (string.IsNullOrEmpty(info.DllPath)) return;
            var result = DllInstaller.RevertToLatest(info.DllPath, info.Name);
            if (!result.Ok) { Warn($"revert {info.Name} failed: {result.Detail}"); return; }

            info.Status = ModStatus.Reverted;
            info.HasBackup = DllInstaller.HasBackup(info.DllPath, info.Name);

            var modsDir = System.IO.Path.GetDirectoryName(info.DllPath);
            var state = UpdaterState.Load(modsDir);
            state.AddOrReplacePending(new PendingUpdate
            {
                Name = info.Name,
                FromVersion = info.InstalledVersion,
                ToVersion = "(previous)",
                IsRevert = true,
                At = DateTime.Now.ToString("o"),
            });
            state.Save(modsDir);
            Msg($"reverted {info.Name}; restart to apply.");
        }

        private static string ModsDir(List<ModInfo> mods)
        {
            var withPath = mods.FirstOrDefault(m => !string.IsNullOrEmpty(m.DllPath));
            return withPath != null ? System.IO.Path.GetDirectoryName(withPath.DllPath) : null;
        }

        /// <summary>Run an action on the userspace world thread (UI / notifications must do this).</summary>
        internal static void MarshalToUserspace(Action action)
        {
            var world = Userspace.UserspaceWorld;
            if (world != null) world.RunSynchronously(action);
            else action(); // fallback; should not happen post-init
        }

        private void LogSummary(List<ModInfo> mods)
        {
            int updated = mods.Count(m => m.Status == ModStatus.Updated);
            int outdated = mods.Count(m => m.Status == ModStatus.Outdated);
            Msg($"check complete: {mods.Count} mods, {updated} updated (restart pending), {outdated} outdated.");
        }
    }
}
