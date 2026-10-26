using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static TavernLib.Backend.Mods.ModManagerConstants;

namespace TavernLib.Backend.Mods;

// Twin of repos.py's index fetch and resolve.py's manifest fetches.
public class ModRepoClient(NativeHttp http, List<string> repos)
{
    private const int SupportedIndexMajor = 2;
    private const long MaxJsonBytes = 8L * 1024 * 1024;
    private const int JsonTimeoutSeconds = 15;

    public static void ValidateRepoIndex(string url)
    {
        using var http = new NativeHttp();
        if (new ModRepoClient(http, []).GetJson($"{NormalizeUrl(url)}/repository.json")["mods"] is not JObject)
            throw new ModManagerException(
                "That URL didn't serve a valid mod index (no 'mods' object in repository.json). " +
                "Double-check it's the raw-content base URL of a correctly structured repo.");
    }

    private JObject GetJson(string url)
    {
        try
        {
            var body = http.GetString(url, TimeSpan.FromSeconds(JsonTimeoutSeconds), MaxJsonBytes);
            // No date parsing: a date-like string must reach the record as install.py writes it.
            using var reader = new JsonTextReader(new StringReader(body)) { DateParseHandling = DateParseHandling.None };
            var obj = JObject.Load(reader);
            if (reader.Read()) throw new JsonReaderException("Additional text after the JSON object.");
            return obj;
        }
        catch (Exception e)
        {
            throw new ModManagerException($"Couldn't fetch {url}: {ModManagerException.Describe(e)}");
        }
    }

    private List<ModSummary> FetchRepoIndex(string baseUrl)
    {
        baseUrl = NormalizeUrl(baseUrl);
        JObject index;
        try
        {
            index = GetJson($"{baseUrl}/repository.json");
        }
        catch (ModManagerException e)
        {
            return Skip(e.Message);
        }

        var indexVersion = index["index_version"]?.Type == JTokenType.Integer ? (int)index["index_version"] : (int?)null;
        if (indexVersion != SupportedIndexMajor)
            return Skip($"index schema v{indexVersion}, this build supports v{SupportedIndexMajor}.");

        var rows = new List<ModSummary>();
        foreach (var mod in (index["mods"] as JObject)?.Properties() ?? [])
            foreach (var major in (mod.Value as JObject)?.Properties() ?? [])
                if (major.Value is JObject entry && ModSummary.FromJson(entry, mod.Name, baseUrl) is { } summary)
                    rows.Add(summary);
        return rows;

        List<ModSummary> Skip(string reason)
        {
            TavernLogger.Warn($"skipping repo {baseUrl}: {reason}");
            return [];
        }
    }

    // Twin of repos.py's fetch_indexes.
    public List<ModSummary> FetchIndexes()
    {
        var merged = new Dictionary<(string, int), (ModSummary Summary, bool FromDefault)>();
        foreach (var baseUrl in repos)
        {
            var fromDefault = IsDefaultRepo(baseUrl);
            foreach (var s in FetchRepoIndex(baseUrl))
                if (!merged.TryGetValue((s.Id, s.Major), out var cur) || (fromDefault && !cur.FromDefault)
                    || (fromDefault == cur.FromDefault && s.Highest > cur.Summary.Highest))
                    merged[(s.Id, s.Major)] = (s, fromDefault);
        }
        return merged.Values.Select(v => v.Summary).ToList();
    }

    // Twin of resolve.py's _fetch_manifest_leaf. A preferRepo that isn't trusted is ignored.
    private ModManifest FetchManifestLeaf(string modId, string leaf, string preferRepo, string what, string expectedVersion)
    {
        var idx = modId.IndexOf('.');
        if (idx < 0)
            throw new ModManagerException($"Mod id '{modId}' isn't '<github-user>.<github-repo>'.");
        // Both halves become URL path segments, and the id comes from an unreviewed index.
        var author = ModPaths.SafeBasename(modId.Substring(0, idx));
        var repo = ModPaths.SafeBasename(modId.Substring(idx + 1));

        Exception lastErr = null;
        var order = repos.Select(NormalizeUrl).OrderBy(b => preferRepo == null || b != NormalizeUrl(preferRepo)).Distinct();
        foreach (var baseUrl in order)
        {
            var url = $"{baseUrl}/manifests/{author}/{repo}/{leaf}";
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
            var m = ModManifest.FromJson(data, baseUrl);
            if (m == null) continue;
            // A repo serving another mod's (or version's) manifest at this path is that repo's failure.
            if (m.Id == modId && (expectedVersion == null || m.Version == expectedVersion)) return m;
            lastErr = new ModManagerException(
                $"{url} served '{m.Id}' {m.Version}, not the requested '{modId}' {expectedVersion ?? "(latest)"}.");
        }
        throw new ModManagerException(
            $"Couldn't find {what} in any configured repository." + (lastErr != null ? $" Last error: {lastErr.Message}" : ""));
    }

    public ModManifest FetchManifest(string modId, string version, string preferRepo) =>
        FetchManifestLeaf(modId, $"{version}.json", preferRepo, $"mod '{modId}' version {version}", version);

    public ModManifest FetchLatest(string modId, string preferRepo) =>
        FetchManifestLeaf(modId, "latest.json", preferRepo, $"mod '{modId}'", null);
}
