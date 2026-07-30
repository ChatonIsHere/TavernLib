using System.Linq;
using Alta.Console;
using Alta.Console.Commands;
using MelonLoader.Utils;
using TavernLib.Backend.Mods;
using TavernLib.Backend.Server.Configs;

namespace TavernLib.ModulePorts;

/// <summary>
/// Community mod management console commands: manual cleanup of mods this
/// server has disabled (dropped from its mods list by reconcile, or disabled
/// by hand) but never deleted, plus the only way to register a new pullable
/// repo - `addrepo`, gated by the same live fetch-and-validate check
/// modmanager.py's add_repo uses.
///
/// The module is deliberately NOT called "mods": the game's own console
/// already owns that name (`mods add`, `mods refresh`, `mods remove`, ... -
/// its native content/asset-mod schema, unrelated to community .dll mods).
/// Registering a second module under the same name would shadow or clash with
/// it. These commands are all `modmanager <command>`.
/// </summary>
[Module("modmanager", "Community mod management console commands")]
public static class ModsCommandModule
{
    private static TrustedReposConfig LoadTrustedRepos()
    {
        var config = new TrustedReposConfig(TavernDirectories.ModRepos);
        config.ReadFromFile();
        return config;
    }

    [ServerOnly]
    [Command("cleanup", "Deletes every currently-disabled community mod and any now-orphaned UserLibs library. Never touches an enabled mod.")]
    private static string Cleanup()
    {
        var gameDir = MelonEnvironment.GameRootDirectory;
        var installed = ModInstaller.ListInstalledModsWithState(gameDir);
        var disabledIds = installed.Where(m => !m.Enabled).Select(m => m.Record.Id).ToList();

        // The reference-count set has to SHRINK as mods go, not stay fixed at
        // the pre-cleanup snapshot: with two disabled mods pinning the same
        // library, a fixed snapshot has each one still seeing the other as
        // needing it, so the library survives both and is never collected.
        var remaining = installed.Select(m => m.Record).ToList();
        var removedCount = 0;
        foreach (var id in disabledIds)
        {
            remaining.RemoveAll(r => r.Id == id);
            if (ModInstaller.UninstallMod(gameDir, id, remaining))
                removedCount++;
        }

        return disabledIds.Count == 0
            ? "No disabled mods to clean up."
            : $"Cleaned up {removedCount}/{disabledIds.Count} disabled mod(s): {string.Join(", ", disabledIds)}.";
    }

    [ServerOnly]
    [Command("list", "Lists every installed community mod and its enabled/disabled state.")]
    private static string List()
    {
        var installed = ModInstaller.ListInstalledModsWithState(MelonEnvironment.GameRootDirectory);
        if (installed.Count == 0) return "No community mods installed.";
        return string.Join("\n", installed.Select(m => $"{m.Record.Id} {m.Record.Version} - {(m.Enabled ? "enabled" : "disabled")}"));
    }

    [ServerOnly]
    [Command("addrepo", "Registers a new pullable mod repo by its raw-content base URL (e.g. https://raw.githubusercontent.com/user/repo/main). Validates it serves a real mod index before adding it - this is the ONLY way a repo becomes pullable; naming it in a modlist's `repos` field is not enough.")]
    private static string AddRepo(string url)
    {
        url = url.TrimEnd('/');
        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            return "A repository URL must start with http:// or https://.";

        var config = LoadTrustedRepos();
        var shorthand = ModManagerConstants.RepoShorthand(url);
        if (shorthand == ModManagerConstants.RepoShorthand(ModManagerConstants.DefaultRepo) ||
            config.LastRead.Repos.Values.Any(v => v.TrimEnd('/') == url))
            return "That repository is already registered.";
        if (config.LastRead.Repos.TryGetValue(shorthand, out var existingUrl) && existingUrl.TrimEnd('/') != url)
            return $"'{shorthand}' is already registered pointing at a different URL ({existingUrl}); remove it first if you want to repoint it.";

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

    [ServerOnly]
    [Command("removerepo", "Removes a repo (by its 'Author/Repo' shorthand or full URL) from the trusted sources this server pulls from. The default Modding Tavern repo can't be removed.")]
    private static string RemoveRepo(string reference)
    {
        if (ModManagerConstants.RepoShorthand(reference) == ModManagerConstants.RepoShorthand(ModManagerConstants.DefaultRepo))
            return "The default Modding Tavern repository can't be removed.";

        var config = LoadTrustedRepos();
        var removed = config.LastRead.Repos.Keys
            .Where(k => k == reference || config.LastRead.Repos[k].TrimEnd('/') == reference.TrimEnd('/'))
            .ToList();
        if (removed.Count == 0) return $"No registered repo matches '{reference}'.";

        foreach (var key in removed) config.LastRead.Repos.Remove(key);
        config.WriteToFile();
        return $"Removed {string.Join(", ", removed)}.";
    }

    [ServerOnly]
    [Command("listrepos", "Lists every repo this server is currently allowed to pull mods from.")]
    private static string ListRepos()
    {
        var config = LoadTrustedRepos();
        var lines = config.LastRead.EffectiveRepoUrls()
            .Select(url => $"{ModManagerConstants.RepoShorthand(url)} ({url})");
        return string.Join("\n", lines);
    }
}
