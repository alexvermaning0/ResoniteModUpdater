using System;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace ModUpdater.Core
{
    /// <summary>Reads the .nuspec metadata out of a MonkeyLoader mod's .nupkg (a zip).</summary>
    public static class NupkgReader
    {
        public static bool TryRead(string nupkgPath, out string id, out string version, out string repoUrl)
        {
            id = version = repoUrl = null;
            try
            {
                using var zip = ZipFile.OpenRead(nupkgPath);
                var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
                if (entry == null) return false;

                using var stream = entry.Open();
                var doc = XDocument.Load(stream);
                var ns = doc.Root.GetDefaultNamespace();
                var md = doc.Root.Element(ns + "metadata");
                if (md == null) return false;

                id = (string)md.Element(ns + "id");
                version = (string)md.Element(ns + "version");
                repoUrl = (string)md.Element(ns + "repository")?.Attribute("url")
                          ?? (string)md.Element(ns + "projectUrl");

                return !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(version);
            }
            catch { return false; }
        }
    }
}
