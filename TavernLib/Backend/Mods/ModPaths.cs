using System.IO;

namespace TavernLib.Backend.Mods;

public static class ModPaths
{
    /// <summary>Twin of layout.py's _safe_basename: the last-line traversal guard
    /// for every id and filename from a repo before it touches disk.</summary>
    public static string SafeBasename(string name)
    {
        if (string.IsNullOrEmpty(name) || name == "." || name == "..")
            throw new ModManagerException($"Unsafe filename '{name}'.");
        if (name.Contains("/") || name.Contains("\\"))
            throw new ModManagerException($"Filename '{name}' must not contain a path separator.");
        // "C:evil.dll" is another directory and "mod.dll:x" an NTFS stream, and
        // Path.GetFileName treats neither colon as a directory part.
        if (name.Contains(":"))
            throw new ModManagerException($"Filename '{name}' must not contain a colon.");
        if (Path.GetFileName(name) != name)
            throw new ModManagerException($"Filename '{name}' must be a bare basename.");
        return name;
    }

    /// <summary>The Mods/ path, without creating it.</summary>
    public static string ModsBase(string gameDir) => Path.Combine(gameDir, "Mods");

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

    /// <summary>Where a folder we didn't install is moved aside when its id's path
    /// is needed. Dot-prefixed, so nothing that scans Mods/ sees it.</summary>
    public static string DisplacedBase(string gameDir) => Path.Combine(ModsBase(gameDir), ".displaced");

    public static string LibrarySidecarPath(string gameDir, string filename) => Path.Combine(UserLibsDir(gameDir), $"{filename}.meta.json");
}
