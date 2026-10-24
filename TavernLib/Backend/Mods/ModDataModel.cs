using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TavernLib;

namespace TavernLib.Backend.Mods;

/// <summary>A pinned, exact-version file a mod needs in UserLibs/: not a mod, so
/// no id and no version range.</summary>
public class LibraryDependency
{
    [JsonProperty("name")] public string Name { get; set; }
    [JsonProperty("download_url")] public string DownloadUrl { get; set; }
    [JsonProperty("sha256")] public string Sha256 { get; set; }
    [JsonProperty("filename")] public string Filename { get; set; }

    public static LibraryDependency FromJson(JObject d)
    {
        foreach (var key in new[] { "name", "download_url", "sha256", "filename" })
            if (d[key] == null)
                throw new ModManagerException($"library_dependencies entry is missing '{key}'.");
        return new LibraryDependency
        {
            Name = (string)d["name"],
            DownloadUrl = (string)d["download_url"],
            Sha256 = ModManifest.Sha256Field(d, "library_dependencies entry"),
            Filename = (string)d["filename"],
        };
    }
}

/// <summary>
/// Reads parity_required: whether a client joining a server that runs the mod
/// must have its exact version. Twin of manifest.py's parity_required:
/// mandatory and strictly a JSON boolean, since guessing is what would let a
/// required mod pass as optional. Callers decide what a failure costs.
/// </summary>
public static class ModParityField
{
    public const string Name = "parity_required";

    public static bool Read(JToken value, string idForMsg)
    {
        if (value == null || value.Type != JTokenType.Boolean)
            throw new ModManagerException(
                $"'{idForMsg}' has no usable {Name} field. It's required: true if a "
                + "client joining a server running this mod must have this exact "
                + "version, false if the server shouldn't block the join over it.");
        return (bool)value;
    }
}

/// <summary>
/// Twin of manifest.py's mod_scenes: null (absent or JSON null) means every
/// scene; a present value must be a non-empty list of non-empty strings, and
/// unknown names are kept so a scene added later doesn't break older readers.
/// </summary>
public static class ModScenesField
{
    public const string Name = "scenes";
    public static readonly string[] Known = ["normal", "quest", "tutorial"];

    public static List<string> Read(JToken value, string idForMsg)
    {
        if (value == null || value.Type == JTokenType.Null) return null;
        if (value is not JArray array || array.Count == 0
            || array.Any(t => t.Type != JTokenType.String || string.IsNullOrEmpty((string)t)))
            throw new ModManagerException(
                $"'{idForMsg}' has a {Name} value of {value.ToString(Newtonsoft.Json.Formatting.None)}. Leave it out "
                + "for a mod that works in every scene, or list the ones it works in, out of "
                + string.Join(", ", Known) + ".");
        return array.Select(t => (string)t).Distinct().ToList();
    }

    /// <summary>Whether a mod with these scenes works in scene. An unknown scene
    /// (null) never rules a mod out.</summary>
    public static bool RunsIn(List<string> scenes, string scene) =>
        scenes == null || scene == null || scenes.Contains(scene);
}

/// <summary>
/// One build in a manifest's artifacts list. Twin of manifest.py's
/// _artifact_from_dict: every entry is checked, but only one with a known
/// package is ever installed, and only for a target this build knows.
/// </summary>
public class ModArtifact
{
    // The compiled manifest's key order, which a record keeps.
    internal static readonly string[] KeyOrder = ["targets", "download_url", "sha256", "package", "library_dependencies"];
    internal static readonly string[] LibraryKeyOrder = ["name", "download_url", "sha256", "filename"];

    /// <summary>Every name listed, known or not.</summary>
    public List<string> Targets { get; private set; }
    public string DownloadUrl { get; private set; }
    public string Sha256 { get; private set; }
    /// <summary>As written; null when absent or not a string.</summary>
    public string Package { get; private set; }
    public List<LibraryDependency> LibraryDependencies { get; private set; }
    private JObject _raw;

    /// <summary>False for a package this build can't install, which leaves the
    /// artifact ignored like an unknown target.</summary>
    public bool Usable => Package is ModManagerConstants.PackageDll or ModManagerConstants.PackageZip;

    public bool Builds(string target) => Usable && Targets.Contains(target);

