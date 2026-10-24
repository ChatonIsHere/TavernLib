using System;
using System.Collections.Generic;
using System.Linq;
using TavernLib;

namespace TavernLib.Backend.Mods;

public static class ModDependencyResolver
{
    /// <summary>
    /// Twin of resolve.py's resolve_dependencies; the verdicts must match. The
    /// closure of the roots (roots excluded) by minimum-version-with-major-lock:
    /// within the required major, the highest indexed version at or above the
    /// running minimum. Cycles, chains deeper than MaxDependencyDepth and
    /// cross-major demands throw. A dependency with no artifact for
    /// <paramref name="target"/> is pruned with everything only it needed: "M
    /// depends on pc-only D" can only mean M's pc build needs D.
    /// </summary>
    public static List<ModManifest> ResolveDependencies(
        IReadOnlyList<ModManifest> roots,
        IReadOnlyList<ModSummary> summaryIndex,
        Func<string, string, string, ModManifest> fetchManifest,
        string target)
    {
        if (!ModManagerConstants.KnownTargets.Contains(target))
            throw new ModManagerException(
                $"ResolveDependencies needs one of the targets {string.Join(", ", ModManagerConstants.KnownTargets)}, got '{target}'.");

        // Read off the per-version MANIFEST, never the index's targets: only
        // the manifest says which artifacts this build can actually install.
        bool BuildsForTarget(ModManifest m) => m.ArtifactFor(target) != null;

        var summ = new Dictionary<(string Id, int Major), ModSummary>();
        foreach (var s in summaryIndex) summ[(s.Id, s.Major)] = s;

        var rootIds = new HashSet<string>(roots.Select(r => r.Id));
        var chosen = new Dictionary<string, ModManifest>();
        var reqMin = new Dictionary<string, ModVersion>();   // the required major is reqMin[id].Major
        var reqBy = new Dictionary<string, (string Requirer, string Constraint)>();
        var pruned = new HashSet<string>();   // ids with no target build, so each is fetched at most once

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
                // Already pruned: reported again so this requirer isn't invisible, but not
                // re-fetched or re-walked.
                TavernLogger.Warn($"{requirer} depends on '{depId}', which has no {target} build; skipped.");
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

            var hasPrev = reqMin.TryGetValue(depId, out var curMin);
            if (hasPrev && curMin.Major != major)
            {
                var (prevName, prevC) = reqBy[depId];
                throw new ModManagerException(
                    $"Dependency conflict on '{depId}': {prevName} needs {prevC} (major {curMin.Major}) but {requirer} needs {minV} (major {major}). These majors can't be satisfied together.");
            }

            var newMin = hasPrev && curMin > mv ? curMin : mv;

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
                reqMin[depId] = newMin;
                if (!reqBy.ContainsKey(depId)) reqBy[depId] = (requirer, minV);
            }

            if (chosen.TryGetValue(depId, out var prevChosen) && prevChosen.Version == pickVersion)
            {
                Record();       // already selected at this exact version
                return;
            }

            var manifest = fetchManifest(depId, pickVersion, summary.SourceRepo);
            if (!BuildsForTarget(manifest))
            {
                // Pruned before its constraints are recorded, so a mod we aren't installing
                // can't raise a diamond conflict, and nothing only it needed is walked.
                pruned.Add(depId);
                TavernLogger.Warn($"{requirer} depends on '{depId}' {pickVersion}, which has no {target} build; skipped, along with anything only it needed.");
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
