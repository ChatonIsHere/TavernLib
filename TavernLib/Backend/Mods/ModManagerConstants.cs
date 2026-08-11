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
    ///
    /// Accepts either form it might be handed: a full base URL, or a string
    /// that is ALREADY a shorthand (`removerepo` and the TrustedRepos lookups
    /// take either). A relative string like "Author/Repo" is not a valid
    /// absolute Uri, so this degrades to treating the input as the path rather
    /// than throwing UriFormatException - matching modmanager.py's
    /// _repo_shorthand, which gets that behaviour free from urlparse.
    /// </summary>
    public static string RepoShorthand(string url)
    {
        var trimmed = (url ?? "").TrimEnd('/');
        if (trimmed.Length == 0) return trimmed;
        var path = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ? uri.AbsolutePath : trimmed;
        var parts = path.Trim('/').Split('/');
        return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : trimmed;
    }

    public const int SupportedManifestMajor = 1;
    public const int SupportedIndexMajor = 1;

    public const int MaxDependencyDepth = 5;

    /// <summary>The two kinds of process a mod closure can be installed into.
    /// Strings rather than an enum, matching modmanager's `side` exactly, since
    /// the dependency resolver either side runs has to make the same call on the
    /// same data - a client-only dependency skipped by the launcher must be
    /// skipped here too, or the crash this prevents comes back on headless
    /// hosts only.</summary>
    public const string SideClient = "client";
    public const string SideServer = "server";

    public const int IndexTtlSeconds = 300;

    public const int ZipMaxEntries = 2000;
    public const long ZipMaxTotalUncompressed = 500L * 1024 * 1024;

    /// <summary>The record filename inside each mod folder - also MelonLoader's
    /// folder-load marker (existence check only, content unread).</summary>
    public const string RecordName = "manifest.json";

    /// <summary>What RecordName is renamed to while a mod is disabled, so the
    /// folder no longer contains RecordName and MelonLoader stops scanning it.</summary>
    public const string DisabledRecordName = "manifest.disabled.json";

    /// <summary>Appended to a loose root Mods/&lt;name&gt;.dll to disable it. A root
    /// dll has no manifest to hide behind and MelonLoader loads any Mods/*.dll
    /// it finds, so renaming the file is the only thing that stops it loading.
    /// Mirrors modmanager.py's disable_untracked_dll.</summary>
    public const string DisabledDllSuffix = ".disabled";

    /// <summary>Hard wall-clock cap on the whole reconcile pass, since a headless
    /// boot has no user watching a progress bar to give up on.</summary>
    public const int ReconcileMaxSeconds = 300;

    /// <summary>Per-repo/manifest JSON fetch timeout.</summary>
    public const int JsonFetchTimeoutSeconds = 15;

    /// <summary>Per-file download connect/read timeout.</summary>
    public const int DownloadTimeoutSeconds = 30;
}
