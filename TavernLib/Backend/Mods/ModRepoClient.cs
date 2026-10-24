using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using TavernLib;

namespace TavernLib.Backend.Mods;

/// <summary>
/// Index and manifest fetches from the trusted repos: twin of repos.py's index
/// fetch and resolve.py's manifest fetches.
/// </summary>
public class ModRepoClient(NativeHttp http)
{
    /// <summary>The live check `addrepo` runs before trusting a URL: its
    /// repository.json must parse as an index.</summary>
    public static void ValidateRepoIndex(string url)
    {
        using var http = new NativeHttp();
        var index = new ModRepoClient(http).GetJson($"{ModManagerConstants.NormalizeUrl(url)}/repository.json");
        if (index["mods"] is not JObject)
            throw new ModManagerException(
                "That URL didn't serve a valid mod index (no 'mods' object in repository.json). " +
                "Double-check it's the raw-content base URL of a correctly structured repo.");
    }

    private JObject GetJson(string url)
    {
        try
        {
            var body = http.GetString(url, TimeSpan.FromSeconds(ModManagerConstants.JsonFetchTimeoutSeconds),
                ModManagerConstants.MaxJsonBytes);
            return JObject.Parse(body);
        }
        catch (Exception e)
        {
            throw new ModManagerException($"Couldn't fetch {url}: {ModManagerException.Describe(e)}");
        }
    }

    /// <summary>One repo's index as ModSummary rows; a repo that fails is skipped
    /// with a warning.</summary>
    private List<ModSummary> FetchRepoIndex(string baseUrl)
    {
        baseUrl = ModManagerConstants.NormalizeUrl(baseUrl);

        List<ModSummary> SkipWith(string reason)
        {
            TavernLogger.Warn($"skipping repo {baseUrl}: {reason}");
            return new List<ModSummary>();
        }

        JObject index;
        try
        {
            index = GetJson($"{baseUrl}/repository.json");
        }
        catch (ModManagerException e)
        {
            return SkipWith(e.Message);
        }

        var indexVersion = index["index_version"]?.Type == JTokenType.Integer ? (int)index["index_version"] : (int?)null;
        if (indexVersion != ModManagerConstants.SupportedIndexMajor)
            return SkipWith($"index schema v{indexVersion}, this build supports v{ModManagerConstants.SupportedIndexMajor}.");

        var outList = new List<ModSummary>();
        if (index["mods"] is JObject mods)
        {
            foreach (var modProp in mods.Properties())
            {
                if (modProp.Value is not JObject byMajor) continue;
                foreach (var majorProp in byMajor.Properties())
                {
                    if (majorProp.Value is not JObject entry) continue;
                    var summary = ModSummary.FromJson(entry, modProp.Name, baseUrl);
                    if (summary != null) outList.Add(summary);
                }
            }
        }

        return outList;
    }

    /// <summary>Twin of repos.py's fetch_indexes: one entry per (id, major); on a
    /// collision the default repo wins, else the highest version.</summary>
    public List<ModSummary> FetchIndexes(IEnumerable<string> repoBases)
    {
        var merged = new Dictionary<(string Id, int Major), (ModSummary Summary, bool FromDefault)>();
        foreach (var baseUrl in repoBases)
        {
            var fromDefault = ModManagerConstants.IsDefaultRepo(baseUrl);
            foreach (var s in FetchRepoIndex(baseUrl))
            {
                var key = (s.Id, s.Major);
                if (!merged.TryGetValue(key, out var cur))
                {
                    merged[key] = (s, fromDefault);
                    continue;
                }
                if (fromDefault && !cur.FromDefault)
                    merged[key] = (s, true);
                else if (cur.FromDefault && !fromDefault)
                    { /* keep the default */ }
                else if (s.Highest > cur.Summary.Highest)
                    merged[key] = (s, fromDefault);
            }
        }
        return merged.Values.Select(v => v.Summary).ToList();
    }

    /// <summary>repoBases with preferRepo moved to the front. A preference only
    /// reorders the trusted repos; a hint naming any other repo is ignored.</summary>
    private static List<string> RepoOrder(IEnumerable<string> repoBases, string preferRepo)
    {
        static bool Same(string a, string b) => ModManagerConstants.NormalizeUrl(a) == ModManagerConstants.NormalizeUrl(b);
        var bases = repoBases.ToList();
        var order = new List<string>();
        foreach (var b in bases.Where(b => preferRepo != null && Same(b, preferRepo)).Concat(bases))
            if (!order.Any(x => Same(x, b)))
                order.Add(b);
        return order;
    }

    /// <summary>Twin of resolve.py's _fetch_manifest_leaf: the first trusted repo,
    /// in preference order, serving a usable manifest for this mod at leaf.</summary>
    private ModManifest FetchManifestLeaf(IEnumerable<string> repoBases, string modId, string leaf, string preferRepo, string what,
        string expectedVersion = null)
    {
        var idx = modId.IndexOf('.');
        if (idx < 0)
            throw new ModManagerException($"Mod id '{modId}' isn't '<github-user>.<github-repo>'.");
        // Both halves become URL path segments and the id comes from an unreviewed
        // index, so they get the disk paths' basename check.
        var author = ModPaths.SafeBasename(modId.Substring(0, idx));
        var repo = ModPaths.SafeBasename(modId.Substring(idx + 1));

        Exception lastErr = null;
        foreach (var baseUrl in RepoOrder(repoBases, preferRepo))
        {
            var url = $"{ModManagerConstants.NormalizeUrl(baseUrl)}/manifests/{author}/{repo}/{leaf}";
            JObject data;
            try
            {
                data = GetJson(url);
            }
            catch (ModManagerException e)
            {
                lastErr = e;
                continue;
            }
            var m = ModManifest.FromJson(data, ModManagerConstants.NormalizeUrl(baseUrl));
            if (m == null) continue;
            // A repo serving another mod's (or version's) manifest at this path
            // is a failure of that repo, not something to install.
            if (m.Id != modId || (expectedVersion != null && m.Version != expectedVersion))
            {
                lastErr = new ModManagerException(
                    $"{url} served '{m.Id}' {m.Version}, not the requested '{modId}' {expectedVersion ?? "(latest)"}.");
                continue;
            }
            return m;
        }
        throw new ModManagerException(
            $"Couldn't find {what ?? modId} in any configured repository." +
            (lastErr != null ? $" Last error: {lastErr.Message}" : ""));
    }

    /// <summary>Loads the full manifest for an exact version.</summary>
    public ModManifest FetchManifest(IEnumerable<string> repoBases, string modId, string version, string preferRepo = null) =>
        FetchManifestLeaf(repoBases, modId, $"{version}.json", preferRepo, $"mod '{modId}' version {version}", version);

    /// <summary>The latest manifest via latest.json, or latest.&lt;major&gt;.json.</summary>
    public ModManifest ResolveModById(IEnumerable<string> repoBases, string modId, string preferRepo = null, int? major = null)
    {
        var leaf = major.HasValue ? $"latest.{major}.json" : "latest.json";
        var what = $"mod '{modId}'" + (major.HasValue ? $" (major {major})" : "");
        return FetchManifestLeaf(repoBases, modId, leaf, preferRepo, what);
    }
}
