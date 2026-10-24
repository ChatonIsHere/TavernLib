using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MelonLoader.Utils;
using TavernLib.Backend.Server.Configs;

namespace TavernLib.Backend.Mods;

/// <summary>
/// Headless-server reconcile of Mods/ against a ModsList, run synchronously
/// from OnEarlyInitializeMelon so what's on disk when it returns is what loads.
/// Pulls only from trustedRepos, never from the modlist's informational
/// `repos`. An unwanted mod is disabled, never deleted. Only the server target
/// is installed, and only mods that list it are this pass's to switch on or off.
///
/// "Wanted" is each listed mod PLUS its dependency closure: dependencies are
/// never named in the list, so a list-driven disable pass would switch off on
/// the next boot every dependency the previous one installed.
/// </summary>
public static class ModReconciler
{
    private const string Target = ModManagerConstants.TargetServer;

    /// <summary>
    /// The headless boot entry point. Runs from OnEarlyInitializeMelon, after
    /// MelonLoader has listed the Mods/ subfolders but before LoadMelons(Mods);
    /// the caller then syncs that list with the disk (MelonModFolders).
    /// </summary>
    public static void ReconcileModList(string modsListPathArg, string scene)
    {
        TavernLogger.Msg("mod reconcile: running before Mods/ is scanned (OnEarlyInitializeMelon).");
        try
        {
            var path = Path.IsPathRooted(modsListPathArg)
                ? modsListPathArg
                : Path.Combine(TavernDirectories.ModdingTavern, modsListPathArg);

            // A named file that's missing is a typo, not an empty list: reading on
            // would create an empty modlist and disable every managed mod.
            if (!File.Exists(path))
            {
                TavernLogger.Error($"mod reconcile: modlist file '{path}' does not exist; skipping reconcile and booting with Mods/ unchanged. Fix the /modlist path or create the file.");
                return;
            }

            var modsList = new ModsListConfig(path);
            modsList.ReadFromFile();
            var trustedRepos = new TrustedReposConfig(TavernDirectories.ModRepos);
            trustedRepos.ReadFromFile();

            Reconcile(MelonEnvironment.GameRootDirectory, modsList.LastRead, trustedRepos.LastRead, scene);
        }
        catch (Exception e)
        {
            TavernLogger.Error($"mod reconcile: couldn't read '{modsListPathArg}' or reconcile failed ({e}); booting with Mods/ unchanged.");
        }
    }

    /// <param name="scene">The scene this server runs ("normal", "quest" or
    /// "tutorial"); a listed mod whose manifest says it doesn't work there is
    /// skipped, and disabled if installed. Null checks nothing.</param>
    public static void Reconcile(string gameDir, ModsList modsList, TrustedRepos trustedRepos, string scene = null)
    {
        using var http = new NativeHttp();
        new Pass(gameDir, http, trustedRepos.EffectiveRepoUrls(), scene).Run(modsList);
    }

    private sealed class Pass(string gameDir, NativeHttp http, List<string> repos, string scene)
    {
        private readonly DateTime _deadline = DateTime.UtcNow.AddSeconds(ModManagerConstants.ReconcileMaxSeconds);
        private readonly ModRepoClient _client = new(http);
        // Listed but left out, by id, with why, so the disable pass can say so
        // rather than calling them unlisted.
        private readonly Dictionary<string, string> _skipped = new();
        private Dictionary<string, InstalledModInfo> _installed;
        private List<ModSummary> _index;
        // Verdicts for this pass, so a dependency or library shared by several
        // listed mods is hashed once; anything reinstalled is forgotten.
        private readonly Dictionary<string, bool?> _modVerified = new();
        private readonly Dictionary<string, bool?> _libVerified = new();

        // Fetched on first use: a boot with nothing to install never needs it.
        private List<ModSummary> Index => _index ??= FetchIndex();

        private string SceneReason => $"it doesn't work in the {scene} scene this server runs";

        private List<ModSummary> FetchIndex()
        {
            try
            {
                return _client.FetchIndexes(repos);
            }
            catch (Exception e)
            {
                TavernLogger.Warn($"mod reconcile: couldn't fetch any repo index ({e.Message}); proceeding with whatever's already installed.");
                return new List<ModSummary>();
            }
        }

        /// <summary>A record that lists the server target: the only kind this
        /// server runs, and so the only kind reconcile turns on or off. A build
        /// installed only for pc in the same folder is a player's, not ours.</summary>
        private bool RunsHere(string id) => _installed.TryGetValue(id, out var info) && info.Record.Lists(Target);

