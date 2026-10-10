using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MelonLoader;
using MelonLoader.Resolver;

namespace TavernLib.Backend.Mods;

/// <summary>
/// MelonLoader decides which Mods/ subfolders to load exactly once, in
/// Core.Initialize (MelonFolderHandler.ScanForFolders), before any plugin runs;
/// only the loose Mods/*.dll files are listed later, at LoadMelons(Mods). A
/// headless reconcile runs between the two, so on its own a mod it just installed
/// into Mods/&lt;id&gt;/ wouldn't load until the next boot, and one it just disabled
/// would load anyway - while the handshake, which reads the disk, told clients
/// the opposite. <see cref="Sync"/> brings MelonLoader's folder list in line with
/// what reconcile left on disk.
///
/// The list is a private field of MelonLoader 0.7's MelonFolderHandler, reached by
/// reflection. If a MelonLoader update moves it, this says so and changes simply
/// take effect on the next restart, as before.
/// </summary>
internal static class MelonModFolders
{
    // MelonFolderHandler's own default exclusions.
    private static readonly string[] ExcludedPrefixes = ["~", "."];
    private static readonly string[] ExcludedNames = ["Broken", "Retired", "Disabled"];

    public static void Sync(string modsDir)
    {
        var loader = LoaderConfig.Current.Loader;
        if (loader.DisableSubFolderLoad) return;

        var field = typeof(MelonLoader.Melons.MelonFolderHandler)
            .GetField("_modDirs", BindingFlags.NonPublic | BindingFlags.Static);
        if (field?.GetValue(null) is not List<string> modDirs)
        {
            TavernLogger.Warn("mod reconcile: couldn't reach MelonLoader's list of mod folders, so mods installed or "
                + "disabled just now take effect on the next restart.");
            return;
        }

        var root = Normalize(modsDir);
        var wanted = new List<string>();
        foreach (var dir in Directory.GetDirectories(modsDir))
        {
            if (IsExcluded(dir)) continue;
            if (!loader.DisableSubFolderManifest && !File.Exists(Path.Combine(dir, ModManagerConstants.RecordName))) continue;
            AddWithSubfolders(dir, wanted);
        }
        var wantedSet = new HashSet<string>(wanted.Select(Normalize), StringComparer.OrdinalIgnoreCase);

        foreach (var stale in modDirs.Where(d => IsUnder(Normalize(d), root) && !wantedSet.Contains(Normalize(d))).ToList())
        {
            modDirs.Remove(stale);
            MelonAssemblyResolver.RemoveSearchDirectory(stale);
            TavernLogger.Msg($"mod reconcile: {Path.GetFileName(stale)} won't load this session.");
        }

        var known = new HashSet<string>(modDirs.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        foreach (var dir in wanted.Where(d => !known.Contains(Normalize(d))))
        {
            modDirs.Add(dir);
            MelonAssemblyResolver.AddSearchDirectory(dir);
            TavernLogger.Msg($"mod reconcile: {Path.GetFileName(dir)} will load this session.");
        }
    }

    // MelonLoader loads every non-excluded folder beneath a mod folder too, with
    // no manifest required below the first level.
    private static void AddWithSubfolders(string dir, List<string> into)
    {
        into.Add(dir);
        foreach (var sub in Directory.GetDirectories(dir))
            if (!IsExcluded(sub)) AddWithSubfolders(sub, into);
    }

    private static bool IsExcluded(string dir)
    {
        var name = Path.GetFileName(dir);
        return ExcludedPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))
            || ExcludedNames.Contains(name);
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsUnder(string path, string root) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
