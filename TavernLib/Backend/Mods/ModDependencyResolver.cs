using System;
using System.Collections.Generic;
using System.Linq;

namespace TavernLib.Backend.Mods;

public static class ModDependencyResolver
{
    private const int MaxDepth = 5;

    /// <summary>Twin of resolve.py's resolve_dependencies; the verdicts must match. The root's dependency
    /// closure by minimum-version-with-major-lock: within the required major, the highest indexed version at
    /// or above the running minimum. Cycles, chains deeper than MaxDepth and cross-major demands throw. A
    /// dependency with no artifact for target is pruned with everything only it needed: "M depends on
    /// pc-only D" can only mean M's pc build needs D. The root itself is never in the result, since any
    /// path back to it is a cycle.</summary>
    public static List<ModManifest> ResolveDependencies(ModManifest root, IReadOnlyList<ModSummary> index,
        Func<string, string, string, ModManifest> fetchManifest, string target)
    {
        var summaries = index.ToDictionary(s => (s.Id, s.Major)); // FetchIndexes yields one row per (id, major)
        var chosen = new Dictionary<string, ModManifest>();
        var reqMin = new Dictionary<string, ModVersion>(); // the required major is reqMin[id].Major
        var reqBy = new Dictionary<string, (string Requirer, string Constraint)>();
        var pruned = new HashSet<string>(); // no target build, so each is fetched at most once

        void Visit(ModManifest mod, List<string> chain)
        {
            if (chain.Count > MaxDepth)
                throw new ModManagerException(
                    $"Dependency chain too deep (> {MaxDepth}): {string.Join(" -> ", chain)}. This is almost certainly a mistake in the mods' dependency data.");

            foreach (var kv in mod.Dependencies.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (chain.Contains(kv.Key))
                    throw new ModManagerException($"Circular dependency: {string.Join(" -> ", chain.Append(kv.Key))}.");
                AddRequirement(kv.Key, kv.Value, mod.Name, chain);
            }
        }

        void AddRequirement(string depId, string minV, string requirer, List<string> chain)
        {
            if (pruned.Contains(depId))
            {
                // Reported again so this requirer isn't invisible, but not re-fetched or re-walked.
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

            summaries.TryGetValue((depId, major), out var summary);
            var available = summary?.Versions ?? [];
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
                Record();
                return;
            }

            var manifest = fetchManifest(depId, pickVersion, summary.SourceRepo);
            if (manifest.ArtifactFor(target) == null)
            {
                // Pruned before its constraints are recorded, so a mod we aren't installing can't raise a
                // diamond conflict, and nothing only it needed is walked.
                pruned.Add(depId);
                TavernLogger.Warn($"{requirer} depends on '{depId}' {pickVersion}, which has no {target} build; skipped, along with anything only it needed.");
                return;
            }

            Record();
            chosen[depId] = manifest;
            Visit(manifest, chain.Append(depId).ToList());
        }

        Visit(root, [root.Id]);
        return chosen.Values.ToList();
    }
}
