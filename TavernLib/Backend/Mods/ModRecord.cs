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
/// <summary>Reads a JSON boolean and nothing else - no coercion from a string
/// or a number, which Json.NET does by default. Used where the launcher's
/// Python reader is equally strict and the two must agree on whether a record
/// is readable at all.</summary>
public class StrictBoolConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(bool);

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType != JsonToken.Boolean)
            throw new JsonSerializationException($"Expected a boolean, got {reader.TokenType}.");
        return reader.Value;
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) =>
        writer.WriteValue((bool)value);
}

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
    /// Required, with no default: every record we write carries it, so one
    /// without it is corrupt and can't be read - ReadFrom returns null and the
    /// mod counts as not installed. Assuming a value would be the one mistake
    /// that matters here, since it decides whether a joining client is obliged
    /// to match. Reading as not-installed is recoverable: the next reconcile
    /// reinstalls it with a full record.
    ///
    /// Strictly a JSON boolean, via the converter below: Required.Always only
    /// checks that the field is PRESENT, and Json.NET would otherwise read
    /// "false" or 0 as a bool where modmanager.py's isinstance check rejects
    /// both. The two implementations have to call the same record unreadable,
    /// or they disagree about which mods a Mods/ folder even contains.</summary>
    [JsonProperty("parity_required", Required = Required.Always)]
    [JsonConverter(typeof(StrictBoolConverter))]
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

    /// <summary>{relative path (forward slashes): sha256} for everything the
    /// installer assembled into this mod's folder, hashed out of staging right
    /// before this record was written into it (so the record is never part of
    /// its own map). What <see cref="ModInstaller.VerifyModFiles"/> checks, and
    /// the same map modmanager.py's verify_mod_files reads - a mod installed
    /// here shows up as Damaged in the launcher's Mod Manager, and one installed
    /// there is checked by reconcile on this side.
    ///
    /// Null (absent), never an empty object, when there's nothing to check: a
    /// record written before this field existed. Deserialization leaves it null
    /// rather than defaulting to an empty map for exactly that reason - "no
    /// evidence" has to stay distinguishable from "evidence of nothing", since
    /// the second would call every file legitimately missing. Both readers treat
    /// absent as "no damage detection", never as damaged.
    ///
    /// Kept out of the handshake fingerprint deliberately - see
    /// <see cref="ModHandshake"/>, whose hash must stay byte-identical to the
    /// launcher's.</summary>
    [JsonProperty("files", NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, string> Files { get; set; }

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

    private static readonly HashSet<string> WarnedUnreadable = new();

    private static ModRecord ReadFrom(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonConvert.DeserializeObject<ModRecord>(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            // Once per path, not per read - the listers run this on every
            // snapshot. But never silently: an unreadable record drops its mod
            // from the managed set (so parity stops being enforced for it) while
            // MelonLoader goes on loading the folder, and an operator needs to
            // be able to see why.
            lock (WarnedUnreadable)
            {
                if (WarnedUnreadable.Add(path))
                    TavernLogger.Warn($"mod record '{path}' can't be read ({e.Message}); treating the mod as not installed until reconcile rewrites it.");
            }
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
