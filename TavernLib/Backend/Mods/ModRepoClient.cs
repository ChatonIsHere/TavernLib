using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Newtonsoft.Json.Linq;
using TavernLib;

namespace TavernLib.Backend.Mods;

/// <summary>
/// Fetches repository.json indexes and per-version manifests over HTTP(S),
/// merges multi-repo indexes, and resolves a mod id to its manifest - the
/// native-C# counterpart of modmanager.py's index/manifest fetching, following
/// the same by-id / latest*.json resolution contract.
/// </summary>
public class ModRepoClient
{
    private static readonly HttpClient Http =
        WindowsProxy.CreateHttpClient(TimeSpan.FromSeconds(ModManagerConstants.JsonFetchTimeoutSeconds));

    // base URL -> (fetched_at, list<ModSummary>). Per-base so one slow/broken
    // repo doesn't invalidate the others' caches.
    private readonly Dictionary<string, (DateTime FetchedAt, List<ModSummary> Summaries)> _indexCache = new();

    /// <summary>
    /// The one per-repo trust check before a URL is ever registered as a
    /// pullable source (see ModsCommandModule's `addrepo`): GETs
    /// {url}/repository.json and confirms it parses as the expected schema.
    /// Throws with a specific reason on anything else - a URL that doesn't
    /// serve a valid index is rejected here, not discovered later mid-resolve.
    /// </summary>
    public static void ValidateRepoIndex(string url)
    {
        var index = GetJson($"{ModManagerConstants.NormalizeUrl(url)}/repository.json");
        if (index["mods"] is not JObject)
            throw new ModManagerException(
                "That URL didn't serve a valid mod index (no 'mods' object in repository.json). " +
                "Double-check it's the raw-content base URL of a correctly structured repo.");
    }

    private static JObject GetJson(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ModManagerConstants.JsonFetchTimeoutSeconds));
            var response = Http.GetAsync(url, cts.Token).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return JObject.Parse(body);
        }
        catch (Exception e)
        {
            throw new ModManagerException($"Couldn't fetch {url}: {e.Message}");
        }
    }

    /// <summary>
    /// Loads one repo's repository.json into a list of ModSummary (one per
    /// (id, major)), tagging each with SourceRepo. Cached with a TTL; a repo that
    /// fails is returned as [] with a warning, never a hard failure.
    /// </summary>
    private List<ModSummary> FetchRepoIndex(string baseUrl, bool force)
    {
        baseUrl = ModManagerConstants.NormalizeUrl(baseUrl);
        var now = DateTime.UtcNow;
        if (!force && _indexCache.TryGetValue(baseUrl, out var hit) &&
            (now - hit.FetchedAt).TotalSeconds < ModManagerConstants.IndexTtlSeconds)
            return hit.Summaries;

        // A repo that can't be used this time falls back to whatever it last
        // served (past its TTL is still better than nothing), else nothing.
        List<ModSummary> SkipWith(string reason)
        {
            TavernLogger.Warn($"skipping repo {baseUrl}: {reason}");
            return _indexCache.TryGetValue(baseUrl, out var stale) ? stale.Summaries : new List<ModSummary>();
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

        _indexCache[baseUrl] = (now, outList);
        return outList;
    }

    /// <summary>
    /// GETs repository.json from every configured repo and merges them into one
    /// resolved entry per (id, major). On a collision, DEFAULT_REPO wins if
    /// present, otherwise the highest version among the remaining repos.
    /// </summary>
    public List<ModSummary> FetchIndexes(IEnumerable<string> repoBases, bool force = false)
    {
        var merged = new Dictionary<(string Id, int Major), (ModSummary Summary, bool FromDefault)>();
        foreach (var baseUrl in repoBases)
        {
            var fromDefault = ModManagerConstants.IsDefaultRepo(baseUrl);
            foreach (var s in FetchRepoIndex(baseUrl, force))
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
                else if (ModVersion.Parse(s.Highest()) > ModVersion.Parse(cur.Summary.Highest()))
                    merged[key] = (s, fromDefault);
            }
        }
        return merged.Values.Select(v => v.Summary).ToList();
    }

    /// <summary>prefer_repo first (the repo a previous fetch/install already used), then
    /// the rest of repoBases, de-duplicated.</summary>
    private static List<string> RepoOrder(IEnumerable<string> repoBases, string preferRepo)
    {
        var order = new List<string>();
        if (!string.IsNullOrEmpty(preferRepo)) order.Add(preferRepo);
        foreach (var b in repoBases)
            if (!order.Any(x => ModManagerConstants.NormalizeUrl(x) == ModManagerConstants.NormalizeUrl(b)))
                order.Add(b);
        return order;
    }

    /// <summary>
    /// Fetches one manifest file (leaf, e.g. "1.0.4.json" or "latest.1.json") from
    /// manifests/&lt;author&gt;/&lt;repo&gt;/ across the configured repos in preference
    /// order, returning the first that parses.
    /// </summary>
    private ModManifest FetchManifestLeaf(IEnumerable<string> repoBases, string modId, string leaf, string preferRepo = null, string what = null)
    {
        var idx = modId.IndexOf('.');
        if (idx < 0)
            throw new ModManagerException($"Mod id '{modId}' isn't '<github-user>.<github-repo>'.");
        // Both halves become path segments in the URL below, and a mod id
        // arrives from an unreviewed index - so they get the same basename
        // check the disk paths use, before a '..' or '/' can walk the request
        // somewhere else on the host.
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
            if (m != null) return m;
        }
        throw new ModManagerException(
            $"Couldn't find {what ?? modId} in any configured repository." +
            (lastErr != null ? $" Last error: {lastErr.Message}" : ""));
    }

    /// <summary>Loads the full manifest for an exact version.</summary>
    public ModManifest FetchManifest(IEnumerable<string> repoBases, string modId, string version, string preferRepo = null) =>
        FetchManifestLeaf(repoBases, modId, $"{version}.json", preferRepo, $"mod '{modId}' version {version}");

    /// <summary>
    /// Fetches a single mod's manifest by id via the latest*.json pointer files.
    /// If major is given, fetches latest.&lt;major&gt;.json; otherwise latest.json.
    /// </summary>
    public ModManifest ResolveModById(IEnumerable<string> repoBases, string modId, string preferRepo = null, int? major = null)
    {
        var leaf = major.HasValue ? $"latest.{major}.json" : "latest.json";
        var what = $"mod '{modId}'" + (major.HasValue ? $" (major {major})" : "");
        return FetchManifestLeaf(repoBases, modId, leaf, preferRepo, what);
    }
}
