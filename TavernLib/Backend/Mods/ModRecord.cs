using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TavernLib.Backend.Mods;

public class InstalledTarget
{
    // UserLibs/ files are reference-counted by these across every record and target.
    [JsonProperty("libraries")] public List<string> Libraries { get; set; } = new();
    [JsonProperty("files")] public Dictionary<string, string> Files { get; set; }
}

// Twin of install.py's _mod_record; names, order and bytes are the on-disk contract.
public class ModRecord
{
    [JsonProperty("id")] public string Id { get; set; }
    [JsonProperty("name")] public string Name { get; set; }
    [JsonProperty("version")] public string Version { get; set; }
    [JsonProperty("parity_required")] public bool ParityRequired { get; set; }
    [JsonProperty("scenes")] public List<string> Scenes { get; set; }
    [JsonProperty("dependencies")] public Dictionary<string, string> Dependencies { get; set; } = new();
    [JsonProperty("artifacts")] public JArray Artifacts { get; set; }
    [JsonProperty("source_repo")] public string SourceRepo { get; set; }
    [JsonProperty("installed")] public Dictionary<string, InstalledTarget> Installed { get; set; }

    public InstalledTarget InstalledFor(string target) =>
        Installed != null && Installed.TryGetValue(target, out var t) ? t : null;

    public bool Lists(string target) => InstalledFor(target) != null;

    public SortedDictionary<string, string> ArtifactTargets()
    {
        var byTarget = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var artifact in ParsedArtifacts().Where(a => a.Usable))
            foreach (var t in artifact.Targets.Where(ModManagerConstants.KnownTargets.Contains))
                if (!byTarget.ContainsKey(t)) byTarget[t] = artifact.Sha256;
        return byTarget;
    }

    public IEnumerable<LibraryDependency> InstalledLibraries(string target)
    {
        var artifact = ParsedArtifacts().FirstOrDefault(a => a.Builds(target));
        foreach (var name in InstalledFor(target)?.Libraries ?? [])
            if (artifact?.LibraryDependencies.FirstOrDefault(l => l.Filename == name) is { } lib)
                yield return lib;
    }

    private IEnumerable<ModArtifact> ParsedArtifacts()
    {
        foreach (var token in Artifacts ?? new JArray())
        {
            ModArtifact artifact;
            try { artifact = ModArtifact.FromJson(token, Id); }
            catch (ModManagerException) { continue; }
            yield return artifact;
        }
    }

    public static JObject Build(ModManifest mod, IDictionary<string, JToken> installed)
    {
        var record = new JObject
        {
            ["manifest_version"] = ModManifest.SupportedMajor,
            ["id"] = mod.Id,
            ["name"] = mod.Name,
            ["version"] = mod.Version,
            ["author"] = mod.Author,
            ["description"] = mod.Description,
            ["parity_required"] = mod.ParityRequired,
        };
        if (mod.Scenes != null) record["scenes"] = new JArray(mod.Scenes);
        record["dependencies"] = new JObject(mod.Dependencies.Select(kv => new JProperty(kv.Key, kv.Value)));
        record["artifacts"] = new JArray(mod.Artifacts.Select(a => a.ToRecordJson()));
        record["source_repo"] = mod.SourceRepo;
        record["installed"] = SortedObject(installed);
        return record;
    }

    public static JObject InstalledEntry(ModArtifact artifact, Dictionary<string, string> files) => new()
    {
        ["sha256"] = artifact.Sha256,
        ["libraries"] = new JArray(artifact.LibraryDependencies.Select(l => ModPaths.SafeBasename(l.Filename))),
        ["files"] = SortedObject(files),
    };

    private static JObject SortedObject<T>(IEnumerable<KeyValuePair<string, T>> pairs) =>
        new(pairs.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new JProperty(kv.Key, kv.Value)));

    // Python's json.dumps(indent=2) bytes, CRLF. DEL is escaped as ensure_ascii does; Json.NET leaves it raw,
    // and it can only occur inside a string.
    internal static string Format(JToken token)
    {
        var text = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\r\n" };
        using (var writer = new JsonTextWriter(text)
               {
                   Formatting = Formatting.Indented,
                   Indentation = 2,
                   IndentChar = ' ',
                   StringEscapeHandling = StringEscapeHandling.EscapeNonAscii,
               })
            token.WriteTo(writer);
        return text.ToString().Replace("\u007f", "\\u007f");
    }

    internal static void WriteFile(string path, JToken token)
    {
        File.WriteAllBytes(path + ".tmp", Encoding.ASCII.GetBytes(Format(token)));
        File.Delete(path);
        File.Move(path + ".tmp", path);
    }

    private static readonly HashSet<string> WarnedUnowned = new();

    // Ownership is ReadOwned's alone, so a malformed field it doesn't look at costs that field, not the record.
    private static readonly JsonSerializerSettings Lenient = new() { Error = (_, e) => e.ErrorContext.Handled = true };

    public static ModRecord Read(string gameDir, string modId, out bool enabled)
    {
        var obj = ReadOwned(gameDir, modId, out var path);
        enabled = Path.GetFileName(path) == ModManagerConstants.RecordName;
        return obj?.ToObject<ModRecord>(JsonSerializer.Create(Lenient));
    }

    // Twin of install.py's _read_mod_record + _owned_mod_record, the "is this folder ours" predicate. An
    // unsafe folder name isn't ours either: one oddly named folder mustn't break every scan.
    internal static JObject ReadOwned(string gameDir, string modId, out string path)
    {
        path = null;
        string dir;
        try { dir = ModPaths.ModDirPath(gameDir, modId); }
        catch (ModManagerException) { return null; }

        foreach (var candidate in new[] { ModManagerConstants.RecordName, ModManagerConstants.DisabledRecordName }.Select(n => Path.Combine(dir, n)))
        {
            if (ParseObject(candidate) is not { } obj || obj["id"]?.Type != JTokenType.String || ((string)obj["id"]).Length == 0) continue;
            if (obj["parity_required"]?.Type != JTokenType.Boolean)
            {
                lock (WarnedUnowned)
                    if (WarnedUnowned.Add(candidate))
                        TavernLogger.Warn($"mod record '{candidate}' has no usable parity_required; treating the mod as not installed until reconcile rewrites it.");
                return null;
            }
            path = candidate;
            return obj;
        }
        return null;
    }

    // Strict UTF-8 with no BOM, as install.py reads it, and no date parsing, so a patched record writes back
    // what it read.
    internal static JObject ParseObject(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return null;
            using var reader = new JsonTextReader(new StringReader(new UTF8Encoding(false, true).GetString(bytes)))
                { DateParseHandling = DateParseHandling.None };
            var token = JToken.ReadFrom(reader);
            return reader.Read() ? null : token as JObject;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
