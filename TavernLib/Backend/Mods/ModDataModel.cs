using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TavernLib.Backend.Mods;

// A pinned, exact-version file a mod needs in UserLibs/: not a mod, so no id and no version range.
public class LibraryDependency
{
    public string Name { get; private set; }
    public string DownloadUrl { get; private set; }
    public string Sha256 { get; private set; }
    public string Filename { get; private set; }

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

/// <summary>One build in a manifest's artifacts. Twin of manifest.py's _artifact_from_dict: every entry is
/// checked, but only one with a known package is ever installed, and only for a target this build
/// knows.</summary>
public class ModArtifact
{
    // The compiled manifest's key order, which a record keeps.
    private static readonly string[] KeyOrder = ["targets", "download_url", "sha256", "package", "library_dependencies"];
    private static readonly string[] LibraryKeyOrder = ["name", "download_url", "sha256", "filename"];

    public List<string> Targets { get; private set; } // every name listed, known or not
    public string DownloadUrl { get; private set; }
    public string Sha256 { get; private set; }
    public string Package { get; private set; } // null when absent or not a string
    public List<LibraryDependency> LibraryDependencies { get; private set; }
    private JObject _raw;

    // An unknown package leaves the artifact ignored, like an unknown target.
    public bool Usable => Package is "dll" or "zip";

    public bool Builds(string target) => Usable && Targets.Contains(target);

