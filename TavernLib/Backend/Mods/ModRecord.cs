using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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

    /// <summary>Required on disk: <see cref="Read"/> refuses a record without a
    /// JSON boolean here rather than guess whether clients must match it.</summary>
    [JsonProperty("parity_required")]
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

    /// <summary>The scenes this mod works in (see <see cref="ModScenesField"/>).
    /// Omitted when null, meaning every scene - the same as the launcher's
    /// record, and what every record written before the field existed already
    /// says. Like Files, kept out of the handshake fingerprint.</summary>
    [JsonProperty("scenes", NullValueHandling = NullValueHandling.Ignore)]
    public List<string> Scenes { get; set; }

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
        Scenes = mod.Scenes?.ToList(),
    };

    public void WriteTo(string path)
    {
        File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
    }

    private static readonly HashSet<string> WarnedUnreadable = new();

    // Ownership is decided by the predicate in Read alone, so a malformed field
    // it doesn't look at costs that field, not the whole record.
    private static readonly JsonSerializer Lenient = JsonSerializer.Create(new JsonSerializerSettings
    {
        Error = (_, e) => e.ErrorContext.Handled = true,
    });

    public static ModRecord Read(string gameDir, string modId) => Read(gameDir, modId, out _);

    /// <summary>Twin of install.py:_read_mod_record + _owned_mod_record, the "is
    /// this folder ours" predicate: the first of manifest.json and
    /// manifest.disabled.json holding an object with a truthy id is the record,
    /// and it's ours only if its parity_required is a JSON boolean. Null when it
    /// isn't. <paramref name="enabled"/> is whether the record came from
    /// manifest.json, as list_installed_mods reports it.</summary>
    public static ModRecord Read(string gameDir, string modId, out bool enabled)
    {
        enabled = true;
        foreach (var path in new[] { ModPaths.ModRecordPath(gameDir, modId), ModPaths.DisabledRecordPath(gameDir, modId) })
        {
            if (ParseObject(path) is { } obj && Truthy(obj["id"]))
            {
                if (obj[ModParityField.Name]?.Type != JTokenType.Boolean)
                {
                    lock (WarnedUnreadable)
                    {
                        if (WarnedUnreadable.Add(path))
                            TavernLogger.Warn($"mod record '{path}' has no usable {ModParityField.Name}; treating the mod as not installed until reconcile rewrites it.");
                    }
                    return null;
                }
                var record = obj.ToObject<ModRecord>(Lenient);
                if (string.IsNullOrEmpty(record.Id)) record.Id = obj["id"].ToString(Formatting.None);
                return record;
            }
            enabled = false;
        }
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
            using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None };
            var token = JToken.ReadFrom(reader);
            return reader.Read() ? null : token as JObject;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Python truthiness, which is what install.py's rec.get("id") tests.
    private static bool Truthy(JToken t) => t?.Type switch
    {
        null or JTokenType.Null => false,
        JTokenType.Boolean => (bool)t,
        JTokenType.String => ((string)t).Length > 0,
        JTokenType.Integer or JTokenType.Float =>
            ((JValue)t).Value is not IConvertible n || n.ToDouble(CultureInfo.InvariantCulture) != 0,
        _ => t is not JContainer c || c.Count > 0,
    };
}
