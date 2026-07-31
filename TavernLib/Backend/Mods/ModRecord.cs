using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace TavernLib.Backend.Mods;

/// <summary>
/// The record written to Mods/&lt;id&gt;/manifest.json: the fetched manifest verbatim
/// (so status/uninstall need no re-fetch) plus install fields. Kept a plain JSON
/// object on purpose - it's also MelonLoader's folder marker. Keep this shape
/// stable; it's read by both this installer and the Python launcher's
/// modmanager.py (same on-disk convention, two implementations).
/// </summary>
public class ModRecord
{
    [JsonProperty("manifest_version")] public int ManifestVersion { get; set; }
    [JsonProperty("id")] public string Id { get; set; }
    [JsonProperty("name")] public string Name { get; set; }
    [JsonProperty("version")] public string Version { get; set; }
    [JsonProperty("author")] public string Author { get; set; }
    [JsonProperty("description")] public string Description { get; set; }
    [JsonProperty("client_side")] public bool ClientSide { get; set; }
    [JsonProperty("server_side")] public bool ServerSide { get; set; }

    /// <summary>Whether a joining client must match this mod's exact version
    /// (see <see cref="ModParityField"/>). Recorded on disk so the handshake can
    /// report it without re-fetching the manifest.
    ///
    /// Required, with no default: a record written before this field existed
    /// can't be read, so ReadFrom returns null and the mod counts as not
    /// installed. Assuming a value would be the one mistake that matters here,
    /// since it decides whether a joining client is obliged to match. Skipping
    /// is recoverable - the next reconcile reinstalls it with a full
    /// record.</summary>
    [JsonProperty("parity_required", Required = Required.Always)]
    public bool ParityRequired { get; set; }
    [JsonProperty("dependencies")] public Dictionary<string, string> Dependencies { get; set; } = new();
    [JsonProperty("library_dependencies")] public List<LibraryRecordEntry> LibraryDependencies { get; set; } = new();
    [JsonProperty("download_url")] public string DownloadUrl { get; set; }
    [JsonProperty("sha256")] public string Sha256 { get; set; }
    [JsonProperty("source_repo")] public string SourceRepo { get; set; }
    [JsonProperty("package")] public string Package { get; set; } = "dll";

    /// <summary>The libraries this mod pins, by filename - uninstall/cleanup
    /// reference-counts these so an orphaned UserLibs/ file is removed while one
    /// another installed mod still lists stays put.</summary>
    [JsonProperty("libraries")] public List<string> Libraries { get; set; } = new();

    public class LibraryRecordEntry
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("download_url")] public string DownloadUrl { get; set; }
        [JsonProperty("sha256")] public string Sha256 { get; set; }
        [JsonProperty("filename")] public string Filename { get; set; }
    }

    public static ModRecord FromManifest(ModManifest mod) => new()
    {
        ManifestVersion = mod.ManifestVersion,
        Id = mod.Id,
        Name = mod.Name,
        Version = mod.Version,
        Author = mod.Author,
        Description = mod.Description,
        ClientSide = mod.ClientSide,
        ServerSide = mod.ServerSide,
        ParityRequired = mod.ParityRequired,
        Dependencies = new Dictionary<string, string>(mod.Dependencies ?? new()),
        LibraryDependencies = (mod.LibraryDependencies ?? new List<LibraryDependency>())
            .Select(l => new LibraryRecordEntry { Name = l.Name, DownloadUrl = l.DownloadUrl, Sha256 = l.Sha256, Filename = l.Filename })
            .ToList(),
        DownloadUrl = mod.DownloadUrl,
        Sha256 = mod.Sha256,
        SourceRepo = mod.SourceRepo,
        Package = mod.Package,
        Libraries = (mod.LibraryDependencies ?? new List<LibraryDependency>())
            .Select(l => ModPaths.SafeBasename(l.Filename))
            .ToList(),
    };

    public void WriteTo(string path)
    {
        File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
    }

    private static ModRecord ReadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonConvert.DeserializeObject<ModRecord>(File.ReadAllText(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The install record for a mod, from whichever state it's in: the enabled
    /// folder record, then the disabled one. Only accepted if it carries an id.
    /// Returns null if neither has it.
    /// </summary>
    public static ModRecord Read(string gameDir, string modId)
    {
        foreach (var path in new[] { ModPaths.ModRecordPath(gameDir, modId), ModPaths.DisabledRecordPath(gameDir, modId) })
        {
            var rec = ReadFrom(path);
            if (rec != null && !string.IsNullOrEmpty(rec.Id)) return rec;
        }
        return null;
    }
}