    // Twin of manifest.py's install_name: the download URL's basename if it's safe and ends in .dll, else <id>.dll.
    public string InstallName(string modId)
    {
        string name;
        try
        {
            name = ModPaths.SafeBasename(Path.GetFileName(new Uri(DownloadUrl).LocalPath));
        }
        catch (Exception)
        {
            name = "";
        }
        return name.ToLowerInvariant().EndsWith(".dll") ? name : $"{modId}.dll";
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

    // The manifest's own values, known keys in KeyOrder, then any others as they came.
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

// A per-version manifest from manifests/<author>/<repo>/<version>.json, or a latest*.json pointer.
public class ModManifest
{
    internal const int SupportedMajor = 2;

    public string Id { get; private set; }
    public string Name { get; private set; }
    public string Version { get; private set; }
    public string Author { get; private set; }
    public string Description { get; private set; }
    public bool ParityRequired { get; private set; }
    public Dictionary<string, string> Dependencies { get; private set; }
    public List<ModArtifact> Artifacts { get; private set; } // in manifest order, ignored ones included: a record keeps them all
    public string SourceRepo { get; private set; }
    public List<string> Scenes { get; private set; } // null means every scene

    // A target no usable artifact lists is one the mod doesn't support.
    public ModArtifact ArtifactFor(string target) => Artifacts.FirstOrDefault(a => a.Builds(target));

    // Whether a mod with these scenes works in scene; an unknown scene (null) never rules one out.
    public static bool RunsIn(List<string> scenes, string scene) => scenes == null || scene == null || scenes.Contains(scene);

    /// <summary>Twin of manifest.py's _manifest_from_dict: null for an unsupported schema (warned and
    /// dropped), throws for a broken manifest.</summary>
    public static ModManifest FromJson(JObject d, string sourceRepo)
    {
        var idForMsg = (string)d["id"] ?? "?";

        if (d["manifest_version"] == null || d["manifest_version"].Type != JTokenType.Integer)
        {
            TavernLogger.Warn($"manifest for {idForMsg} has no integer manifest_version, skipped.");
            return null;
        }
        var mv = (int)d["manifest_version"];
        if (mv != SupportedMajor)
        {
            TavernLogger.Warn($"manifest for {idForMsg} is schema v{mv}, this build supports v{SupportedMajor}, skipped.");
            return null;
        }

        try
        {
            foreach (var field in new[] { "id", "version" })
                if (d[field] == null)
                    throw new ModManagerException($"missing field '{field}'");

            if (d["artifacts"] is not JArray rawArtifacts || rawArtifacts.Count == 0)
                throw new ModManagerException($"manifest for {idForMsg} has no artifacts.");
            var artifacts = rawArtifacts.Select(a => ModArtifact.FromJson(a, idForMsg)).ToList();
            // Over every artifact, ignored or not, so which build a target gets never depends on which
            // packages a reader knows.
            var twice = artifacts.SelectMany(a => a.Targets)
                .Where(ModManagerConstants.KnownTargets.Contains)
                .GroupBy(t => t)
                .FirstOrDefault(g => g.Count() > 1);
            if (twice != null)
                throw new ModManagerException($"manifest for {idForMsg} lists the '{twice.Key}' target twice.");

            var modId = (string)d["id"];
            var deps = d["dependencies"] is JObject depObj
                ? depObj.Properties().ToDictionary(p => p.Name, p => (string)p.Value)
                : new Dictionary<string, string>();
            return new ModManifest
            {
                Id = modId,
                Name = (string)d["name"] ?? modId,
                Version = (string)d["version"],
                Author = (string)d["author"] ?? modId.Split('.')[0],
                Description = (string)d["description"] ?? "",
                ParityRequired = ReadParity(d["parity_required"], idForMsg),
                Dependencies = deps,
                Artifacts = artifacts,
                SourceRepo = sourceRepo,
                Scenes = ReadScenes(d["scenes"], idForMsg),
            };
        }
        catch (Exception e) when (e is not ModManagerException)
        {
            throw new ModManagerException($"manifest for {idForMsg} is missing or has a malformed field: {e.Message}");
        }
    }

    // Twin of manifest.py's parity_required: mandatory and strictly a JSON boolean, since guessing is what
    // would let a required mod pass as optional.
    private static bool ReadParity(JToken value, string idForMsg) =>
        value?.Type == JTokenType.Boolean
            ? (bool)value
            : throw new ModManagerException(
                $"'{idForMsg}' has no usable parity_required field. It's required: true if a "
                + "client joining a server running this mod must have this exact "
                + "version, false if the server shouldn't block the join over it.");

    // Twin of manifest.py's mod_scenes: absent or null means every scene; otherwise a non-empty list of
    // non-empty strings, unknown names kept so a scene added later doesn't break older readers.
    private static List<string> ReadScenes(JToken value, string idForMsg)
    {
        if (value == null || value.Type == JTokenType.Null) return null;
        if (value is not JArray array || array.Count == 0
            || array.Any(t => t.Type != JTokenType.String || string.IsNullOrEmpty((string)t)))
            throw new ModManagerException(
                $"'{idForMsg}' has a scenes value of {value.ToString(Formatting.None)}. Leave it out "
                + "for a mod that works in every scene, or list the ones it works in, out of normal, quest, tutorial.");
        return array.Select(t => (string)t).Distinct().ToList();
    }

    // Lowercased, refusing anything but 64 hex characters. Same rule and message as manifest.py's _sha256_field.
    internal static string Sha256Field(JObject d, string where)
    {
        var value = d["sha256"]?.Type == JTokenType.String ? (string)d["sha256"] : null;
        if (value == null || value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new ModManagerException($"{where} has an invalid sha256 (expected 64 hex characters).");
        return value.ToLowerInvariant();
    }
}

// One (id, major) row of repository.json: enough to pick a version; nothing install-critical lives here.
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
        foreach (var s in raw.Select(v => v.ToString()))
        {
            if (!ModVersion.TryParse(s, out var parsed))
            {
                TavernLogger.Warn($"index entry for {modId} lists a bad version '{s}', ignored.");
                continue;
            }
            versions.Add(s);
            if (highest == null || parsed > highest.Value) highest = parsed;
        }
        if (highest == null) return null;

        // One malformed entry shouldn't cost the whole index: skipped rather than guessed at.
        if (d["parity_required"]?.Type != JTokenType.Boolean)
        {
            TavernLogger.Warn($"index entry for {modId} in {sourceRepo} has no parity_required field, skipped.");
            return null;
        }

        return new ModSummary { Id = modId, Versions = versions, SourceRepo = sourceRepo, Highest = highest.Value };
    }
}
