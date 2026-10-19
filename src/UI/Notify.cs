using System.Collections.Generic;
using System.Linq;
using Elements.Core;
using FrooxEngine;
using ModUpdater.Core;

namespace ModUpdater.UI
{
    /// <summary>In-world toast notifications. Must be called on a world thread.</summary>
    public static class Notify
    {
        /// <summary>Floating text message in front of the user.</summary>
        public static void Toast(string text, colorX color)
        {
            try
            {
                // (message, color, distance, showTime, size, floatUpTime, floatUpDistance, textOutline)
                NotificationMessage.SpawnTextMessage(text, color, 0.7f, 10f, 0.1f, 1.2f, 0.15f, 0.3f);
            }
            catch { /* notifications are best-effort */ }
        }

        /// <summary>Summarize a completed check as a single toast.</summary>
        public static void Summary(List<ModInfo> results)
        {
            if (results == null || results.Count == 0) { Toast("ModUpdater: no mods found", colorX.Yellow); return; }

            var updatedMods = results.Where(m => m.Status == ModStatus.Updated).ToList();
            int outdated = results.Count(m => m.Status == ModStatus.Outdated);
            int errors = results.Count(m => m.Status == ModStatus.Error);

            if (updatedMods.Count > 0)
            {
                // MonkeyLoader mods swapped live reload themselves; RML mods (and queued swaps) need a restart.
                var hot = updatedMods.Where(m => m.HotReloaded).ToList();
                var restart = updatedMods.Where(m => !m.HotReloaded).ToList();

                var lines = new List<string>();
                if (restart.Count > 0)
                    lines.Add($"updated {restart.Count} mod(s) — restart Resonite to apply\n{Names(restart)}");
                if (hot.Count > 0)
                    lines.Add($"hot-reloaded {hot.Count} MonkeyLoader mod(s) — no restart needed\n{Names(hot)}");
                Toast("ModUpdater: " + string.Join("\n", lines), colorX.Green);
            }
            else if (outdated > 0)
            {
                Toast($"ModUpdater: {outdated} update(s) available (auto-download is off)", colorX.Orange);
            }
            else
            {
                var tail = errors > 0 ? $" ({errors} error(s))" : "";
                Toast($"ModUpdater: all mods up to date{tail}", colorX.Cyan);
            }
        }

        private static string Names(List<ModInfo> mods) =>
            string.Join(", ", mods.Select(m => m.Name).Take(6));
    }
}
