using System;
using System.IO;

namespace TavernLib.Backend.Mods;

public static class ModManagerConstants
{
    public const string DefaultRepo = "https://raw.githubusercontent.com/ModdingTavern/TavernMods/main";

    public const string TargetClientWindows = "client_windows";
    public const string TargetServer = "server";
    public const string TargetClientAndroid = "client_android";
    // Any other target name is ignored, so one added later doesn't break this build.
    public static readonly string[] KnownTargets = [TargetClientWindows, TargetServer, TargetClientAndroid];

    // Also MelonLoader's folder-load marker (an existence check; the content is never read).
    public const string RecordName = "manifest.json";
    public const string DisabledRecordName = "manifest.disabled.json";
    // MelonLoader loads any Mods/*.dll, so only a rename disables a loose one.
    public const string DisabledDllSuffix = ".disabled";

    // A trailing slash is not a different repo.
    public static string NormalizeUrl(string url) => (url ?? "").TrimEnd('/');

    public static bool IsDefaultRepo(string url) => NormalizeUrl(url) == NormalizeUrl(DefaultRepo);
}

public static class ModPaths
{
    // Twin of layout.py's _safe_basename: the last-line traversal guard.
    public static string SafeBasename(string name)
    {
        if (string.IsNullOrEmpty(name) || name == "." || name == "..")
            throw new ModManagerException($"Unsafe filename '{name}'.");
        if (name.Contains("/") || name.Contains("\\"))
            throw new ModManagerException($"Filename '{name}' must not contain a path separator.");
        // "C:evil.dll" is another directory and "mod.dll:x" an NTFS stream; GetFileName misses both.
        if (name.Contains(":"))
            throw new ModManagerException($"Filename '{name}' must not contain a colon.");
        if (Path.GetFileName(name) != name)
            throw new ModManagerException($"Filename '{name}' must be a bare basename.");
        return name;
    }

    public static string ModsBase(string gameDir) => Path.Combine(gameDir, "Mods");

    public static string UserLibsDir(string gameDir)
    {
        var d = Path.Combine(gameDir, "UserLibs");
        Directory.CreateDirectory(d);
        return d;
    }

    public static string ModDirPath(string gameDir, string modId) => Path.Combine(ModsBase(gameDir), SafeBasename(modId));

    // Dot-prefixed, so MelonLoader skips it until MelonModFolders adds it back.
    public static string TargetDirPath(string gameDir, string modId, string target) => Path.Combine(ModDirPath(gameDir, modId), "." + target);

    public static string ModRecordPath(string gameDir, string modId) => Path.Combine(ModDirPath(gameDir, modId), ModManagerConstants.RecordName);

    public static string DisabledRecordPath(string gameDir, string modId) => Path.Combine(ModDirPath(gameDir, modId), ModManagerConstants.DisabledRecordName);

    public static string LibrarySidecarPath(string gameDir, string filename) => Path.Combine(UserLibsDir(gameDir), $"{filename}.meta.json");
}

public class ModManagerException(string message) : Exception(message)
{
    // The useful cause of a failed fetch is often only in an inner exception.
    public static string Describe(Exception e)
    {
        var message = e.Message;
        for (var inner = e.InnerException; inner != null; inner = inner.InnerException)
            message += $" -> {inner.GetType().Name}: {inner.Message}";
        return message;
    }
}
