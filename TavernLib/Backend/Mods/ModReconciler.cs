using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MelonLoader.Utils;
using TavernLib.Backend.Server.Configs;

namespace TavernLib.Backend.Mods;

/// <summary>
/// Headless-server reconcile of Mods/ against a ModsList, run synchronously at boot so what's on disk when it
/// returns is what loads. Pulls only from TrustedRepos. Only server builds are installed, and only mods that
/// list the server target are this pass's to switch on or off; an unwanted one is disabled, never deleted.
/// "Wanted" is each listed mod plus its dependency closure: dependencies are never named in the list, so a
/// list-driven disable pass would otherwise switch off every dependency the previous boot installed.
/// </summary>
public static class ModReconciler
{
    private const string Target = ModManagerConstants.TargetServer;
    private const int MaxSeconds = 300; // nobody watches a headless boot

    // Runs from OnEarlyInitializeMelon, after MelonLoader has listed the Mods/ subfolders but before it loads them.
    public static void ReconcileModList(string modsListPathArg)
    {
        TavernLogger.Msg("mod reconcile: running before Mods/ is scanned (OnEarlyInitializeMelon).");
        try
        {
            // The manifest "scenes" name of the scene this server runs, from SceneIndexPatches' arguments.
            var scene = CommandLineArguments.Contains(TavernArgs.TutorialScene) ? "tutorial"
                : CommandLineArguments.Contains(TavernArgs.QuestScene) ? "quest"
                : "normal";
            // Relative to ModdingTavern/; Combine keeps a rooted path as it is.
            var path = Path.Combine(TavernDirectories.ModdingTavern, modsListPathArg);
            // A named file that's missing is a typo, not an empty list: reading on would create an empty
            // modlist and disable every managed mod.
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

    // scene: the one this server runs; a listed mod whose manifest rules it out is skipped, and disabled if installed.
    public static void Reconcile(string gameDir, ModsList modsList, TrustedRepos trustedRepos, string scene)
    {
        using var http = new NativeHttp();
        new Pass(gameDir, http, trustedRepos.EffectiveRepoUrls(), scene).Run(modsList);
    }

    private sealed class Pass(string gameDir, NativeHttp http, List<string> repos, string scene)
    {
        private readonly DateTime _deadline = DateTime.UtcNow.AddSeconds(MaxSeconds);
        private readonly ModRepoClient _client = new(http, repos);
        // Listed but left out, by id, with why, so the disable pass can say so rather than calling them unlisted.
        private readonly Dictionary<string, string> _skipped = new();
        private Dictionary<string, InstalledModInfo> _installed;
        private List<ModSummary> _index;
        // Verdicts for this pass, so a dependency or library shared by several listed mods is hashed once;
        // anything reinstalled is forgotten.
        private readonly Dictionary<string, bool?> _modVerified = new();
        private readonly Dictionary<string, bool?> _libVerified = new();

        // Fetched on first use: a boot with nothing to install never needs it.
        private List<ModSummary> Index => _index ??= FetchIndex();

        private string SceneReason => $"it doesn't work in the {scene} scene this server runs";

        private List<ModSummary> FetchIndex()
        {
            try
            {
                return _client.FetchIndexes();
            }
            catch (Exception e)
            {
                TavernLogger.Warn($"mod reconcile: couldn't fetch any repo index ({e.Message}); proceeding with whatever's already installed.");
                return [];
            }
        }

        // Only a record listing the server target runs here; a build installed only for pc in the same
        // folder is a player's, not ours.
        private bool RunsHere(string id) => _installed.TryGetValue(id, out var info) && info.Record.Lists(Target);

        public void Run(ModsList modsList)
        {
            var installed = ModInstaller.ScanMods(gameDir).Installed;
            _installed = installed.ToDictionary(m => m.Record.Id);
            var entries = (modsList.Mods ?? []).Select(ModsList.ParseEntry).ToList();
            var keepIds = new HashSet<string>();

            for (var i = 0; i < entries.Count; i++)
            {
                if (DateTime.UtcNow > _deadline)
                {
                    TavernLogger.Warn($"mod reconcile: exceeded its {MaxSeconds}s budget with {entries.Count - i} mod(s) left unprocessed; booting with whatever's already installed for them.");
                    // Anything not reached keeps what it has on disk, closure included, rather than being
                    // disabled as unwanted.
                    foreach (var (id, _) in entries.Skip(i))
                    {
                        if (_installed.TryGetValue(id, out var left) && !ModManifest.RunsIn(left.Record.Scenes, scene))
                            _skipped[id] = SceneReason;
                        else
                            AddInstalledClosure(id, keepIds);
                    }
                    break;
                }
                keepIds.UnionWith(ReconcileOne(entries[i]));
            }

            // Re-enable anything kept but disabled, such as a mod disabled by hand that's back in the list.
            foreach (var id in keepIds.Where(RunsHere))
            {
                try
                {
                    if (ModInstaller.SetEnabled(gameDir, id, true))
                        TavernLogger.Msg($"mod reconcile: re-enabled '{id}'.");
                }
                catch (Exception e)
                {
                    TavernLogger.Warn($"mod reconcile: couldn't re-enable '{id}' ({e.Message}); leaving it as it is.");
                }
            }

            foreach (var id in installed.Where(m => m.Enabled && m.Record.Lists(Target)).Select(m => m.Record.Id).Where(id => !keepIds.Contains(id)))
            {
                try
                {
                    if (ModInstaller.SetEnabled(gameDir, id, false))
                        TavernLogger.Msg($"mod reconcile: disabled '{id}' ("
                            + (_skipped.TryGetValue(id, out var why) ? why : "no longer in the configured mods list") + ").");
                }
                catch (Exception e)
                {
                    TavernLogger.Warn($"mod reconcile: couldn't disable '{id}' ({e.Message}); it stays enabled.");
                }
            }
        }

        // An installed mod's dependency closure, read off the records with no network: the steady state, and
        // the fallback when resolution fails, so a repo outage never disables a dependency. Only server
        // builds are walked; an id with none is added unwalked, for the caller's missing-dependency check.
        private void AddInstalledClosure(string modId, HashSet<string> into)
        {
            if (!into.Add(modId) || !RunsHere(modId)) return;
            foreach (var depId in _installed[modId].Record.Dependencies?.Keys ?? Enumerable.Empty<string>())
                AddInstalledClosure(depId, into);
        }

        private HashSet<string> InstalledClosure(string rootId)
        {
            var closure = new HashSet<string>();
            AddInstalledClosure(rootId, closure);
            return closure;
        }

        // The first damaged server build or pinned library in an entry's installed closure, described, or
        // null. Dependencies are checked here because nothing else ever re-checks them.
        private string FindDamage(string rootId)
        {
            foreach (var id in InstalledClosure(rootId).Where(RunsHere).OrderBy(x => x, StringComparer.Ordinal))
            {
                var record = _installed[id].Record;
                if (Memo(_modVerified, id, () => ModInstaller.VerifyTarget(gameDir, record, Target)) == false)
                    return $"'{id}' {record.Version} is damaged (its {Target} files no longer match what was installed)";
                foreach (var lib in record.InstalledFor(Target).Libraries ?? [])
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
                // Root last, so a failure partway leaves new dependencies beside the old root, which still loads.
                foreach (var m in deps) ModInstaller.InstallMod(gameDir, http, m, Target, _deadline);
                foreach (var lib in libs) ModInstaller.InstallLibraryDependency(gameDir, http, lib, _deadline);
                ModInstaller.InstallMod(gameDir, http, target, Target, _deadline);
            }
            finally
            {
                // Even after a partial failure, so later entries sharing these see what is on disk now.
                foreach (var m in deps.Append(target))
                {
                    _modVerified.Remove(m.Id);
                    if (ModRecord.Read(gameDir, m.Id, out var enabled) is { } record)
                        _installed[m.Id] = new InstalledModInfo { Record = record, Enabled = enabled };
                }
                foreach (var lib in libs) _libVerified.Remove(lib.Filename);
            }
        }

        // Which trusted repo to ask first. Once the index is in hand (always, for a fresh install), the one it
        // says hosts the mod, as install.py's install_mod_closure prefers; otherwise the one the installed
        // copy came from.
        private string PreferredRepo((string Id, string PinnedVersion) entry, InstalledModInfo existing)
        {
            if (existing != null && _index == null) return existing.Record.SourceRepo;
            int? major = entry.PinnedVersion != null && ModVersion.TryParse(entry.PinnedVersion, out var pinned) ? pinned.Major : null;
            return Index.Where(s => s.Id == entry.Id && (major == null || s.Major == major))
                .OrderByDescending(s => s.Highest)
                .FirstOrDefault()?.SourceRepo;
        }

        /// <summary>Brings one entry's server build to its target version and returns every id it accounts
        /// for, itself plus its closure. Only an install needs more than the target manifest; if anything
        /// fails, the on-disk closure is kept so an outage degrades to "keep what's installed".</summary>
        private HashSet<string> ReconcileOne((string Id, string PinnedVersion) entry)
        {
            var keep = new HashSet<string>();
            _installed.TryGetValue(entry.Id, out var existing);
            try
            {
                var preferRepo = PreferredRepo(entry, existing);
                var target = entry.PinnedVersion != null
                    ? _client.FetchManifest(entry.Id, entry.PinnedVersion, preferRepo)
                    : _client.FetchLatest(entry.Id, preferRepo);

                // Skipped, not refused: nobody is there to ask, and booting without the mod beats booting
                // something that can't run here. Keeping nothing lets the disable pass turn an installed copy off.
                if (target.ArtifactFor(Target) == null)
                {
                    TavernLogger.Warn($"mod reconcile: '{entry.Id}' {target.Version} has no {Target} build; skipped.");
                    _skipped[entry.Id] = $"it has no {Target} build";
                    return keep;
                }
                if (!ModManifest.RunsIn(target.Scenes, scene))
                {
                    TavernLogger.Warn($"mod reconcile: '{entry.Id}' {target.Version} works in the "
                        + $"{string.Join(", ", target.Scenes)} scene(s), not the {scene} scene this server runs; skipped.");
                    _skipped[entry.Id] = SceneReason;
                    return keep;
                }

                var fetched = new Dictionary<(string, string), ModManifest>();
                ModManifest Fetch(string id, string version, string sourceRepo) =>
                    fetched.TryGetValue((id, version), out var m) ? m : fetched[(id, version)] = _client.FetchManifest(id, version, sourceRepo);

                // Dependencies with no server build are pruned rather than installed into a process that would
                // throw loading them.
                List<ModManifest> ResolveDeps() => ModDependencyResolver.ResolveDependencies(target, Index, Fetch, Target);

                var needsInstall = !RunsHere(entry.Id) || existing.Record.Version != target.Version;
                List<ModManifest> deps = null;

                // Right version, wrong bytes: re-check the files map. A record without one verifies as null
                // and is left alone.
                if (!needsInstall)
                {
                    var damage = FindDamage(entry.Id);
                    // A dependency missing from disk may have been deleted, or may have no server build and so
                    // never have been installed; only resolving tells the two apart.
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
                keep.UnionWith(deps.Select(d => d.Id));
                return keep;
            }
            catch (Exception e)
            {
                // The installed record still knows its scenes, so an unreachable repo can't let back in a mod
                // this server's scene rules out.
                if (existing != null && !ModManifest.RunsIn(existing.Record.Scenes, scene))
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
