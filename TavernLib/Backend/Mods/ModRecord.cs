using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TavernLib.Backend.Mods;

/// <summary>One target's entry under a record's `installed`.</summary>
public class InstalledTarget
{
    [JsonProperty("sha256")] public string Sha256 { get; set; }

    /// <summary>The library filenames this build pins; UserLibs/ files are
    /// reference-counted by these across every record and target.</summary>
    [JsonProperty("libraries")] public List<string> Libraries { get; set; } = new();

    /// <summary>{forward-slash path relative to .&lt;target&gt;/: lowercase sha256}
    /// of what the install placed. Not in the handshake hash.</summary>
    [JsonProperty("files")] public Dictionary<string, string> Files { get; set; }
}

/// <summary>
/// Mods/&lt;id&gt;/manifest.json, also MelonLoader's folder marker: the fetched
/// manifest plus what is installed for each target. Twin of install.py's
/// _mod_record; names, order and bytes are the on-disk contract.
/// </summary>
public class ModRecord
{
    [JsonProperty("manifest_version")] public int ManifestVersion { get; set; }
    [JsonProperty("id")] public string Id { get; set; }
    [JsonProperty("name")] public string Name { get; set; }
    [JsonProperty("version")] public string Version { get; set; }
    [JsonProperty("author")] public string Author { get; set; }
    [JsonProperty("description")] public string Description { get; set; }

    /// <summary>Required on disk: <see cref="Read"/> refuses a record without a
    /// JSON boolean here rather than guess whether clients must match it.</summary>
    [JsonProperty("parity_required")]
    public bool ParityRequired { get; set; }
    [JsonProperty("scenes")] public List<string> Scenes { get; set; }
    [JsonProperty("dependencies")] public Dictionary<string, string> Dependencies { get; set; } = new();
    [JsonProperty("artifacts")] public JArray Artifacts { get; set; }
    [JsonProperty("source_repo")] public string SourceRepo { get; set; }

    /// <summary>Null on a record from before targets, which lists none.</summary>
    [JsonProperty("installed")] public Dictionary<string, InstalledTarget> Installed { get; set; }

    public InstalledTarget InstalledFor(string target) =>
        Installed != null && Installed.TryGetValue(target, out var t) ? t : null;

    /// <summary>Whether target is installed here: what loading, the handshake
    /// and reconcile all key on.</summary>
    public bool Lists(string target) => InstalledFor(target) != null;

