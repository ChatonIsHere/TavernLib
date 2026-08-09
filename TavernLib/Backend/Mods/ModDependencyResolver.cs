using System;
using System.Collections.Generic;
using System.Linq;
using TavernLib;

namespace TavernLib.Backend.Mods;

public static class ModDependencyResolver
{
    /// <summary>
    /// Resolves the full dependency closure of one or more root mods, returning a
    /// flat, deduplicated list of ModManifests (the roots themselves not
    /// included - the caller already has them).
    ///
    /// Minimum-Required-Version-with-Major-Lock: for each id -> min_version, keep
    /// the versions the index lists in the required major that are &gt;= the
    /// running minimum, take the highest, and fetch that one.
    ///
    /// Three failure modes: a circular dependency, a chain deeper than
    /// MaxDependencyDepth, or a diamond conflict (two branches need the same id
    /// at incompatible majors) - each throws a specific, actionable message.
    ///
    /// <paramref name="side"/> is the kind of process this closure is being
    /// installed into (ModManagerConstants.SideClient/SideServer), mandatory with
    /// no default. A dependency that can't run on that side is dropped, along
    /// with everything only it needed. That is a derivation, not a guess:
    /// server_side false means the mod cannot load in a server process at all, so
    /// nothing running in a server process can hard-depend on it, and "M depends
    /// on D" where D is client-only has exactly one coherent reading - M's client
    /// half needs D. Must stay in lockstep with modmanager's resolve_dependencies:
    /// if the launcher prunes a dependency here and this doesn't, the crash comes
    /// back on headless hosts only.
    /// </summary>
    public static List<ModManifest> ResolveDependencies(
        IReadOnlyList<ModManifest> roots,
        IReadOnlyList<ModSummary> summaryIndex,
        Func<string, string, string, ModManifest> fetchManifest,
        string side)
    {
        if (side != ModManagerConstants.SideClient && side != ModManagerConstants.SideServer)
            throw new ModManagerException(
                $"ResolveDependencies needs side '{ModManagerConstants.SideClient}' or '{ModManagerConstants.SideServer}', got '{side}'.");

        // Read off the per-version MANIFEST, never a ModSummary: the manifest
        // requires both fields, while a summary defaults them to false, so a
        // hand-rolled third-party index omitting them would prune on both sides.
        bool RunsOnThisSide(ModManifest m) =>
            side == ModManagerConstants.SideClient ? m.ClientSide : m.ServerSide;

        var summ = new Dictionary<(string Id, int Major), ModSummary>();
        foreach (var s in summaryIndex) summ[(s.Id, s.Major)] = s;

        var rootIds = new HashSet<string>(roots.Select(r => r.Id));
        var chosen = new Dictionary<string, ModManifest>();
        var reqMajor = new Dictionary<string, int>();
        var reqMin = new Dictionary<string, ModVersion>();
        var reqBy = new Dictionary<string, (string Requirer, string Constraint)>();
        var pruned = new HashSet<string>();   // wrong-side ids, so each is fetched at most once

        void Visit(ModManifest mod, List<string> chain)
        {
            if (chain.Count > ModManagerConstants.MaxDependencyDepth)
                throw new ModManagerException(
                    $"Dependency chain too deep (> {ModManagerConstants.MaxDependencyDepth}): {string.Join(" -> ", chain)}. This is almost certainly a mistake in the mods' dependency data.");

            foreach (var kv in (mod.Dependencies ?? new Dictionary<string, string>()).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var depId = kv.Key;
                var minV = kv.Value;
                if (chain.Contains(depId))
                    throw new ModManagerException($"Circular dependency: {string.Join(" -> ", chain.Append(depId))}.");
                AddRequirement(depId, minV, mod.Name ?? mod.Id, chain);
            }
        }

        void AddRequirement(string depId, string minV, string requirer, List<string> chain)
        {
            if (pruned.Contains(depId))
            {
                // Already established this one can't run here. Still reported, so a
                // second requirer of the same wrong-side dep isn't invisible, but
                // not re-fetched and not re-walked.
                TavernLogger.Warn($"{requirer} depends on '{depId}', which doesn't run on the {side}; skipped.");
                return;
            }

            ModVersion mv;
            try
            {
                mv = ModVersion.Parse(minV);
            }
            catch (ModManagerException e)
            {
                throw new ModManagerException($"{requirer} requires {depId} at an invalid version '{minV}': {e.Message}");
            }
            var major = mv.Major;

            if (reqMajor.TryGetValue(depId, out var existingMajor) && existingMajor != major)
            {
                var (prevName, prevC) = reqBy[depId];
                throw new ModManagerException(
                    $"Dependency conflict on '{depId}': {prevName} needs {prevC} (major {existingMajor}) but {requirer} needs {minV} (major {major}). These majors can't be satisfied together.");
            }

            var newMin = reqMin.TryGetValue(depId, out var curMin) && curMin > mv ? curMin : mv;

            summ.TryGetValue((depId, major), out var summary);
            var available = summary?.Versions ?? new List<string>();
            var candidates = available.Where(v => ModVersion.Parse(v) >= newMin).ToList();
            if (candidates.Count == 0)
            {
                var have = available.Count > 0 ? string.Join(", ", available.OrderBy(v => v)) : "none";
                throw new ModManagerException(
                    $"{requirer} needs '{depId}' >= {newMin} (major {major}), but no such version is available (available: {have}). Is the right repository added?");
            }
            var pickVersion = candidates.OrderByDescending(ModVersion.Parse).First();

            void Record()
            {
                reqMajor[depId] = major;
                reqMin[depId] = newMin;
                if (!reqBy.ContainsKey(depId)) reqBy[depId] = (requirer, minV);
            }

            var prevChosen = chosen.GetValueOrDefault(depId);
            if (prevChosen != null && prevChosen.Version == pickVersion)
            {
                Record();       // already selected at this exact version
                return;
            }

            var manifest = fetchManifest(depId, pickVersion, summary.SourceRepo);
            if (!RunsOnThisSide(manifest))
            {
                // Pruned BEFORE its constraints are recorded, so a mod we aren't
                // installing can't go on to raise a diamond conflict against a
                // later requirer, and its own dependencies are never walked -
                // nothing reached only through it is needed either.
                pruned.Add(depId);
                TavernLogger.Warn($"{requirer} depends on '{depId}' {pickVersion}, which doesn't run on the {side}; skipped, along with anything only it needed.");
                return;
            }

            Record();
            chosen[depId] = manifest;
            Visit(manifest, chain.Append(depId).ToList());
        }

        foreach (var root in roots)
            Visit(root, new List<string> { root.Id });

        return chosen.Where(kv => !rootIds.Contains(kv.Key)).Select(kv => kv.Value).ToList();
    }
}
