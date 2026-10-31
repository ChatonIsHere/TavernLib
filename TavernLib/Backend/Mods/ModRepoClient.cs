using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static TavernLib.Backend.Mods.ModManagerConstants;

namespace TavernLib.Backend.Mods;

// One entry of a source's `replaced`: OldId is retired in favour of By. Twin of repos.py's Replacement.
public sealed class ModReplacement(string oldId, string by, string note, string sourceRepo)
{
    public string OldId { get; } = oldId;
    public string By { get; } = by;
    public string Note { get; } = note;
    public string SourceRepo { get; } = sourceRepo;
}

// FetchIndexes' summaries, with every source's replacements. Twin of repos.py's ModIndex.
public class ModIndex : List<ModSummary>
{
    public List<ModReplacement> Replaced { get; } = [];

    public ModIndex() { }

    public ModIndex(IEnumerable<ModSummary> summaries, IEnumerable<ModReplacement> replaced) : base(summaries)
    {
        Replaced.AddRange(replaced);
    }

    // Twin of repos.py's replacement_of_unlisted: for an id named without a source (a dependency, a modlist
    // entry), only when no source lists the id and one lists its replacement. The default source's entry
    // wins, then the first source's.
    public static ModReplacement ReplacementOfUnlisted(IReadOnlyList<ModSummary> index, string modId)
    {
        if (index.Any(s => s.Id == modId)) return null;
        var reps = (index as ModIndex)?.Replaced.Where(r => r.OldId == modId).ToList() ?? [];
        var rep = reps.FirstOrDefault(r => IsDefaultRepo(r.SourceRepo)) ?? reps.FirstOrDefault();
        return rep != null && index.Any(s => s.Id == rep.By) ? rep : null;
    }
}

// Twin of repos.py's index fetch and resolve.py's manifest fetches.
// deadlineUtc, when set, also bounds each fetch, as the reconcile pass's budget.
public class ModRepoClient(NativeHttp http, List<string> repos, DateTime? deadlineUtc = null)
{
    private const int SupportedIndexMajor = 2;
    private const long MaxJsonBytes = 8L * 1024 * 1024;
    private const int JsonTimeoutSeconds = 15;

    public static void ValidateRepoIndex(string url)
    {
        using var http = new NativeHttp();
        var index = new ModRepoClient(http, []).GetJson($"{NormalizeUrl(url)}/repository.json");
        if (index["mods"] is not JObject)
            throw new ModManagerException(
                "That URL didn't serve a valid mod index (no 'mods' object in repository.json). " +
                "Double-check it's the raw-content base URL of a correctly structured repo.");
        if (!IsSupported(index))
            throw new ModManagerException(
                $"That repository's index is schema v{index["index_version"]}, and this build reads " +
                $"v{SupportedIndexMajor} only, so it would list nothing from it.");
    }

    // JValue.Value is a long, or a BigInteger past one, so no cast can overflow.
    private static bool IsSupported(JObject index) =>
        index["index_version"] is JValue { Type: JTokenType.Integer, Value: long v } && v == SupportedIndexMajor;

    private JObject GetJson(string url)
    {
        try
        {
            var timeout = TimeSpan.FromSeconds(JsonTimeoutSeconds);
            if (deadlineUtc is { } deadline && deadline - DateTime.UtcNow < timeout) timeout = deadline - DateTime.UtcNow;
            var body = http.GetString(url, timeout, MaxJsonBytes);
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

    private (List<ModSummary> Rows, List<ModReplacement> Replaced) FetchRepoIndex(string baseUrl)
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

        if (!IsSupported(index))
            return Skip($"index schema v{index["index_version"]}, this build supports v{SupportedIndexMajor}.");

        var rows = new List<ModSummary>();
        foreach (var mod in (index["mods"] as JObject)?.Properties() ?? [])
            foreach (var major in (mod.Value as JObject)?.Properties() ?? [])
                if (major.Value is JObject entry && ModSummary.FromJson(entry, mod.Name, baseUrl) is { } summary)
                    rows.Add(summary);
        return (rows, ReplacementsFrom(index, baseUrl));

        (List<ModSummary>, List<ModReplacement>) Skip(string reason)
        {
            TavernLogger.Warn($"skipping repo {baseUrl}: {reason}");
            return ([], []);
        }
    }

    // Twin of repos.py's _replacements_from: entries that aren't an object with a string "by" naming another id are skipped.
    public static List<ModReplacement> ReplacementsFrom(JObject index, string baseUrl)
    {
        var replaced = new List<ModReplacement>();
        foreach (var p in (index["replaced"] as JObject)?.Properties() ?? [])
            if (p.Value is JObject entry && entry["by"]?.Type == JTokenType.String && (string)entry["by"] != p.Name)
                replaced.Add(new ModReplacement(p.Name, (string)entry["by"],
                    entry["note"]?.Type == JTokenType.String ? (string)entry["note"] : "", baseUrl));
        return replaced;
    }

    // Twin of repos.py's fetch_indexes.
    public ModIndex FetchIndexes()
    {
        var merged = new Dictionary<(string, int), (ModSummary Summary, bool FromDefault)>();
        var replaced = new List<ModReplacement>();
        foreach (var baseUrl in repos)
        {
            var fromDefault = IsDefaultRepo(baseUrl);
            var (rows, reps) = FetchRepoIndex(baseUrl);
            replaced.AddRange(reps);
            foreach (var s in rows)
                if (!merged.TryGetValue((s.Id, s.Major), out var cur) || (fromDefault && !cur.FromDefault)
                    || (fromDefault == cur.FromDefault && s.Highest > cur.Summary.Highest))
                    merged[(s.Id, s.Major)] = (s, fromDefault);
        }
        return new ModIndex(merged.Values.Select(v => v.Summary), replaced);
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
