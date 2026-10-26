using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MelonLoader;
using MelonLoader.Resolver;
using MelonLoader.Utils;

namespace TavernLib.Backend.Mods;

/// <summary>
/// MelonLoader lists Mods/ subfolders once, before any plugin runs, and skips
/// dot folders, so neither .pc/ nor .server/ ever loads on its own. Sync edits
/// that list (a private field of MelonLoader 0.7's MelonFolderHandler, by
/// reflection) to match the disk after reconcile, plus this process's target
/// build of every enabled mod of ours that lists it. If the field moves,
/// managed mods don't load at all, never the wrong build.
/// </summary>
internal static class MelonModFolders
{
    // MelonFolderHandler's own default exclusions.
    private static readonly string[] ExcludedPrefixes = ["~", "."];
    private static readonly string[] ExcludedNames = ["Broken", "Retired", "Disabled"];

    // Below Mods/, MelonLoader files folders with these names as user libs or
    // plugins, never as mod folders. Too late for either by now.
    private static readonly string[] OtherScanNames = ["UserLibs", "Plugins"];

    /// <summary>The boot entry point, for every role, before MelonLoader loads
    /// Mods: .server/ builds in a server process (/start_server), .pc/ in any other.</summary>
    public static void SyncForBoot(bool serverProcess)
    {
        var target = serverProcess ? ModManagerConstants.TargetServer : ModManagerConstants.TargetPc;
        try
        {
            Sync(MelonEnvironment.GameRootDirectory, target);
        }
        catch (Exception e)
        {
            TavernLogger.Error($"mod folders: couldn't update MelonLoader's mod folder list ({e}); managed mods won't load this session.");
        }
    }

    /// <summary>Makes _modDirs MelonLoader's own Mods/ scan of the disk as it is
    /// now, plus .&lt;target&gt;/ (and its non-excluded subfolders) of each
    /// folder whose record is enabled, ours, and lists target.</summary>
    internal static void Sync(string gameDir, string target)
    {
        var loader = LoaderConfig.Current.Loader;
        if (loader.DisableSubFolderLoad) return;

        var field = typeof(MelonLoader.Melons.MelonFolderHandler)
            .GetField("_modDirs", BindingFlags.NonPublic | BindingFlags.Static);
        if (field?.GetValue(null) is not List<string> modDirs)
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
        foreach (var dir in Directory.GetDirectories(modsDir))
        {
            if (IsExcluded(dir)) continue;
            if (!loader.DisableSubFolderManifest && !File.Exists(Path.Combine(dir, ModManagerConstants.RecordName))) continue;
            AddWithSubfolders(dir, wanted);

            var name = Path.GetFileName(dir);
            var build = Path.Combine(dir, "." + target);
            // The record decides, never the folder name: anything that isn't ours, or is disabled, loads no build.
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

    // MelonLoader loads every non-excluded folder beneath a mod folder too, with
    // no manifest required below the first level.
    private static void AddWithSubfolders(string dir, List<string> into)
    {
        into.Add(dir);
        foreach (var sub in Directory.GetDirectories(dir))
            if (!IsExcluded(sub) && !OtherScanNames.Contains(Path.GetFileName(sub))) AddWithSubfolders(sub, into);
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