    /// <summary>Each known target the record's artifacts build, sorted, with
    /// that build's sha256. An artifact that doesn't parse is skipped: a record
    /// is read leniently.</summary>
    public SortedDictionary<string, string> ArtifactTargets()
    {
        var byTarget = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var artifact in ParsedArtifacts())
            foreach (var t in artifact.Targets.Where(t => artifact.Usable && ModManagerConstants.KnownTargets.Contains(t)))
                if (!byTarget.ContainsKey(t))
                    byTarget[t] = artifact.Sha256;
        return byTarget;
    }

    /// <summary>The (filename, sha256) pins of the build installed for target,
    /// read off its artifact; a library that artifact doesn't describe is left out.</summary>
    public IEnumerable<LibraryDependency> InstalledLibraries(string target)
    {
        var artifact = ParsedArtifacts().FirstOrDefault(a => a.Builds(target));
        foreach (var name in InstalledFor(target)?.Libraries ?? new List<string>())
            if (artifact?.LibraryDependencies.FirstOrDefault(l => l.Filename == name) is { } lib)
                yield return lib;
    }

    private IEnumerable<ModArtifact> ParsedArtifacts()
    {
        foreach (var token in Artifacts ?? new JArray())
        {
            ModArtifact artifact;
            try
            {
                artifact = ModArtifact.FromJson(token, Id);
            }
            catch (ModManagerException)
            {
                continue;
            }
            yield return artifact;
        }
    }

    /// <summary>
    /// The record install.py's _mod_record writes for this manifest with these
    /// `installed` entries: keys in spec order, `scenes` only when set, every
    /// artifact kept, `installed` sorted by target.
    /// </summary>
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
            [ModParityField.Name] = mod.ParityRequired,
        };
        if (mod.Scenes != null) record[ModScenesField.Name] = new JArray(mod.Scenes);
        var deps = new JObject();
        foreach (var kv in mod.Dependencies ?? new Dictionary<string, string>()) deps[kv.Key] = kv.Value;
        record["dependencies"] = deps;
        record["artifacts"] = new JArray(mod.Artifacts.Select(a => a.ToRecordJson()));
        record["source_repo"] = mod.SourceRepo;
        var sorted = new JObject();
        foreach (var kv in installed.OrderBy(kv => kv.Key, StringComparer.Ordinal)) sorted[kv.Key] = kv.Value;
        record["installed"] = sorted;
        return record;
    }

    /// <summary>One `installed` entry: the build's sha256, its pinned library
    /// filenames in manifest order, and the files map sorted by ordinal.</summary>
    public static JObject InstalledEntry(ModArtifact artifact, Dictionary<string, string> files)
    {
        var sortedFiles = new JObject();
        foreach (var kv in files.OrderBy(kv => kv.Key, StringComparer.Ordinal)) sortedFiles[kv.Key] = kv.Value;
        return new JObject
        {
            ["sha256"] = artifact.Sha256,
            ["libraries"] = new JArray(artifact.LibraryDependencies.Select(l => ModPaths.SafeBasename(l.Filename))),
            ["files"] = sortedFiles,
        };
    }

    /// <summary>
    /// The sidecar byte format both installers share, as Python's
    /// json.dumps(indent=2) writes it: two-space indent, non-ASCII as lowercase
    /// \uXXXX, CRLF, no trailing newline. DEL is escaped too, as ensure_ascii
    /// does; Json.NET leaves it raw, and it can only occur inside a string.
    /// </summary>
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

    /// <summary>Writes a sidecar via a temp file, so a cut-short write never
    /// leaves a truncated record in place.</summary>
    internal static void WriteFile(string path, JToken token)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, Encoding.ASCII.GetBytes(Format(token)));
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    private static readonly HashSet<string> WarnedUnreadable = new();

    // Ownership is decided by the predicate in Read alone, so a malformed field
    // it doesn't look at costs that field, not the whole record.
    private static readonly JsonSerializerSettings Lenient = new()
    {
        Error = (_, e) => e.ErrorContext.Handled = true,
    };

    public static ModRecord Read(string gameDir, string modId) => Read(gameDir, modId, out _);

    /// <summary><paramref name="enabled"/> is whether the record came from
    /// manifest.json, as list_installed_mods reports it.</summary>
    public static ModRecord Read(string gameDir, string modId, out bool enabled)
    {
        var obj = ReadOwned(gameDir, modId, out var path);
        enabled = path != null && path == ModPaths.ModRecordPath(gameDir, modId);
        return obj?.ToObject<ModRecord>(JsonSerializer.Create(Lenient));
    }

    /// <summary>Twin of install.py:_read_mod_record + _owned_mod_record, the "is
    /// this folder ours" predicate: the first of manifest.json and
    /// manifest.disabled.json holding an object with a non-empty string id is the record,
    /// and it's ours only if its parity_required is a JSON boolean. Null when it
    /// isn't; <paramref name="path"/> is the file it came from.</summary>
    internal static JObject ReadOwned(string gameDir, string modId, out string path)
    {
        foreach (var candidate in new[] { ModPaths.ModRecordPath(gameDir, modId), ModPaths.DisabledRecordPath(gameDir, modId) })
        {
            if (ParseObject(candidate) is not { } obj || !HasId(obj["id"])) continue;
            if (obj[ModParityField.Name]?.Type != JTokenType.Boolean)
            {
                lock (WarnedUnreadable)
                {
                    if (WarnedUnreadable.Add(candidate))
                        TavernLogger.Warn($"mod record '{candidate}' has no usable {ModParityField.Name}; treating the mod as not installed until reconcile rewrites it.");
                }
                break;
            }
            path = candidate;
            return obj;
        }
        path = null;
        return null;
    }

    /// <summary>The file's top-level JSON object, or null if it's missing, not
    /// JSON, or not an object. Strings stay strings (no date parsing), so a
    /// record patched through this writes back what it read.</summary>
    internal static JObject ParseObject(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            // Strict UTF-8 with no BOM, as install.py's open(encoding="utf-8")
            // + json.load reads it; File.ReadAllText would accept either.
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return null;
            var text = new UTF8Encoding(false, true).GetString(bytes);
            using var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None };
            var token = JToken.ReadFrom(reader);
            return reader.Read() ? null : token as JObject;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // A non-empty JSON string, as install.py's _folder_record requires: any
    // other id can't sort into the handshake alongside real ones.
    private static bool HasId(JToken t) => t?.Type == JTokenType.String && ((string)t).Length > 0;
}
