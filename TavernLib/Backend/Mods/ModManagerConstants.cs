using System;

namespace TavernLib.Backend.Mods;

public static class ModManagerConstants
{
    public const string DefaultRepo = "https://raw.githubusercontent.com/ChatonIsHere/CommunityMods/main";

    /// <summary>URL identity for repos and downloads: a trailing slash is not a
    /// different repo.</summary>
    public static string NormalizeUrl(string url) => (url ?? "").TrimEnd('/');

    public static bool IsDefaultRepo(string url) => NormalizeUrl(url) == NormalizeUrl(DefaultRepo);

    /// <summary>"Author/Repo" from a repo's base URL (its first two path segments),
    /// for display and as the TrustedRepos key; never turned back into a URL. A
    /// shorthand passes through unchanged, as in repos.py's _repo_shorthand.</summary>
    public static string RepoShorthand(string url)
    {
        var trimmed = NormalizeUrl(url);
        if (trimmed.Length == 0) return trimmed;
        var path = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ? uri.AbsolutePath : trimmed;
        var parts = path.Trim('/').Split('/');
        return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : trimmed;
    }

    public const int SupportedManifestMajor = 2;
    public const int SupportedIndexMajor = 2;

    public const int MaxDependencyDepth = 5;

    /// <summary>The kinds of process an artifact is built for. TavernLib only
    /// ever installs <see cref="TargetServer"/>.</summary>
    public const string TargetPc = "pc";
    public const string TargetServer = "server";
    public const string TargetQuest = "quest";

    /// <summary>Any other target name is ignored, so one added later doesn't
    /// break this build.</summary>
    public static readonly string[] KnownTargets = [TargetPc, TargetServer, TargetQuest];

    public const string PackageDll = "dll";
    public const string PackageZip = "zip";

    public const int ZipMaxEntries = 2000;
    public const long ZipMaxTotalUncompressed = 500L * 1024 * 1024;

    /// <summary>The record filename inside each mod folder - also MelonLoader's
    /// folder-load marker (existence check only, content unread).</summary>
    public const string RecordName = "manifest.json";

    /// <summary>What RecordName is renamed to while a mod is disabled, so the
    /// folder no longer contains RecordName and MelonLoader stops scanning it.</summary>
    public const string DisabledRecordName = "manifest.disabled.json";

    /// <summary>Appended to a loose root DLL to disable it: MelonLoader loads any
    /// Mods/*.dll, so only a rename stops it.</summary>
    public const string DisabledDllSuffix = ".disabled";

    /// <summary>Wall-clock cap on a reconcile pass; nobody watches a headless boot.</summary>
    public const int ReconcileMaxSeconds = 300;

    /// <summary>Largest repository.json or manifest body accepted.</summary>
    public const long MaxJsonBytes = 8L * 1024 * 1024;

    /// <summary>Per-repo/manifest JSON fetch timeout.</summary>
    public const int JsonFetchTimeoutSeconds = 15;

    /// <summary>Per-file download connect/read timeout.</summary>
    public const int DownloadTimeoutSeconds = 30;
}
