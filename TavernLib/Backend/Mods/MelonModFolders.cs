using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MelonLoader;
using MelonLoader.Resolver;
using MelonLoader.Utils;

namespace TavernLib.Backend.Mods;

// MelonLoader lists Mods/ subfolders once, before any plugin runs, skipping dot folders like .pc/ and
// .server/. Sync edits that private list to match the disk after reconcile, plus this process's build of
// each enabled mod of ours. If the field moves, managed mods don't load at all, never the wrong build.
internal static class MelonModFolders
{
    // MelonFolderHandler's own default exclusions, besides names starting with '.' or '~'.
    private static readonly string[] ExcludedNames = ["Broken", "Retired", "Disabled"];
    // Below Mods/, MelonLoader files these as user libs or plugins, never mod folders; too late for either now.
    private static readonly string[] OtherScanNames = ["UserLibs", "Plugins"];

    public static void SyncForBoot(bool serverProcess)
    {
        try
        {
            Sync(MelonEnvironment.GameRootDirectory, serverProcess ? ModManagerConstants.TargetServer : ModManagerConstants.TargetPc);
        }
        catch (Exception e)
        {
            TavernLogger.Error($"mod folders: couldn't update MelonLoader's mod folder list ({e}); managed mods won't load this session.");
        }
    }

    internal static void Sync(string gameDir, string target)
    {
        var loader = LoaderConfig.Current.Loader;
        if (loader.DisableSubFolderLoad) return;

        if (typeof(MelonLoader.Melons.MelonFolderHandler).GetField("_modDirs", BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null) is not List<string> modDirs)
        {
            TavernLogger.Warn("mod folders: couldn't reach MelonLoader's list of mod folders, so no managed mod "
                + "loads this session.");
            return;
        }

        var modsDir = ModPaths.ModsBase(gameDir);
        if (!Directory.Exists(modsDir)) return;
        var root = Normalize(modsDir);
        var wanted = new List<string>();
        var builds = new List<string>();
        foreach (var dir in Directory.GetDirectories(modsDir).Where(d => !IsExcluded(d)))
        {
            if (!loader.DisableSubFolderManifest && !File.Exists(Path.Combine(dir, ModManagerConstants.RecordName))) continue;
            AddWithSubfolders(dir, wanted);

            // The record decides, never the folder name.
            var name = Path.GetFileName(dir);
            var build = Path.Combine(dir, "." + target);
            if (Directory.Exists(build) && ModRecord.Read(gameDir, name, out var enabled) is { } record && enabled && record.Lists(target))
            {
                AddWithSubfolders(build, wanted);
                builds.Add(name);
            }
        }
        var wantedSet = new HashSet<string>(wanted.Select(Normalize), StringComparer.OrdinalIgnoreCase);

        foreach (var stale in modDirs.Where(d => IsUnder(Normalize(d), root) && !wantedSet.Contains(Normalize(d))).ToList())
        {
            modDirs.Remove(stale);
            MelonAssemblyResolver.RemoveSearchDirectory(stale);
            TavernLogger.Msg($"mod folders: {Path.GetFileName(stale)} won't load this session.");
        }

        var known = new HashSet<string>(modDirs.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        foreach (var dir in wanted.Where(d => !known.Contains(Normalize(d))))
        {
            modDirs.Add(dir);
            MelonAssemblyResolver.AddSearchDirectory(dir);
        }
        if (builds.Count > 0)
            TavernLogger.Msg($"mod folders: loading the {target} build of {string.Join(", ", builds)}.");
    }

    // MelonLoader also loads every non-excluded folder beneath a mod folder, manifest or not.
    private static void AddWithSubfolders(string dir, List<string> into)
    {
        into.Add(dir);
        foreach (var sub in Directory.GetDirectories(dir).Where(s => !IsExcluded(s) && !OtherScanNames.Contains(Path.GetFileName(s))))
            AddWithSubfolders(sub, into);
    }

    private static bool IsExcluded(string dir)
    {
        var name = Path.GetFileName(dir);
        return name.StartsWith("~", StringComparison.Ordinal) || name.StartsWith(".", StringComparison.Ordinal) || ExcludedNames.Contains(name);
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsUnder(string path, string root) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
