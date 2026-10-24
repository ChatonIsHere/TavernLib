using System;
using System.Collections.Generic;
using System.Linq;
using Alta.Console;
using Alta.Console.Commands;
using MelonLoader.Utils;
using TavernLib.Backend.Mods;
using TavernLib.Backend.Server.Configs;

namespace TavernLib.ModulePorts;

/// <summary>
/// `modmanager list` and `modmanager addrepo`. Not called "mods": the game's
/// console already owns that module name for its own content mods.
/// </summary>
[Module("modmanager", "Community mod management console commands")]
public static class ModsCommandModule
{
    private static TrustedReposConfig LoadTrustedRepos()
    {
        var config = new TrustedReposConfig(TavernDirectories.ModRepos);
        config.ReadFromFile();
        config.LastRead.Repos ??= new Dictionary<string, string>();   // "repos": null in a hand-edited file
        return config;
    }

    [ServerOnly]
    [Command("list", "Lists every community mod in Mods/ and its enabled/disabled state, including untracked ones this manager didn't install.")]
    private static string List()
    {
        var gameDir = MelonEnvironment.GameRootDirectory;
        var (installed, untracked) = ModInstaller.ScanMods(gameDir);
        if (installed.Count == 0 && untracked.Count == 0) return "No community mods installed.";

        // Enabled mods only: a disabled one isn't loaded, so it's listed
        // without a verdict rather than hashed for nothing.
        var damaged = installed
            .Where(m => m.Enabled && ModInstaller.VerifyModFiles(gameDir, m.Record) == false)
            .Select(m => m.Record.Id)
            .ToHashSet();

        // Sorted, and managed before untracked: this merges two directory scans
        // whose order is filesystem-dependent, so without it the same Mods/
        // folder can list differently between two runs.
        var lines = installed
            .OrderBy(m => m.Record.Id, StringComparer.OrdinalIgnoreCase)
            .Select(m => $"{m.Record.Id} {m.Record.Version} - {State(m.Enabled)}"
                         + (damaged.Contains(m.Record.Id) ? " [DAMAGED]" : "")
                         + (m.Record.Scenes != null ? $" [scenes: {string.Join(", ", m.Record.Scenes)}]" : ""))
            .Concat(untracked
                .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
                .Select(u => $"{u.Name} - {State(u.Enabled)} [untracked {u.Kind}]"));
        var listing = string.Join("\n", lines);
        if (damaged.Count == 0) return listing;
        // Said explicitly because the state is otherwise alarming and the fix
        // is already scheduled: reconcile re-checks the files map at boot and
        // reinstalls anything that doesn't match.
        return listing + $"\n\n{damaged.Count} mod(s) no longer match the files that were installed "
                       + "(a cut-short write, or antivirus removing a DLL). They'll be reinstalled "
                       + "automatically on the next server restart.";
    }

    private static string State(bool enabled) => enabled ? "enabled" : "disabled";

    [ServerOnly]
    [Command("addrepo", "Registers a new pullable mod repo by its raw-content base URL (e.g. https://raw.githubusercontent.com/user/repo/main). Validates it serves a real mod index before adding it - this is the ONLY way a repo becomes pullable; naming it in a modlist's `repos` field is not enough.")]
    private static string AddRepo(string url)
    {
        url = ModManagerConstants.NormalizeUrl(url);
        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            return "A repository URL must start with http:// or https://.";

        var config = LoadTrustedRepos();
        var shorthand = ModManagerConstants.RepoShorthand(url);
        if (shorthand == ModManagerConstants.RepoShorthand(ModManagerConstants.DefaultRepo) ||
            config.LastRead.Repos.Values.Any(v => ModManagerConstants.NormalizeUrl(v) == url))
            return "That repository is already registered.";
        if (config.LastRead.Repos.TryGetValue(shorthand, out var existingUrl) && ModManagerConstants.NormalizeUrl(existingUrl) != url)
            return $"'{shorthand}' is already registered pointing at a different URL ({existingUrl}); remove it from mod_repos.json first if you want to repoint it.";

        try
        {
            ModRepoClient.ValidateRepoIndex(url);
        }
        catch (ModManagerException e)
        {
            return $"Couldn't add repo: {e.Message}";
        }

        config.LastRead.Repos[shorthand] = url;
        config.WriteToFile();
        return $"Added '{shorthand}' ({url}) as a trusted mod source.";
    }
}
