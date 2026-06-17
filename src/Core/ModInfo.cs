using System;

namespace ModUpdater.Core
{
    /// <summary>Result of evaluating / updating a single loaded mod.</summary>
    public enum ModStatus
    {
        Unknown,      // could not parse a version / tag for comparison
        UpToDate,     // on "Latest" and installed version >= latest release
        Outdated,     // a different target version is available (download disabled / notify only)
        Updated,      // the target DLL was downloaded & staged; pending a Resonite restart
        Reverted,     // a previous backup was staged back; pending a Resonite restart
        Pinned,       // pinned to a specific version and already on it
        Held,         // user chose "Hold" — never touched
        NoSource,     // no usable GitHub Link
        Skipped,      // skipped for a structural reason (the updater itself, missing dll)
        ManualOnly,   // target release exists but has no .dll asset to auto-install
        Ambiguous,    // release has multiple .dll assets and none matches the file name
        Error,        // an exception occurred while checking/updating this mod
    }

    // Sentinel pin values; anything else is a concrete release tag.
    public static class Pins
    {
        public const string Latest = "latest";
        public const string Hold = "hold";
    }

    /// <summary>What kind of artifact a mod is, which decides its asset type and install folder.</summary>
    public enum ModKind
    {
        RmlDll,     // ResoniteModLoader mod: a .dll in rml_mods
        MlNupkg,    // MonkeyLoader mod: a .nupkg in MonkeyLoader/Mods
        Loader,     // MonkeyLoader itself (notify-only)
    }

    /// <summary>A loaded mod plus everything we learn about it during a check.</summary>
    public sealed class ModInfo
    {
        public string Name;
        public string Author;
        public string InstalledVersion;
        public string Link;
        public string DllPath;          // absolute path of the installed file (.dll or .nupkg)

        public ModKind Kind = ModKind.RmlDll;
        public bool NotifyOnly;         // true for the loader: surface updates but never auto-stage
        public ManifestEntry Manifest;  // non-null when the community manifest is the source

        public string Owner;            // parsed from Link (or manifest sourceLocation)
        public string Repo;             // parsed from Link (or manifest sourceLocation)
        public string LatestTag;        // newest release tag from GitHub
        public string LatestVersion;    // LatestTag, normalized for display
        public System.Collections.Generic.List<string> AvailableTags = new();  // merged (github + manifest), newest first
        public System.Collections.Generic.List<string> GitHubTags = new();     // raw github tags (for exact API lookups)
        public System.Collections.Generic.HashSet<string> PrereleaseTags = new(System.StringComparer.OrdinalIgnoreCase);

        public string Pin = Pins.Latest;   // user's version selection: "latest" / "hold" / a tag
        public string TargetTag;            // the tag we want installed for this Pin (null for hold/latest-uptodate)
        public bool IsDowngrade;            // true when the wanted change installs an older version than installed

        public ModStatus Status = ModStatus.Unknown;
        public string Detail;           // human-readable note (error text, asset name, etc.)
        public bool HasBackup;          // a restorable backup exists on disk

        public string AssetExtension => Kind == ModKind.MlNupkg ? ".nupkg" : ".dll";
        public string FileName => string.IsNullOrEmpty(DllPath) ? Name + AssetExtension : System.IO.Path.GetFileName(DllPath);

        public override string ToString() =>
            $"{Name} [{InstalledVersion} -> {LatestVersion ?? "?"}] {Status}{(string.IsNullOrEmpty(Detail) ? "" : " (" + Detail + ")")}";
    }
}
