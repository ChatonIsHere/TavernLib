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
    // {forward-slash path relative to .<target>/: lowercase sha256}.
    [JsonProperty("files")] public Dictionary<string, string> Files { get; set; }
}

/// <summary>Mods/&lt;id&gt;/manifest.json, also MelonLoader's folder marker: the fetched manifest plus what
/// is installed for each target. Twin of install.py's _mod_record; names, order and bytes are the on-disk
/// contract.</summary>
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
    // Null on a record from before targets.
    [JsonProperty("installed")] public Dictionary<string, InstalledTarget> Installed { get; set; }

    public InstalledTarget InstalledFor(string target) =>
        Installed != null && Installed.TryGetValue(target, out var t) ? t : null;

    // What loading, the handshake and reconcile all key on.
    public bool Lists(string target) => InstalledFor(target) != null;

    // Each known target the record's usable artifacts build, sorted, with the first such build's sha256.
    public SortedDictionary<string, string> ArtifactTargets()
    {
        var byTarget = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var artifact in ParsedArtifacts().Where(a => a.Usable))
            foreach (var t in artifact.Targets.Where(ModManagerConstants.KnownTargets.Contains))
                if (!byTarget.ContainsKey(t)) byTarget[t] = artifact.Sha256;
        return byTarget;
    }

    // The pins of the build installed for target, read off its artifact; one it doesn't describe is left out.
    public IEnumerable<LibraryDependency> InstalledLibraries(string target)
    {
        var artifact = ParsedArtifacts().FirstOrDefault(a => a.Builds(target));
        foreach (var name in InstalledFor(target)?.Libraries ?? [])
            if (artifact?.LibraryDependencies.FirstOrDefault(l => l.Filename == name) is { } lib)
                yield return lib;
    }

    // A record is read leniently: an artifact that doesn't parse is skipped.
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

    /// <summary>The record install.py's _mod_record writes: keys in spec order, `scenes` only when set,
    /// every artifact kept, `installed` sorted by target.</summary>
    public static JObject Build(ModManifest mod, IDictionary<string, JToken> installed)
    {
        var record = new JObject
        {
            ["manifest_version"] = mod.ManifestVersion,
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

    // One `installed` entry: libraries in manifest order, files sorted by ordinal.
    public static JObject InstalledEntry(ModArtifact artifact, Dictionary<string, string> files) => new()
    {
        ["sha256"] = artifact.Sha256,
        ["libraries"] = new JArray(artifact.LibraryDependencies.Select(l => ModPaths.SafeBasename(l.Filename))),
        ["files"] = SortedObject(files),
    };

    private static JObject SortedObject<T>(IEnumerable<KeyValuePair<string, T>> pairs) =>
        new(pairs.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new JProperty(kv.Key, kv.Value)));

    /// <summary>The sidecar bytes both installers share, as Python's json.dumps(indent=2) writes them:
    /// two-space indent, non-ASCII as lowercase \uXXXX, CRLF, no trailing newline. DEL is escaped too, as
    /// ensure_ascii does; Json.NET leaves it raw, and it can only occur inside a string.</summary>
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

    // Via a temp file, so a cut-short write never leaves a truncated record in place.
    internal static void WriteFile(string path, JToken token)
    {
        File.WriteAllBytes(path + ".tmp", Encoding.ASCII.GetBytes(Format(token)));
        File.Delete(path);
        File.Move(path + ".tmp", path);
    }

    private static readonly HashSet<string> WarnedUnowned = new();

    // Ownership is ReadOwned's predicate alone, so a malformed field it doesn't look at costs that field,
    // not the whole record.
    private static readonly JsonSerializerSettings Lenient = new() { Error = (_, e) => e.ErrorContext.Handled = true };

    // enabled: the record is manifest.json rather than manifest.disabled.json.
    public static ModRecord Read(string gameDir, string modId, out bool enabled)
    {
        var obj = ReadOwned(gameDir, modId, out var path);
        enabled = Path.GetFileName(path) == ModManagerConstants.RecordName;
        return obj?.ToObject<ModRecord>(JsonSerializer.Create(Lenient));
    }

    /// <summary>Twin of install.py's _read_mod_record + _owned_mod_record, the "is this folder ours"
    /// predicate: the first of manifest.json and manifest.disabled.json holding an object with a non-empty
    /// string id is the record, and it's ours only if its parity_required is a JSON boolean. Null when it
    /// isn't, or when modId isn't a safe folder name (one oddly named folder mustn't break every scan);
    /// <paramref name="path"/> is the file it came from.</summary>
    internal static JObject ReadOwned(string gameDir, string modId, out string path)
    {
        path = null;
        string dir;
        try { dir = ModPaths.ModDirPath(gameDir, modId); }
        catch (ModManagerException) { return null; }

        foreach (var candidate in new[] { ModManagerConstants.RecordName, ModManagerConstants.DisabledRecordName }.Select(n => Path.Combine(dir, n)))
        {
            // A non-empty string id, as install.py's _folder_record requires: any other can't sort into the
            // handshake alongside real ones.
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

    // The file's top-level JSON object, or null if it's missing or isn't one. Strict UTF-8 with no BOM, as
    // install.py's open(encoding="utf-8") + json.load reads it, and no date parsing, so a record patched
    // through this writes back what it read.
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
