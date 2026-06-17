using System;
using System.IO;
using System.Linq;

namespace ModUpdater.Core
{
    public sealed class InstallResult
    {
        public bool Ok;
        public string Detail;
        public string BackupPath;   // where the previous dll was moved to (for state/rollback)
        public bool Locked;         // failed because the file is held by another process (use the helper)
        public bool Queued;         // staged to the pending queue; applies when Resonite closes
        public static InstallResult Fail(string why) => new InstallResult { Ok = false, Detail = why };
    }

    /// <summary>
    /// Installs a downloaded DLL over a currently-loaded one. A loaded DLL on Windows cannot be
    /// overwritten or deleted, but it CAN be moved/renamed (same volume) — so we move the old file
    /// into a backups folder and write the new bytes in its place. The change takes effect on the
    /// next Resonite launch; the moved-aside file doubles as the rollback backup.
    /// </summary>
    public static class DllInstaller
    {
        private const int KeepBackupsPerMod = 3;

        public static string BaseDir(string modsDir) => Path.Combine(modsDir, "_ModUpdater");
        public static string BackupsRoot(string modsDir) => Path.Combine(BaseDir(modsDir), "backups");

        public static string BackupDirFor(string modsDir, string modName) =>
            Path.Combine(BackupsRoot(modsDir), Sanitize(modName));

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        // Accept a PE/DLL ("MZ") or a .nupkg / zip ("PK").
        private static bool LooksValid(byte[] bytes) =>
            bytes != null && bytes.Length > 256 &&
            ((bytes[0] == 0x4D && bytes[1] == 0x5A) || (bytes[0] == 0x50 && bytes[1] == 0x4B));

        /// <summary>Move the loaded dll aside (backup) and write the new bytes in its place.</summary>
        public static InstallResult StageUpdate(string dllPath, string modName, string newVersionLabel, byte[] newBytes)
        {
            if (!LooksValid(newBytes))
                return InstallResult.Fail("downloaded file is not a valid DLL/nupkg");
            if (!File.Exists(dllPath))
                return InstallResult.Fail("installed file not found: " + dllPath);

            var modsDir = Path.GetDirectoryName(dllPath);
            var backupDir = BackupDirFor(modsDir, modName);
            Directory.CreateDirectory(backupDir);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var label = string.IsNullOrWhiteSpace(newVersionLabel) ? "prev" : Sanitize(newVersionLabel);
            // ".bak" so neither RML (rml_mods) nor MonkeyLoader (recursive Mods scan) ever loads a backup.
            var backupPath = Path.Combine(backupDir,
                $"{Path.GetFileNameWithoutExtension(dllPath)}__{label}__{stamp}{Path.GetExtension(dllPath)}.bak");

            // The loaded file can be transiently locked (a file watcher / AV / momentary read), so retry.
            // A persistent lock (the loader holding the file all session) can't be beaten in-process —
            // the caller falls back to the close-and-apply helper for those (InstallResult.Locked).
            if (!TryWithRetry(() => File.Move(dllPath, backupPath), out var moveErr))
                return new InstallResult { Ok = false, Locked = true, Detail = "could not move current file aside: " + moveErr };

            try
            {
                File.WriteAllBytes(dllPath, newBytes);
            }
            catch (Exception ex)
            {
                // Roll back the move so the user is never left without the mod.
                try { if (!File.Exists(dllPath)) File.Move(backupPath, dllPath); } catch { /* best effort */ }
                return InstallResult.Fail("could not write new file: " + ex.Message);
            }

            PruneBackups(backupDir);
            return new InstallResult { Ok = true, BackupPath = backupPath };
        }

        /// <summary>Run a file op, retrying through transient sharing violations.</summary>
        private static bool TryWithRetry(Action op, out string error, int attempts = 8, int delayMs = 300)
        {
            error = null;
            for (int i = 0; ; i++)
            {
                try { op(); return true; }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && i < attempts - 1)
                {
                    System.Threading.Thread.Sleep(delayMs);
                }
                catch (Exception ex) { error = ex.Message; return false; }
            }
        }

        /// <summary>True if at least one backup exists for this mod.</summary>
        public static bool HasBackup(string dllPath, string modName)
        {
            var dir = BackupDirFor(Path.GetDirectoryName(dllPath), modName);
            return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.bak").Any();
        }

        /// <summary>Most recent backup file for a mod, or null.</summary>
        public static string LatestBackup(string dllPath, string modName)
        {
            var dir = BackupDirFor(Path.GetDirectoryName(dllPath), modName);
            if (!Directory.Exists(dir)) return null;
            return Directory.EnumerateFiles(dir, "*.bak")
                            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                            .FirstOrDefault();
        }

        /// <summary>Restore the most recent backup over the current dll (effective next launch).</summary>
        public static InstallResult RevertToLatest(string dllPath, string modName)
        {
            var backup = LatestBackup(dllPath, modName);
            if (backup == null) return InstallResult.Fail("no backup to revert to");

            byte[] bytes;
            try { bytes = File.ReadAllBytes(backup); }
            catch (Exception ex) { return InstallResult.Fail("could not read backup: " + ex.Message); }

            // Stage the backup just like an update (queues to the helper if the file is locked).
            var result = PendingQueue.StageOrQueue(dllPath, modName, "revert", bytes);
            if (result.Ok)
            {
                // The freshly-restored file is now identical to the backup we used; remove that backup
                // so "latest backup" points at the version we just replaced, keeping revert toggling sane.
                try { File.Delete(backup); } catch { /* best effort */ }
            }
            return result;
        }

        private static void PruneBackups(string backupDir)
        {
            try
            {
                var old = Directory.EnumerateFiles(backupDir, "*.bak")
                                   .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                                   .Skip(KeepBackupsPerMod)
                                   .ToList();
                foreach (var f in old) File.Delete(f);
            }
            catch { /* pruning is best-effort */ }
        }
    }
}
