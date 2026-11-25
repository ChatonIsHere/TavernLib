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

// One entry of a source's `incompatible`: two mods that don't work together while each is below its
// `below` (null: at every version). Twin of repos.py's Incompatibility.
public sealed class ModIncompatibility(Dictionary<string, ModVersion?> below, string note, string sourceRepo)
{
    public Dictionary<string, ModVersion?> Below { get; } = below;
    public string Note { get; } = note;
    public string SourceRepo { get; } = sourceRepo;
}

// One entry of a source's `withdrawn`: a version nothing should install. Twin of repos.py's Withdrawal.
public sealed class ModWithdrawal(string modId, string version, string reason, string sourceRepo)
{
    public string ModId { get; } = modId;
    public string Version { get; } = version;
    public string Reason { get; } = reason;
    public string SourceRepo { get; } = sourceRepo;
}

// FetchIndexes' summaries, with every source's replacements, incompatible pairs and withdrawn versions.
// Twin of repos.py's ModIndex.
public class ModIndex : List<ModSummary>
{
    public List<ModReplacement> Replaced { get; } = [];
    public List<ModIncompatibility> Incompatible { get; } = [];
    public List<ModWithdrawal> Withdrawn { get; } = [];

    public ModIndex() { }

    public ModIndex(IEnumerable<ModSummary> summaries, IEnumerable<ModReplacement> replaced,
        IEnumerable<ModIncompatibility> incompatible = null, IEnumerable<ModWithdrawal> withdrawn = null) : base(summaries)
    {
        Replaced.AddRange(replaced);
        Incompatible.AddRange(incompatible ?? []);
        Withdrawn.AddRange(withdrawn ?? []);
    }

    // Twin of repos.py's withdrawal_for without a source: the default source's entry wins, then the first.
    public ModWithdrawal WithdrawalOf(string modId, string version)
    {
        var ws = Withdrawn.Where(w => w.ModId == modId && w.Version == version).ToList();
        return ws.FirstOrDefault(w => IsDefaultRepo(w.SourceRepo)) ?? ws.FirstOrDefault();
    }

    // Twin of repos.py's clashes: the pairs that apply among versions (id -> version), each once. An
    // unreadable version counts as below any bound.
    public List<ModIncompatibility> Clashes(IReadOnlyDictionary<string, string> versions)
    {
        var seen = new HashSet<string>();
        var found = new List<ModIncompatibility>();
        foreach (var inc in Incompatible)
        {
            var key = string.Join("\n", inc.Below.Keys.OrderBy(k => k, StringComparer.Ordinal));
            if (inc.Below.All(side => versions.TryGetValue(side.Key, out var v)
                    && (side.Value is not { } first || !ModVersion.TryParse(v, out var have) || have < first))
                && seen.Add(key))
                found.Add(inc);
        }
        return found;
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

    private (List<ModSummary> Rows, List<ModReplacement> Replaced, List<ModIncompatibility> Incompatible,
        List<ModWithdrawal> Withdrawn) FetchRepoIndex(string baseUrl)
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
        return (rows, ReplacementsFrom(index, baseUrl), IncompatibilitiesFrom(index, baseUrl), WithdrawalsFrom(index, baseUrl));

        (List<ModSummary>, List<ModReplacement>, List<ModIncompatibility>, List<ModWithdrawal>) Skip(string reason)
        {
            TavernLogger.Warn($"skipping repo {baseUrl}: {reason}");
            return ([], [], [], []);
        }
    }

    // Twin of repos.py's _incompatibilities_from: an entry needs exactly two different ids, each mapping to an
    // object, and a string note. A "below" that isn't a version counts as absent: every version clashes.
    public static List<ModIncompatibility> IncompatibilitiesFrom(JObject index, string baseUrl)
    {
        var found = new List<ModIncompatibility>();
        foreach (var token in (index["incompatible"] as JArray) ?? [])
        {
            if (token is not JObject entry || entry["mods"] is not JObject mods || entry["note"]?.Type != JTokenType.String)
                continue;
            var sides = mods.Properties().ToList();
            if (sides.Count != 2 || string.Equals(sides[0].Name, sides[1].Name, StringComparison.OrdinalIgnoreCase)
                || sides.Any(s => s.Value is not JObject))
                continue;
            var below = new Dictionary<string, ModVersion?>();
            foreach (var side in sides)
                below[side.Name] = side.Value["below"]?.Type == JTokenType.String
                                   && ModVersion.TryParse((string)side.Value["below"], out var v) ? v : null;
            found.Add(new ModIncompatibility(below, (string)entry["note"], baseUrl));
        }
        return found;
    }

    // Twin of repos.py's _withdrawals_from: {id: {version: reason}}, string reasons only.
    public static List<ModWithdrawal> WithdrawalsFrom(JObject index, string baseUrl)
    {
        var found = new List<ModWithdrawal>();
        foreach (var mod in (index["withdrawn"] as JObject)?.Properties() ?? [])
            foreach (var version in (mod.Value as JObject)?.Properties() ?? [])
                if (version.Value.Type == JTokenType.String)
                    found.Add(new ModWithdrawal(mod.Name, version.Name, (string)version.Value, baseUrl));
        return found;
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
        var incompatible = new List<ModIncompatibility>();
        var withdrawn = new List<ModWithdrawal>();
        foreach (var baseUrl in repos)
        {
            var fromDefault = IsDefaultRepo(baseUrl);
            var (rows, reps, incs, gone) = FetchRepoIndex(baseUrl);
            replaced.AddRange(reps);
            incompatible.AddRange(incs);
            withdrawn.AddRange(gone);
            foreach (var s in rows)
                if (!merged.TryGetValue((s.Id, s.Major), out var cur) || (fromDefault && !cur.FromDefault)
                    || (fromDefault == cur.FromDefault && s.Highest > cur.Summary.Highest))
                    merged[(s.Id, s.Major)] = (s, fromDefault);
        }
        return new ModIndex(merged.Values.Select(v => v.Summary), replaced, incompatible, withdrawn);
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
