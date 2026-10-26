using System;
using System.Linq;
using Alta.Console;
using Alta.Console.Commands;
using MelonLoader.Utils;
using TavernLib.Backend.Mods;
using TavernLib.Backend.Server.Configs;
using static TavernLib.Backend.Mods.ModManagerConstants;

namespace TavernLib.ModulePorts;

// Not "mods": the game's console already owns that module name for its own content mods.
[Module("modmanager", "Community mod management console commands")]
public static class ModsCommandModule
{
    [ServerOnly]
    [Command("list", "Lists every community mod in Mods/ and its enabled/disabled state, including untracked ones this manager didn't install.")]
    private static string List() => ListFor(MelonEnvironment.GameRootDirectory);

    internal static string ListFor(string gameDir)
    {
        var (installed, untracked) = ModInstaller.ScanMods(gameDir);
        if (installed.Count == 0 && untracked.Count == 0) return "No community mods installed.";

        var damaged = installed
            .Where(m => m.Enabled && ModInstaller.VerifyTarget(gameDir, m.Record, TargetServer) == false)
            .Select(m => m.Record.Id)
            .ToHashSet();

        var lines = installed
            .OrderBy(m => m.Record.Id, StringComparer.OrdinalIgnoreCase)
            .Select(m => $"{m.Record.Id} {m.Record.Version} - {State(m.Enabled)} [installed: {Targets(m.Record)}]"
                         + (damaged.Contains(m.Record.Id) ? " [DAMAGED]" : "")
                         + (m.Record.Scenes != null ? $" [scenes: {string.Join(", ", m.Record.Scenes)}]" : ""))
            .Concat(untracked
                .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
                .Select(u => $"{u.Name} - {State(u.Enabled)} [untracked {u.Kind}]"));
        var listing = string.Join("\n", lines);
        if (damaged.Count == 0) return listing;
        return listing + $"\n\n{damaged.Count} mod(s) no longer match the server files that were installed "
                       + "(a cut-short write, or antivirus removing a DLL). They'll be reinstalled "
                       + "automatically on the next server restart.";
    }

    private static string State(bool enabled) => enabled ? "enabled" : "disabled";

    private static string Targets(ModRecord record) =>
        record.Installed is { Count: > 0 } installed ? string.Join(", ", installed.Keys.OrderBy(t => t, StringComparer.Ordinal)) : "nothing";

    [ServerOnly]
    [Command("addrepo", "Registers a new pullable mod repo by its raw-content base URL (e.g. https://raw.githubusercontent.com/user/repo/main). Validates it serves a real mod index before adding it - this is the ONLY way a repo becomes pullable; naming it in a modlist's `repos` field is not enough.")]
    private static string AddRepo(string url)
    {
        url = NormalizeUrl(url);
        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            return "A repository URL must start with http:// or https://.";

        var config = new TrustedReposConfig(TavernDirectories.ModRepos);
        config.ReadFromFile();
        var repos = config.LastRead.Repos ??= new(); // "repos": null in a hand-edited file
        var shorthand = RepoShorthand(url);
        if (shorthand == RepoShorthand(DefaultRepo) || repos.Values.Any(v => NormalizeUrl(v) == url))
            return "That repository is already registered.";
        if (repos.TryGetValue(shorthand, out var existingUrl))
            return $"'{shorthand}' is already registered pointing at a different URL ({existingUrl}); remove it from mod_repos.json first if you want to repoint it.";

        try
        {
            ModRepoClient.ValidateRepoIndex(url);
        }
        catch (ModManagerException e)
        {
            return $"Couldn't add repo: {e.Message}";
        }

        repos[shorthand] = url;
        config.WriteToFile();
        return $"Added '{shorthand}' ({url}) as a trusted mod source.";
    }

    // Twin of repos.py's _repo_shorthand.
    private static string RepoShorthand(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        var parts = path.Trim('/').Split('/');
        return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : url;
    }
}
