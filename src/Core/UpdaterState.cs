using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ModUpdater.Core
{
    /// <summary>One mod update that has been staged and is waiting for a Resonite restart.</summary>
    public sealed class PendingUpdate
    {
        public string Name { get; set; }
        public string FromVersion { get; set; }
        public string ToVersion { get; set; }
        public bool IsRevert { get; set; }
        public string At { get; set; }      // ISO timestamp
    }

    /// <summary>
    /// Persists which updates are staged-but-not-yet-loaded (across the session and app restarts)
    /// in <c>rml_mods/_ModUpdater/state.json</c>. Entries are pruned once the mod actually loads at
    /// its target version.
    /// </summary>
    public sealed class UpdaterState
    {
        public List<PendingUpdate> Pending { get; set; } = new List<PendingUpdate>();
        public string LastCheck { get; set; }

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { WriteIndented = true };

        private static string StatePath(string modsDir) =>
            Path.Combine(DllInstaller.BaseDir(modsDir), "state.json");

        public static UpdaterState Load(string modsDir)
        {
            try
            {
                var path = StatePath(modsDir);
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<UpdaterState>(File.ReadAllText(path)) ?? new UpdaterState();
            }
            catch { /* fall through to fresh state */ }
            return new UpdaterState();
        }

        public void Save(string modsDir)
        {
            try
            {
                Directory.CreateDirectory(DllInstaller.BaseDir(modsDir));
                File.WriteAllText(StatePath(modsDir), JsonSerializer.Serialize(this, JsonOpts));
            }
            catch { /* state is advisory; never fail the update over it */ }
        }

        public void AddOrReplacePending(PendingUpdate update)
        {
            Pending.RemoveAll(p => string.Equals(p.Name, update.Name, StringComparison.OrdinalIgnoreCase));
            Pending.Add(update);
        }

        /// <summary>Drop pending entries whose target version is already the one loaded now.</summary>
        public bool PruneApplied(Func<string, string> currentlyLoadedVersionFor)
        {
            int before = Pending.Count;
            Pending.RemoveAll(p =>
            {
                var loaded = currentlyLoadedVersionFor(p.Name);
                return loaded != null && string.Equals(
                    VersionUtil.Normalize(loaded), VersionUtil.Normalize(p.ToVersion), StringComparison.OrdinalIgnoreCase);
            });
            return Pending.Count != before;
        }
    }
}
