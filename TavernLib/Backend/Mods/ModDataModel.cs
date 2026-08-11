using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using TavernLib;

namespace TavernLib.Backend.Mods;

/// <summary>
/// A pinned, exact-version support assembly a mod links against, installed to
/// UserLibs/. No id, no version-range resolution, no separate manifest - just
/// "here's the exact file", distinct from <see cref="ModManifest.Dependencies"/>,
/// which is for depending on other mods.
/// </summary>
public class LibraryDependency
{
    public string Name { get; set; }
    public string DownloadUrl { get; set; }
    public string Sha256 { get; set; }
    public string Filename { get; set; }

    public static LibraryDependency FromJson(JObject d)
    {
        foreach (var key in new[] { "name", "download_url", "sha256", "filename" })
            if (d[key] == null)
                throw new ModManagerException($"library_dependencies entry is missing '{key}'.");
        return new LibraryDependency
        {
            Name = (string)d["name"],
            DownloadUrl = (string)d["download_url"],
            Sha256 = ((string)d["sha256"]).ToLowerInvariant(),
            Filename = (string)d["filename"],
        };
    }
}

/// <summary>
/// Reads a <c>parity_required</c> value out of raw JSON: whether a client
/// joining a server that runs this mod must match its exact version.
/// <para>
/// True for mods whose two halves are one system - a voice codec,
/// a network protocol, anything where both sides exchange data they have to
/// agree on. False for mods whose halves work independently, where the server
/// won't block a join and the client is offered the mod rather than obliged to
/// have it.
/// </para>
/// Mandatory, with no default: every shape that carries this field is written
/// by tooling that knows about it, so a missing one means the data is
/// malformed, and guessing on its behalf is exactly what would let a required
/// mod be silently treated as optional. Callers decide what a failure means - a
/// manifest becomes unresolvable, an index entry is skipped.
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
    public bool ClientSide { get; set; }
    public bool ServerSide { get; set; }
    public bool ParityRequired { get; set; }
    public Dictionary<string, string> Dependencies { get; set; } = new();
    public List<LibraryDependency> LibraryDependencies { get; set; } = new();
    public string DownloadUrl { get; set; }
    public string Sha256 { get; set; }
    public string SourceRepo { get; set; }
    public string Package { get; set; } = "dll";

    /// <summary>
    /// The basename this mod's single .dll lands under in Mods/&lt;id&gt;/, taken from
    /// DownloadUrl. Falls back to "&lt;id&gt;.dll" if the URL's basename isn't a safe,
    /// ".dll"-suffixed name - only meaningful for Package == "dll".
    /// </summary>
    public string InstallName()
    {
        string basename;
        try
        {
            var uri = new Uri(DownloadUrl);
            basename = System.IO.Path.GetFileName(uri.LocalPath);
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

        return safe.ToLowerInvariant().EndsWith(".dll") ? safe : $"{Id}.dll";
    }

    /// <summary>
    /// Builds a ModManifest from a parsed manifest object. Returns null (caller
    /// warns and drops it) if manifest_version's major isn't supported. Throws
    /// only for a manifest that claims a supported version but is structurally
    /// broken (a required field missing).
    /// </summary>
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

        var package = ((string)d["package"] ?? "dll").ToLowerInvariant();
        if (package != "dll" && package != "zip")
        {
            TavernLogger.Warn($"manifest for {idForMsg} has unknown package type '{package}' (expected 'dll' or 'zip'), skipped.");
            return null;
        }

        try
        {
            RequireField(d, "id");
            RequireField(d, "version");
            RequireField(d, "client_side");
            RequireField(d, "server_side");
            RequireField(d, "download_url");
            RequireField(d, "sha256");

            var modId = (string)d["id"];
            var libs = ((JArray)d["library_dependencies"] ?? new JArray())
                .Select(x => LibraryDependency.FromJson((JObject)x))
                .ToList();
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
                ClientSide = (bool)d["client_side"],
                ServerSide = (bool)d["server_side"],
                ParityRequired = ModParityField.Read(d[ModParityField.Name], idForMsg),
                Dependencies = deps,
                LibraryDependencies = libs,
                DownloadUrl = (string)d["download_url"],
                Sha256 = ((string)d["sha256"]).ToLowerInvariant(),
                SourceRepo = sourceRepo,
                Package = package,
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
}

/// <summary>
/// One (id, major) row from a repository.json index - enough to browse and pick
/// a version; install-critical data (download_url/sha256/dependencies) lives
/// only in the per-version manifest.
/// </summary>
public class ModSummary
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Author { get; set; }
    public string Description { get; set; }
    public bool ClientSide { get; set; }
    public bool ServerSide { get; set; }
    public bool ParityRequired { get; set; }
    public List<string> Versions { get; set; } = new();
    public string SourceRepo { get; set; }

    public string Highest() => Versions.OrderByDescending(ModVersion.Parse).First();
    public int Major => ModVersion.Parse(Highest()).Major;

    public static ModSummary FromJson(JObject d, string modId, string sourceRepo)
    {
        if (d["versions"] is not JArray raw)
        {
            TavernLogger.Warn($"index entry for {modId} in {sourceRepo} has no versions list, skipped.");
            return null;
        }

        var versions = new List<string>();
        foreach (var v in raw)
        {
            var s = v.ToString();
            if (ModVersion.TryParse(s, out _))
                versions.Add(s);
            else
                TavernLogger.Warn($"index entry for {modId} lists a bad version '{s}', ignored.");
        }
        if (versions.Count == 0) return null;

        bool parityRequired;
        try
        {
            parityRequired = ModParityField.Read(d[ModParityField.Name], modId);
        }
        catch (ModManagerException)
        {
            // One malformed entry shouldn't cost the whole index. Skipped rather
            // than guessed at, same as an entry with no usable versions.
            TavernLogger.Warn($"index entry for {modId} in {sourceRepo} has no {ModParityField.Name} field, skipped.");
            return null;
        }

        return new ModSummary
        {
            Id = modId,
            Name = (string)d["name"] ?? modId,
            Author = (string)d["author"] ?? modId.Split('.')[0],
            Description = (string)d["description"] ?? "",
            ClientSide = (bool?)d["client_side"] ?? false,
            ServerSide = (bool?)d["server_side"] ?? false,
            ParityRequired = parityRequired,
            Versions = versions,
            SourceRepo = sourceRepo,
        };
    }
}
