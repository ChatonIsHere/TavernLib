using System;

namespace TavernLib.Backend.Mods;

public static class ModManagerConstants
{
    public const string DefaultRepo = "https://raw.githubusercontent.com/ChatonIsHere/CommunityMods/main";

    /// <summary>
    /// Derives a GitHub-style "Author/Repo" display identifier from a repo's
    /// base URL - the first two path segments (works for
    /// raw.githubusercontent.com/&lt;author&gt;/&lt;repo&gt;/&lt;branch&gt; regardless of
    /// branch name or depth beyond that). Display/identification only: a
    /// modlist's `repos` field lists these, but this is never resolved back
    /// into a URL except by looking an already-registered repo up by this same
    /// key in TrustedRepos - it can't be used to synthesize or guess a
    /// pullable URL.
    /// </summary>
    public static string RepoShorthand(string url)
    {
        var path = new Uri(url.TrimEnd('/')).AbsolutePath.Trim('/').Split('/');
        return path.Length >= 2 ? $"{path[0]}/{path[1]}" : url.TrimEnd('/');
    }

    public const int SupportedManifestMajor = 1;
    public const int SupportedIndexMajor = 1;

    public const int MaxDependencyDepth = 5;

    public const int IndexTtlSeconds = 300;

    public const int ZipMaxEntries = 2000;
    public const long ZipMaxTotalUncompressed = 500L * 1024 * 1024;

    /// <summary>The record filename inside each mod folder - also MelonLoader's
    /// folder-load marker (existence check only, content unread).</summary>
    public const string RecordName = "manifest.json";

    /// <summary>What RecordName is renamed to while a mod is disabled, so the
    /// folder no longer contains RecordName and MelonLoader stops scanning it.</summary>
    public const string DisabledRecordName = "manifest.disabled.json";

    /// <summary>Hard wall-clock cap on the whole reconcile pass, since a headless
    /// boot has no user watching a progress bar to give up on.</summary>
    public const int ReconcileMaxSeconds = 300;

    /// <summary>Per-repo/manifest JSON fetch timeout.</summary>
    public const int JsonFetchTimeoutSeconds = 15;

    /// <summary>Per-file download connect/read timeout.</summary>
    public const int DownloadTimeoutSeconds = 30;
}
