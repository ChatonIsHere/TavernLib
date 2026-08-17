using System;
using System.IO;
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
/// `list` covers untracked mods too - anything in Mods/ that MelonLoader loads
/// but this manager didn't install - so a headless operator can see the same
/// set a launcher-run server shows in its Mod Manager table, tagged rather than
/// mixed in. `enable`/`disable` act on those and only those, for the reason
/// spelled out on Toggle. There is deliberately no way to uninstall one from
/// here: the launcher won't either, on the same grounds.
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
    [Command("cleanup", "Deletes every currently-disabled community mod and any now-orphaned UserLibs library. Never touches an enabled mod, or any untracked one.")]
    private static string Cleanup()
    {
        // Untracked mods are deliberately out of scope, disabled or not: we
        // didn't install them, so we don't know what they dropped in UserLibs/
        // and can't promise a clean removal. Deleting an operator's own file
        // because a housekeeping command swept past it is not a trade worth
        // making - `modmanager list` surfaces them, and removal stays manual.
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
    [Command("list", "Lists every community mod in Mods/ and its enabled/disabled state, including untracked ones this manager didn't install.")]
    private static string List()
    {
        var gameDir = MelonEnvironment.GameRootDirectory;
        var installed = ModInstaller.ListInstalledModsWithState(gameDir);
        var untracked = ModInstaller.ListUntrackedMods(gameDir);
        if (installed.Count == 0 && untracked.Count == 0) return "No community mods installed.";

        // Re-hashed here rather than read off a cached answer: this is an
        // operator asking, once, on demand - the same gesture the launcher's
        // Refresh button makes, and the only moment a headless server has to
        // report damage between restarts. Everything else that cares (reconcile)
        // asks at boot.
        var damaged = installed
            .Where(m => ModInstaller.VerifyModFiles(gameDir, m.Record.Id) == false)
            .Select(m => m.Record.Id)
            .ToHashSet();

        // Sorted, and managed before untracked: this merges two directory scans
        // whose order is filesystem-dependent, so without it the same Mods/
        // folder can list differently between two runs.
        var lines = installed
            .OrderBy(m => m.Record.Id, StringComparer.OrdinalIgnoreCase)
            .Select(m => $"{m.Record.Id} {m.Record.Version} - {State(m.Enabled)}"
                         + (damaged.Contains(m.Record.Id) ? " [DAMAGED]" : ""))
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
    [Command("disable", "Disables an untracked mod by name (as shown by `modmanager list`) without deleting it. Takes effect on the next restart.")]
    private static string Disable(string name) => Toggle(name, disable: true);

    [ServerOnly]
    [Command("enable", "Re-enables an untracked mod previously disabled with `modmanager disable`. Takes effect on the next restart.")]
    private static string Enable(string name) => Toggle(name, disable: false);

    /// <summary>
    /// The shared body of enable/disable. Untracked mods ONLY, and the refusal
    /// on a managed one is the point rather than an omission: reconcile re-enables
    /// everything in its keep set on every boot (see ModReconciler), so disabling
    /// a mod that's in the configured mods list would be silently undone at the
    /// next restart. Untracked mods are the ones reconcile never touches, which
    /// is exactly why a hand-toggle of one sticks and is worth offering.
    /// </summary>
    private static string Toggle(string name, bool disable)
    {
        var gameDir = MelonEnvironment.GameRootDirectory;
        var verb = disable ? "disable" : "enable";

        if (ModInstaller.ListInstalledModsWithState(gameDir)
            .Any(m => string.Equals(m.Record.Id, name, StringComparison.OrdinalIgnoreCase)))
            return $"'{name}' is a managed mod, controlled by this server's configured mods "
                   + $"list - add or remove it there instead. Reconcile would undo a hand-{verb} "
                   + "on the next restart.";

        var match = ModInstaller.ListUntrackedMods(gameDir)
            .FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            return $"Nothing in Mods/ is named '{name}'. Run `modmanager list` to see what's there.";
        if (match.Enabled != disable)
            return $"'{match.Name}' is already {State(!disable)}.";

        try
        {
            var ok = match.Kind == UntrackedMod.KindFolder
                ? (disable ? ModInstaller.DisableMod(gameDir, match.Name) : ModInstaller.EnableMod(gameDir, match.Name))
                : (disable ? ModInstaller.DisableUntrackedDll(gameDir, match.Name) : ModInstaller.EnableUntrackedDll(gameDir, match.Name));
            if (!ok) return $"Couldn't {verb} '{match.Name}' - it changed on disk just now; run `modmanager list` again.";
        }
        catch (ModManagerException e)
        {
            return $"Couldn't {verb} '{match.Name}': {e.Message}";
        }
        catch (IOException e)
        {
            return $"Couldn't {verb} '{match.Name}': {e.Message}";
        }

        // MelonLoader scanned Mods/ long before any console command could run, so
        // whatever this just renamed is either already loaded or already skipped
        // for this session. Saying so beats an operator assuming it took hold.
        return $"'{match.Name}' {State(!disable)}. Its files stay on disk; "
               + "the change takes effect on the next server restart.";
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
