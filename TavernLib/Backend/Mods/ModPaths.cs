using System.IO;

namespace TavernLib.Backend.Mods;

public static class ModPaths
{
    /// <summary>
    /// Last-line path-traversal guard, applied to a mod id and every install
    /// filename right before it touches disk. Throws if name has any directory
    /// part, contains '/' or '\' or ':', or is "." / "..". An id/filename from
    /// an unreviewed third-party repo can't be trusted to be path-safe.
    /// </summary>
    public static string SafeBasename(string name)
    {
        if (string.IsNullOrEmpty(name) || name == "." || name == "..")
            throw new ModManagerException($"Unsafe filename '{name}'.");
        if (name.Contains("/") || name.Contains("\\"))
            throw new ModManagerException($"Filename '{name}' must not contain a path separator.");
        // A colon is a drive separator ("C:evil.dll" resolves somewhere else
        // entirely) and, on NTFS, the alternate-data-stream separator:
        // "mod.dll:x" opens a hidden stream on mod.dll rather than a file of
        // its own. Path.GetFileName treats neither as a directory part, so the
        // check below doesn't catch it. No legal Windows filename has one.
        if (name.Contains(":"))
            throw new ModManagerException($"Filename '{name}' must not contain a colon.");
        if (Path.GetFileName(name) != name)
            throw new ModManagerException($"Filename '{name}' must be a bare basename.");
        return name;
    }

    /// <summary>The Mods/ path, without creating it.</summary>
    public static string ModsBase(string gameDir) => Path.Combine(gameDir, "Mods");

    public static string ModsDir(string gameDir)
    {
        var d = ModsBase(gameDir);
        Directory.CreateDirectory(d);
        return d;
    }

    public static string UserLibsDir(string gameDir)
    {
        var d = Path.Combine(gameDir, "UserLibs");
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>A mod's own folder: Mods/&lt;id&gt;/.</summary>
    public static string ModDirPath(string gameDir, string modId) => Path.Combine(ModsBase(gameDir), SafeBasename(modId));

    public static string ModRecordPath(string gameDir, string modId) => Path.Combine(ModDirPath(gameDir, modId), ModManagerConstants.RecordName);

    public static string DisabledRecordPath(string gameDir, string modId) => Path.Combine(ModDirPath(gameDir, modId), ModManagerConstants.DisabledRecordName);

    /// <summary>Where a mod is assembled before being swapped into place atomically.
    /// Dot-prefixed so MelonLoader ignores it even if a crash leaves one behind.</summary>
    public static string StagingPath(string gameDir, string modId) => Path.Combine(ModsBase(gameDir), $".{SafeBasename(modId)}.installing");

    /// <summary>Where a Mods/&lt;name&gt;/ folder we didn't install goes when a mod of
    /// the same id needs that path. Dot-prefixed, so nothing that scans Mods/ -
    /// MelonLoader, the installed lister, the untracked lister - sees what's
    /// parked in here. An operator's own files are moved aside, never deleted,
    /// because a headless install has nobody to ask.</summary>
    public static string DisplacedBase(string gameDir) => Path.Combine(ModsBase(gameDir), ".displaced");

    public static string LibrarySidecarPath(string gameDir, string filename) => Path.Combine(UserLibsDir(gameDir), $"{filename}.meta.json");
}