        public void Run(ModsList modsList)
        {
            var installed = ModInstaller.ScanMods(gameDir).Installed;
            _installed = installed.ToDictionary(m => m.Record.Id);
            var desiredEntries = (modsList.Mods ?? new List<string>()).Select(ModsListEntry.Parse).ToList();
            var keepIds = new HashSet<string>();

            for (var i = 0; i < desiredEntries.Count; i++)
            {
                if (DateTime.UtcNow > _deadline)
                {
                    TavernLogger.Warn($"mod reconcile: exceeded its {ModManagerConstants.ReconcileMaxSeconds}s budget with {desiredEntries.Count - i} mod(s) left unprocessed; booting with whatever's already installed for them.");
                    // Anything not reached keeps what it has on disk, closure
                    // included, rather than being disabled as unwanted.
                    for (var j = i; j < desiredEntries.Count; j++)
                    {
                        var id = desiredEntries[j].Id;
                        if (_installed.TryGetValue(id, out var left) && !ModScenesField.RunsIn(left.Record.Scenes, scene))
                            _skipped[id] = SceneReason;
                        else
                            AddInstalledClosure(id, keepIds);
                    }
                    break;
                }
                keepIds.UnionWith(ReconcileOne(desiredEntries[i]));
            }

            // Re-enable anything kept but disabled, such as a mod disabled by
            // hand that is back in the list.
            foreach (var id in keepIds.Where(RunsHere))
            {
                try
                {
                    if (ModInstaller.EnableMod(gameDir, id))
                        TavernLogger.Msg($"mod reconcile: re-enabled '{id}'.");
                }
                catch (Exception e)
                {
                    TavernLogger.Warn($"mod reconcile: couldn't re-enable '{id}' ({e.Message}); leaving it as it is.");
                }
            }

            foreach (var info in installed)
            {
                if (keepIds.Contains(info.Record.Id)) continue;
                if (!info.Enabled || !info.Record.Lists(Target)) continue;
                try
                {
                    if (ModInstaller.DisableMod(gameDir, info.Record.Id))
                        TavernLogger.Msg($"mod reconcile: disabled '{info.Record.Id}' ("
                            + (_skipped.TryGetValue(info.Record.Id, out var why) ? why : "no longer in the configured mods list") + ").");
                }
                catch (Exception e)
                {
                    TavernLogger.Warn($"mod reconcile: couldn't disable '{info.Record.Id}' ({e.Message}); it stays enabled.");
                }
            }
        }

        /// <summary>An installed mod's dependency closure, read off the records
        /// on disk with no network: the steady state, and the fallback when
        /// resolution fails, so a repo outage never disables a dependency. Only
        /// server builds are walked; an id with none is added unwalked, for the
        /// caller's missing-dependency check. Visited-guarded against cycles in
        /// recorded data.</summary>
        private void AddInstalledClosure(string modId, HashSet<string> into)
        {
            if (!into.Add(modId)) return;
            if (!RunsHere(modId)) return;
            foreach (var depId in (_installed[modId].Record.Dependencies ?? new Dictionary<string, string>()).Keys)
                AddInstalledClosure(depId, into);
        }

        private HashSet<string> InstalledClosure(string rootId)
        {
            var closure = new HashSet<string>();
            AddInstalledClosure(rootId, closure);
            return closure;
        }

        /// <summary>The first damaged server build or pinned library in an
        /// entry's installed closure, described, or null. Dependencies are
        /// checked here because nothing else ever re-checks them.</summary>
        private string FindDamage(string rootId)
        {
            foreach (var id in InstalledClosure(rootId).Where(RunsHere).OrderBy(x => x, StringComparer.Ordinal))
            {
                var record = _installed[id].Record;
                if (Memo(_modVerified, id, () => ModInstaller.VerifyTarget(gameDir, record, Target)) == false)
                    return $"'{id}' {record.Version} is damaged (its {Target} files no longer match what was installed)";
                foreach (var lib in record.InstalledFor(Target).Libraries ?? new List<string>())
                    if (Memo(_libVerified, lib, () => ModInstaller.VerifyLibrary(gameDir, lib)) == false)
                        return $"library '{lib}' (pinned by '{id}') is damaged";
            }
            return null;
        }

        private static bool? Memo(Dictionary<string, bool?> memo, string key, Func<bool?> verify)
        {
            if (!memo.TryGetValue(key, out var verdict)) memo[key] = verdict = verify();
            return verdict;
        }

        private void InstallClosure(ModManifest target, List<ModManifest> deps, List<LibraryDependency> libs)
        {
            try
            {
                ModInstaller.InstallModClosure(gameDir, http, target, deps, libs, Target, _deadline);
            }
            finally
            {
                // Even after a partial failure, so later entries sharing these
                // see what is on disk now.
                foreach (var m in deps.Append(target))
                {
                    _modVerified.Remove(m.Id);
                    var record = ModRecord.Read(gameDir, m.Id, out var enabled);
                    if (record != null) _installed[m.Id] = new InstalledModInfo { Record = record, Enabled = enabled };
                }
                foreach (var lib in libs) _libVerified.Remove(lib.Filename);
            }
        }

        /// <summary>Which trusted repo to ask first for a listed mod. Once the
        /// index is in hand (always, for a fresh install), the repo it says hosts
        /// the mod, as install.py's install_mod_closure prefers; otherwise the
        /// repo the installed copy came from. ModRepoClient ignores a
        /// preference that isn't a trusted repo.</summary>
        private string PreferredRepo(ModsListEntry entry, InstalledModInfo existing)
        {
            if (existing != null && _index == null) return existing.Record.SourceRepo;
            int? major = entry.PinnedVersion != null && ModVersion.TryParse(entry.PinnedVersion, out var pinned) ? pinned.Major : null;
            return Index.Where(s => s.Id == entry.Id && (major == null || s.Major == major))
                .OrderByDescending(s => s.Highest)
                .FirstOrDefault()?.SourceRepo;
        }