    /// <summary>Twin of manifest.py's install_name: the download URL's basename if
    /// it's safe and ends in .dll, else &lt;id&gt;.dll.</summary>
    public string InstallName(string modId)
    {
        string basename;
        try
        {
            basename = System.IO.Path.GetFileName(new Uri(DownloadUrl).LocalPath);
        }
        catch (Exception)
        {
            basename = "";
        }

        string safe;
        try
        {
            safe = ModPaths.SafeBasename(basename);
        }
        catch (ModManagerException)
        {
            safe = "";
        }

        return safe.ToLowerInvariant().EndsWith(".dll") ? safe : $"{modId}.dll";
    }

    public static ModArtifact FromJson(JToken token, string idForMsg)
    {
        var where = $"an artifact of '{idForMsg}'";
        if (token is not JObject d)
            throw new ModManagerException($"{where} isn't a JSON object.");
        if (d["targets"] is not JArray targets || targets.Count == 0 || targets.Any(t => t.Type != JTokenType.String))
            throw new ModManagerException($"{where} has no targets list.");
        if (d["download_url"]?.Type != JTokenType.String)
            throw new ModManagerException($"{where} has no download_url.");
        var libs = d["library_dependencies"] ?? new JArray();
        if (libs is not JArray || libs.Any(l => l.Type != JTokenType.Object))
            throw new ModManagerException($"{where} has a library_dependencies value that isn't a list of objects.");

        return new ModArtifact
        {
            Targets = targets.Select(t => (string)t).ToList(),
            DownloadUrl = (string)d["download_url"],
            Sha256 = ModManifest.Sha256Field(d, where),
            Package = d["package"]?.Type == JTokenType.String ? (string)d["package"] : null,
            LibraryDependencies = libs.Select(l => LibraryDependency.FromJson((JObject)l)).ToList(),
            _raw = d,
        };
    }

    /// <summary>The artifact as a record stores it: the manifest's own values,
    /// known keys in <see cref="KeyOrder"/>, then any others as they came.</summary>
    internal JObject ToRecordJson()
    {
        var copy = Reordered(_raw, KeyOrder);
        if (copy["library_dependencies"] is JArray libs)
            copy["library_dependencies"] = new JArray(libs.Select(l => Reordered((JObject)l, LibraryKeyOrder)));
        return copy;
    }

    private static JObject Reordered(JObject source, string[] order)
    {
        var copy = new JObject();
        foreach (var key in order.Where(source.ContainsKey))
            copy[key] = source[key].DeepClone();
        foreach (var prop in source.Properties().Where(p => !order.Contains(p.Name)))
            copy[prop.Name] = prop.Value.DeepClone();
        return copy;
    }
}

/// <summary>
/// The full per-version manifest, fetched on demand from
/// manifests/&lt;author&gt;/&lt;repo&gt;/&lt;version&gt;.json (or a latest*.json pointer).
/// </summary>
public class ModManifest
{
    public int ManifestVersion { get; set; }
    public string Id { get; set; }
    public string Name { get; set; }
    public string Version { get; set; }
    public string Author { get; set; }
    public string Description { get; set; }
    public bool ParityRequired { get; set; }
    public Dictionary<string, string> Dependencies { get; set; } = new();
    /// <summary>In manifest order, ignored ones included: a record keeps them all.</summary>
    public List<ModArtifact> Artifacts { get; set; } = new();
    public string SourceRepo { get; set; }
    /// <summary>See <see cref="ModScenesField"/>; null means every scene.</summary>
    public List<string> Scenes { get; set; }

    /// <summary>The build to install for target, or null: a target no usable
    /// artifact lists is one the mod doesn't support.</summary>
    public ModArtifact ArtifactFor(string target) => Artifacts.FirstOrDefault(a => a.Builds(target));

