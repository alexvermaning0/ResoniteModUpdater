using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ModUpdater.Core
{
    public sealed class PendingEntry
    {
        public string Mod { get; set; }
        public string Version { get; set; }
        public string Target { get; set; }   // absolute path of the installed file to replace
        public string Source { get; set; }   // staged new file in the pending folder
        public string Backup { get; set; }   // where Target is moved before the swap (.bak)
    }

    /// <summary>
    /// Handles updates whose target file is locked by the loader for the whole session (can't be
    /// swapped in-process). The new file is staged to a pending folder, and a detached PowerShell
    /// helper waits for Resonite to exit — when every file is unlocked — then applies the swaps. Used
    /// as a fallback after a live <see cref="DllInstaller.StageUpdate"/> reports the file Locked.
    /// </summary>
    public static class PendingQueue
    {
        private static string _root;     // ...\rml_mods\_ModUpdater\pending
        private static int _gamePid;
        private static Process _helper;
        private static readonly object _lock = new();
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        public static void Init(string ownAssemblyPath)
        {
            try
            {
                var rml = Path.GetDirectoryName(ownAssemblyPath);
                _root = Path.Combine(rml, "_ModUpdater", "pending");
                _gamePid = Process.GetCurrentProcess().Id;
            }
            catch { /* leave uninitialized; StageOrQueue falls back to a plain failure */ }
        }

        /// <summary>Try a live swap; if the file is locked, stage it for the close-and-apply helper.</summary>
        public static InstallResult StageOrQueue(string targetPath, string modName, string version, byte[] bytes)
        {
            var live = DllInstaller.StageUpdate(targetPath, modName, version, bytes);
            if (live.Ok || !live.Locked) return live;
            if (_root == null) return live;   // can't queue — report the lock failure

            try { Enqueue(targetPath, modName, version, bytes); }
            catch (Exception ex) { return InstallResult.Fail("could not queue update: " + ex.Message); }
            return new InstallResult { Ok = true, Queued = true, Detail = "queued — applies when you close Resonite" };
        }

        private static void Enqueue(string targetPath, string modName, string version, byte[] bytes)
        {
            Directory.CreateDirectory(_root);
            var ext = Path.GetExtension(targetPath);
            // Drop any earlier staged copies for this mod (superseded by this download).
            try
            {
                foreach (var stale in Directory.EnumerateFiles(_root, $"{Sanitize(modName)}__*{ext}"))
                    File.Delete(stale);
            }
            catch { }

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var source = Path.Combine(_root, $"{Sanitize(modName)}__{stamp}{ext}");
            File.WriteAllBytes(source, bytes);

            // Same backup convention as live installs (.bak next to the mod) so Revert keeps working.
            var backupDir = DllInstaller.BackupDirFor(Path.GetDirectoryName(targetPath), modName);
            var backup = Path.Combine(backupDir,
                $"{Path.GetFileNameWithoutExtension(targetPath)}__{Sanitize(version)}__{stamp}{ext}.bak");

            var list = LoadManifest();
            list.RemoveAll(e => string.Equals(e.Target, targetPath, StringComparison.OrdinalIgnoreCase));
            list.Add(new PendingEntry { Mod = modName, Version = version, Target = targetPath, Source = source, Backup = backup });
            SaveManifest(list);

            EnsureHelper();
        }

        private static string ManifestPath => Path.Combine(_root, "pending.json");

        private static List<PendingEntry> LoadManifest()
        {
            try
            {
                if (File.Exists(ManifestPath))
                    return JsonSerializer.Deserialize<List<PendingEntry>>(File.ReadAllText(ManifestPath)) ?? new();
            }
            catch { }
            return new List<PendingEntry>();
        }

        private static void SaveManifest(List<PendingEntry> list) =>
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(list, JsonOpts));

        private static void EnsureHelper()
        {
            lock (_lock)
            {
                var script = Path.Combine(_root, "apply-pending.ps1");
                File.WriteAllText(script, HelperScript);
                if (_helper != null && !_helper.HasExited) return;

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -Dir \"{_root}\" -GamePid {_gamePid}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                _helper = Process.Start(psi);
            }
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        // Waits for the game (GamePid) to exit, then applies each pending swap (files unlocked by then).
        // CRITICAL: file ops use -ErrorAction Stop so a failed (still-locked) move is *caught* and the
        // manifest is kept for the next attempt — never deleted unless the swap genuinely succeeded.
        private const string HelperScript = @"
param([string]$Dir, [int]$GamePid)
$manifest = Join-Path $Dir 'pending.json'

function Apply-Pending {
    if (-not (Test-Path -LiteralPath $manifest)) { return $true }
    $entries = @(Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json)
    $remaining = @()
    foreach ($e in $entries) {
        if (-not (Test-Path -LiteralPath $e.Source)) { continue }   # already applied
        try {
            $bdir = Split-Path $e.Backup
            if ($bdir) { New-Item -ItemType Directory -Force -Path $bdir -ErrorAction Stop | Out-Null }
            if (Test-Path -LiteralPath $e.Target) { Move-Item -Force -LiteralPath $e.Target -Destination $e.Backup -ErrorAction Stop }
            Move-Item -Force -LiteralPath $e.Source -Destination $e.Target -ErrorAction Stop
        } catch { $remaining += $e }
    }
    if ($remaining.Count -eq 0) { Remove-Item -Force -LiteralPath $manifest -ErrorAction SilentlyContinue; return $true }
    ConvertTo-Json @($remaining) -Depth 6 | Set-Content -LiteralPath $manifest
    return $false
}

# Wait for the game process to exit (poll ~1s).
while ($true) {
    try { $null = Get-Process -Id $GamePid -ErrorAction Stop } catch { break }
    Start-Sleep -Seconds 1
}
# Game exited: apply now, retrying ~20s while the OS releases the file handle. If a fast relaunch
# re-locks a file, the move is caught and the manifest is preserved for that session's helper to retry.
for ($i = 0; $i -lt 40; $i++) {
    if (Apply-Pending) { break }
    Start-Sleep -Milliseconds 500
}
";
    }
}