        /// <summary>
        /// Brings one entry's server build to its target version and returns
        /// every id it accounts for, itself plus its closure. Only an install
        /// needs more than the target manifest; if anything fails, the on-disk
        /// closure is kept so an outage degrades to "keep what's installed".
        /// </summary>
        private HashSet<string> ReconcileOne(ModsListEntry entry)
        {
            var keep = new HashSet<string>();
            _installed.TryGetValue(entry.Id, out var existing);
            try
            {
                var preferRepo = PreferredRepo(entry, existing);
                var target = entry.PinnedVersion != null
                    ? _client.FetchManifest(repos, entry.Id, entry.PinnedVersion, preferRepo)
                    : _client.ResolveModById(repos, entry.Id, preferRepo);

                // Skipped, not refused: nobody is there to ask, and booting
                // without the mod beats booting something that can't run here.
                // Keeping nothing means the disable pass turns an installed copy off.
                if (target.ArtifactFor(Target) == null)
                {
                    TavernLogger.Warn($"mod reconcile: '{entry.Id}' {target.Version} has no {Target} build; skipped.");
                    _skipped[entry.Id] = $"it has no {Target} build";
                    return keep;
                }
                if (!ModScenesField.RunsIn(target.Scenes, scene))
                {
                    TavernLogger.Warn($"mod reconcile: '{entry.Id}' {target.Version} works in the "
                        + $"{string.Join(", ", target.Scenes)} scene(s), not the {scene} scene this server runs; skipped.");
                    _skipped[entry.Id] = SceneReason;
                    return keep;
                }

                var fetched = new Dictionary<(string Id, string Version), ModManifest>();
                ModManifest Fetch(string id, string version, string sourceRepo)
                {
                    var key = (id, version);
                    if (!fetched.TryGetValue(key, out var m))
                    {
                        m = _client.FetchManifest(repos, id, version, sourceRepo);
                        fetched[key] = m;
                    }
                    return m;
                }

                // Dependencies with no server build are pruned rather than
                // installed into a process that would throw loading them.
                List<ModManifest> ResolveDeps() => ModDependencyResolver.ResolveDependencies(
                    new List<ModManifest> { target }, Index, Fetch, Target);

                var needsInstall = !RunsHere(entry.Id) || existing.Record.Version != target.Version;
                List<ModManifest> deps = null;

                // Right version, wrong bytes: re-check the files map. A record
                // without one verifies as null and is left alone.
                if (!needsInstall)
                {
                    var damage = FindDamage(entry.Id);
                    // A dependency missing from disk may have been deleted, or may
                    // be one with no server build and so never installed; only
                    // resolving tells the two apart.
                    if (damage == null && InstalledClosure(entry.Id).Any(id => !RunsHere(id)))
                    {
                        deps = ResolveDeps();
                        var lost = deps.FirstOrDefault(d => !RunsHere(d.Id));
                        if (lost != null) damage = $"dependency '{lost.Id}' is missing";
                    }
                    if (damage != null)
                    {
                        TavernLogger.Warn($"mod reconcile: {damage}. Reinstalling '{entry.Id}' and its dependencies.");
                        needsInstall = true;
                    }
                }

                if (!needsInstall)
                {
                    // parity_required and scenes can change without a version bump.
                    if (ModInstaller.RefreshRecordMetadata(gameDir, target))
                        TavernLogger.Msg($"mod reconcile: refreshed '{entry.Id}' record metadata "
                                         + $"(parity_required now {target.ParityRequired}, scenes now "
                                         + (target.Scenes == null ? "all" : string.Join(", ", target.Scenes)) + ").");
                    AddInstalledClosure(entry.Id, keep);
                    return keep;
                }

                deps ??= ResolveDeps();
                var libs = ModInstaller.CollectLibraryDependencies(gameDir, deps.Prepend(target).ToList(), Target);
                InstallClosure(target, deps, libs);
                TavernLogger.Msg($"mod reconcile: installed '{entry.Id}' {target.Version} ({Target}).");

                keep.Add(target.Id);
                foreach (var dep in deps) keep.Add(dep.Id);
                return keep;
            }
            catch (Exception e)
            {
                // The installed record still knows its scenes, so an unreachable
                // repo can't let back in a mod this server's scene rules out.
                if (existing != null && !ModScenesField.RunsIn(existing.Record.Scenes, scene))
                {
                    _skipped[entry.Id] = SceneReason;
                    return keep;
                }
                if (RunsHere(entry.Id))
                    TavernLogger.Warn($"mod reconcile: couldn't resolve/update '{entry.Id}' ({e.Message}); keeping the installed {existing.Record.Version}.");
                else
                    TavernLogger.Warn($"mod reconcile: couldn't resolve/install '{entry.Id}' ({e.Message}); the server will boot without it.");
                AddInstalledClosure(entry.Id, keep);
                return keep;
            }
        }
    }
}