    /// <summary>Twin of manifest.py's _manifest_from_dict: null for an unsupported
    /// schema (warned and dropped), throws for a broken manifest.</summary>
    public static ModManifest FromJson(JObject d, string sourceRepo)
    {
        var idForMsg = (string)d["id"] ?? "?";

        if (d["manifest_version"] == null || d["manifest_version"].Type != JTokenType.Integer)
        {
            TavernLogger.Warn($"manifest for {idForMsg} has no integer manifest_version, skipped.");
            return null;
        }

        var mv = (int)d["manifest_version"];
        if (mv != ModManagerConstants.SupportedManifestMajor)
        {
            TavernLogger.Warn($"manifest for {idForMsg} is schema v{mv}, this build supports v{ModManagerConstants.SupportedManifestMajor}, skipped.");
            return null;
        }

        try
        {
            RequireField(d, "id");
            RequireField(d, "version");

            if (d["artifacts"] is not JArray rawArtifacts || rawArtifacts.Count == 0)
                throw new ModManagerException($"manifest for {idForMsg} has no artifacts.");
            var artifacts = rawArtifacts.Select(a => ModArtifact.FromJson(a, idForMsg)).ToList();
            // Over every artifact, ignored or not, so which build a target gets
            // never depends on which packages a reader knows.
            var twice = artifacts.SelectMany(a => a.Targets)
                .Where(ModManagerConstants.KnownTargets.Contains)
                .GroupBy(t => t)
                .FirstOrDefault(g => g.Count() > 1);
            if (twice != null)
                throw new ModManagerException($"manifest for {idForMsg} lists the '{twice.Key}' target twice.");

            var modId = (string)d["id"];
            var deps = new Dictionary<string, string>();
            if (d["dependencies"] is JObject depObj)
                foreach (var prop in depObj.Properties())
                    deps[prop.Name] = (string)prop.Value;

            return new ModManifest
            {
                ManifestVersion = mv,
                Id = modId,
                Name = (string)d["name"] ?? modId,
                Version = (string)d["version"],
                Author = (string)d["author"] ?? modId.Split('.')[0],
                Description = (string)d["description"] ?? "",
                ParityRequired = ModParityField.Read(d[ModParityField.Name], idForMsg),
                Dependencies = deps,
                Artifacts = artifacts,
                SourceRepo = sourceRepo,
                Scenes = ModScenesField.Read(d[ModScenesField.Name], idForMsg),
            };
        }
        catch (ModManagerException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new ModManagerException($"manifest for {idForMsg} is missing or has a malformed field: {e.Message}");
        }
    }

    private static void RequireField(JObject d, string field)
    {
        if (d[field] == null)
            throw new ModManagerException($"missing field '{field}'");
    }

    /// <summary>d["sha256"] lowercased, refusing anything but 64 hex
    /// characters. Same rule and message as manifest.py's _sha256_field.</summary>
    internal static string Sha256Field(JObject d, string where)
    {
        var token = d["sha256"];
        var value = token?.Type == JTokenType.String ? (string)token : null;
        if (value == null || value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new ModManagerException($"{where} has an invalid sha256 (expected 64 hex characters).");
        return value.ToLowerInvariant();
    }
}

/// <summary>One (id, major) row of repository.json: enough to pick a version;
/// nothing install-critical lives here.</summary>
public class ModSummary
{
    public string Id { get; private set; }
    public List<string> Versions { get; private set; }
    public string SourceRepo { get; private set; }
    public ModVersion Highest { get; private set; }
    public int Major => Highest.Major;

    public static ModSummary FromJson(JObject d, string modId, string sourceRepo)
    {
        if (d["versions"] is not JArray raw)
        {
            TavernLogger.Warn($"index entry for {modId} in {sourceRepo} has no versions list, skipped.");
            return null;
        }

        var versions = new List<string>();
        ModVersion? highest = null;
        foreach (var v in raw)
        {
            var s = v.ToString();
            if (ModVersion.TryParse(s, out var parsed))
            {
                versions.Add(s);
                if (highest == null || parsed > highest.Value) highest = parsed;
            }
            else
                TavernLogger.Warn($"index entry for {modId} lists a bad version '{s}', ignored.");
        }
        if (highest == null) return null;

        try
        {
            ModParityField.Read(d[ModParityField.Name], modId);
        }
        catch (ModManagerException)
        {
            // One malformed entry shouldn't cost the whole index. Skipped rather
            // than guessed at, same as an entry with no usable versions.
            TavernLogger.Warn($"index entry for {modId} in {sourceRepo} has no {ModParityField.Name} field, skipped.");
            return null;
        }

        return new ModSummary { Id = modId, Versions = versions, SourceRepo = sourceRepo, Highest = highest.Value };
    }
}
