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
/// A mod on disk but no longer wanted is disabled, not deleted (same
/// manifest.disabled.json rename the manual toggle uses), so re-adding it later
/// needs no re-download. Actual deletion of long-disabled mods is a separate,
/// manual-only cleanup (see ModsCommandModule).
///
/// "Wanted" is the desired list PLUS every mod in its dependency closure. A
/// dependency is never named in the mods list - it arrives because something
/// else needs it - so the disable pass has to be driven by the closure, not the
/// list, or the second boot would disable every dependency the first boot
/// installed and break the mod that pulled it in.
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

        // Everything that must stay loaded: each desired id PLUS its whole
        // dependency closure. A dependency is never named in the mods list (it
        // arrives because something else needs it), so keying the disable pass
        // off the mods list alone would disable every dependency on the next
        // boot and silently break the mod that pulled it in.
        var keepIds = new HashSet<string>();

        for (var i = 0; i < desiredEntries.Count; i++)
        {
            var entry = desiredEntries[i];
            if (DateTime.UtcNow > deadline)
            {
                TavernLogger.Warn($"mod reconcile: exceeded its {ModManagerConstants.ReconcileMaxSeconds}s budget with {desiredEntries.Count - i} mod(s) left unprocessed; booting with whatever's already installed for them.");
                // Anything not reached this pass keeps whatever it has on disk,
                // closure included, rather than being disabled as unwanted.
                for (var j = i; j < desiredEntries.Count; j++)
                    AddInstalledClosure(desiredEntries[j].Id, installedById, keepIds);
                break;
            }
            keepIds.UnionWith(ReconcileOne(gameDir, entry, repos, index, client, installedById, deadline));
        }

        // Re-enable anything in the keep set that's on disk but disabled, such
        // as a mod an operator disabled by hand and has since put back in the
        // list. A no-op for anything already enabled or not installed at all.
        foreach (var id in keepIds)
        {
            if (ModInstaller.EnableMod(gameDir, id))
                TavernLogger.Msg($"mod reconcile: re-enabled '{id}'.");
        }

        foreach (var info in installed)
        {
            if (keepIds.Contains(info.Record.Id)) continue;
            if (!info.Enabled) continue;
            if (ModInstaller.DisableMod(gameDir, info.Record.Id))
                TavernLogger.Msg($"mod reconcile: disabled '{info.Record.Id}' (no longer in the configured mods list).");
        }
    }

    /// <summary>
    /// The dependency closure of an installed mod, read straight off the
    /// records already on disk - each record carries the manifest's own
    /// `dependencies` verbatim, so this needs no network at all. Used for the
    /// steady state (root already at its target version, nothing to fetch) and
    /// as the offline fallback when resolution fails, so a repo outage can
    /// never cause a dependency to be disabled as unwanted. Visited-guarded, so
    /// a cycle in the recorded data terminates instead of recursing forever.
    /// </summary>
    private static void AddInstalledClosure(string modId, Dictionary<string, InstalledModInfo> installedById, HashSet<string> into)
    {
        if (!into.Add(modId)) return;
        if (!installedById.TryGetValue(modId, out var info)) return;
        foreach (var depId in (info.Record.Dependencies ?? new Dictionary<string, string>()).Keys)
            AddInstalledClosure(depId, installedById, into);
    }

    /// <summary>
    /// Brings one desired entry to its target version, and returns every id
    /// that entry accounts for - itself plus its whole dependency closure - so
    /// the caller's disable pass knows a dependency is wanted even though it's
    /// never named in the mods list.
    ///
    /// Only the install path needs the network: in the steady state (already at
    /// the target version) the closure is read off the records on disk, and if
    /// resolution fails entirely the on-disk closure is still returned, so a
    /// repo outage degrades to "keep what's installed" rather than disabling a
    /// working mod set.
    /// </summary>
    private static HashSet<string> ReconcileOne(
        string gameDir, ModsListEntry entry, List<string> repos, List<ModSummary> index,
        ModRepoClient client, Dictionary<string, InstalledModInfo> installedById, DateTime deadline)
    {
        var keep = new HashSet<string>();
        var existing = installedById.GetValueOrDefault(entry.Id);
        try
        {
            var target = entry.PinnedVersion != null
                ? client.FetchManifest(repos, entry.Id, entry.PinnedVersion)
                : client.ResolveModById(repos, entry.Id);

            var needsInstall = existing == null || existing.Record.Version != target.Version;
            if (!needsInstall)
            {
                // The manifest was fetched to work out the target version, so
                // catching the record up on metadata that can change without the
                // version changing costs nothing extra. parity_required is why
                // this is here: a mod published before that field existed, or
                // later relaxed, would otherwise be enforced on joining clients
                // forever on the strength of a stale record.
                if (ModInstaller.RefreshRecordMetadata(gameDir, target))
                    TavernLogger.Msg($"mod reconcile: refreshed '{entry.Id}' record metadata "
                                     + $"(parity_required now {target.ParityRequired}).");

                // Already at the target version, so its dependencies are already
                // on disk and their records name the whole closure - no fetching.
                AddInstalledClosure(entry.Id, installedById, keep);
                return keep;
            }

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

            keep.Add(target.Id);
            foreach (var dep in deps) keep.Add(dep.Id);
            return keep;
        }
        catch (Exception e)
        {
            if (existing != null)
                TavernLogger.Warn($"mod reconcile: couldn't resolve/update '{entry.Id}' ({e.Message}); keeping the installed {existing.Record.Version}.");
            else
                TavernLogger.Warn($"mod reconcile: couldn't resolve/install '{entry.Id}' ({e.Message}); the server will boot without it.");
            // Keep whatever of this entry's closure is already on disk; an
            // unreachable repo must not turn into a disabled mod set.
            AddInstalledClosure(entry.Id, installedById, keep);
            return keep;
        }
    }
}
