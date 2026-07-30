using System;
using System.Collections.Generic;
using System.Linq;
using TavernLib.Backend.Server.Configs;

namespace TavernLib.Backend.Mods;

/// <summary>
/// Headless-server native installer: reconciles Mods/ against a ModsList's
/// desired mods, entirely in C#, no launcher required. Runs
/// synchronously inside Tavern.cs's OnEarlyInitializeMelon, before MelonLoader
/// scans Mods/ - whatever's on disk when this returns is what loads this
/// session.
///
/// Pulls only ever come from trustedRepos (the separately-maintained,
/// add_repo-gated allow-list) - never from modsList.Repos, which is an
/// informational "Author/Repo" identifier list, not a source of pull
/// authorization (see ModsList's own doc comment).
///
/// A mod on disk but no longer in the desired list is disabled, not deleted
/// (same manifest.disabled.json rename the manual toggle uses), so re-adding it
/// later needs no re-download. Actual deletion of long-disabled mods is a
/// separate, manual-only cleanup (see ModsCommandModule).
/// </summary>
public static class ModReconciler
{
    public static void Reconcile(string gameDir, ModsList modsList, TrustedRepos trustedRepos)
    {
        var deadline = DateTime.UtcNow.AddSeconds(ModManagerConstants.ReconcileMaxSeconds);
        var repos = trustedRepos.EffectiveRepoUrls();
        var client = new ModRepoClient();

        List<ModSummary> index;
        try
        {
            index = client.FetchIndexes(repos);
        }
        catch (Exception e)
        {
            TavernLogger.Warn($"mod reconcile: couldn't fetch any repo index ({e.Message}); proceeding with whatever's already installed.");
            index = new List<ModSummary>();
        }

        var installed = ModInstaller.ListInstalledModsWithState(gameDir);
        var installedById = installed.ToDictionary(m => m.Record.Id);
        var desiredEntries = (modsList.Mods ?? new List<string>()).Select(ModsListEntry.Parse).ToList();
        var desiredIds = new HashSet<string>(desiredEntries.Select(e => e.Id));

        foreach (var entry in desiredEntries)
        {
            if (DateTime.UtcNow > deadline)
            {
                var remaining = desiredEntries.Count - desiredEntries.IndexOf(entry);
                TavernLogger.Warn($"mod reconcile: exceeded its {ModManagerConstants.ReconcileMaxSeconds}s budget with {remaining} mod(s) left unprocessed; booting with whatever's already installed for them.");
                break;
            }
            ReconcileOne(gameDir, entry, repos, index, client, installedById, deadline);
        }

        foreach (var info in installed)
        {
            if (desiredIds.Contains(info.Record.Id)) continue;
            if (!info.Enabled) continue;
            if (ModInstaller.DisableMod(gameDir, info.Record.Id))
                TavernLogger.Msg($"mod reconcile: disabled '{info.Record.Id}' (no longer in the configured mods list).");
        }
    }

    private static void ReconcileOne(
        string gameDir, ModsListEntry entry, List<string> repos, List<ModSummary> index,
        ModRepoClient client, Dictionary<string, InstalledModInfo> installedById, DateTime deadline)
    {
        var existing = installedById.GetValueOrDefault(entry.Id);
        try
        {
            var target = entry.PinnedVersion != null
                ? client.FetchManifest(repos, entry.Id, entry.PinnedVersion)
                : client.ResolveModById(repos, entry.Id);

            var needsInstall = existing == null || existing.Record.Version != target.Version;
            if (needsInstall)
            {
                var fetched = new Dictionary<(string Id, string Version), ModManifest>();
                ModManifest Fetch(string id, string version, string sourceRepo)
                {
                    var key = (id, version);
                    if (!fetched.TryGetValue(key, out var m))
                    {
                        m = client.FetchManifest(repos, id, version, sourceRepo);
                        fetched[key] = m;
                    }
                    return m;
                }

                var deps = ModDependencyResolver.ResolveDependencies(new List<ModManifest> { target }, index, Fetch);
                var libs = ModInstaller.CollectLibraryDependencies(new[] { target }.Concat(deps));
                ModInstaller.InstallModClosure(gameDir, target, deps, libs, deadline);
                TavernLogger.Msg($"mod reconcile: installed '{entry.Id}' {target.Version}.");
            }
            else if (!existing.Enabled)
            {
                ModInstaller.EnableMod(gameDir, entry.Id);
                TavernLogger.Msg($"mod reconcile: re-enabled '{entry.Id}' {existing.Record.Version}.");
            }
        }
        catch (Exception e)
        {
            if (existing != null)
                TavernLogger.Warn($"mod reconcile: couldn't resolve/update '{entry.Id}' ({e.Message}); keeping the installed {existing.Record.Version}.");
            else
                TavernLogger.Warn($"mod reconcile: couldn't resolve/install '{entry.Id}' ({e.Message}); the server will boot without it.");
        }
    }
}
