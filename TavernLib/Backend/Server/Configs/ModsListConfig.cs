using System.Collections.Generic;
using Newtonsoft.Json;

namespace TavernLib.Backend.Server.Configs;

/// <summary>
/// A headless server's desired mod state - the mod ids a launcher GUI's
/// active-set otherwise manages, since a headless operator has no launcher
/// config. Each `mods` entry is an id, latest by default, or `id@version` to
/// pin an exact one.
///
/// This is also the canonical modlist format: a file exported from a launcher
/// (client or launcher-run server) is this exact shape, so it can become a
/// headless server's config directly, with no conversion step.
///
/// `repos` is informational only - each entry is a repo's "Author/Repo"
/// shorthand (see ModManagerConstants.RepoShorthand), naming where this
/// modlist's mods are expected to come from, for a human reading the file.
/// It is NEVER used to resolve or authorize a pull: reconcile only ever pulls
/// from TrustedRepos, the separately-maintained, add_repo-gated allow-list.
/// A repo named here that isn't already registered there simply can't be
/// resolved from - the same as any other mod whose source hasn't been added.
/// </summary>
public class ModsList
{
    [JsonProperty("schema")] public int Schema { get; set; } = 1;
    [JsonProperty("repos")] public List<string> Repos { get; set; } = new();
    [JsonProperty("mods")] public List<string> Mods { get; set; } = new();
}

/// <summary>One `mods` entry, split into its id and an optional pinned exact
/// version ("Acme.AdminTools@1.4.0" vs. latest-by-default "Acme.QuestGiver").</summary>
public readonly struct ModsListEntry
{
    public string Id { get; }
    public string PinnedVersion { get; }

    private ModsListEntry(string id, string pinnedVersion)
    {
        Id = id;
        PinnedVersion = pinnedVersion;
    }

    public static ModsListEntry Parse(string entry)
    {
        var at = entry.IndexOf('@');
        return at < 0
            ? new ModsListEntry(entry, null)
            : new ModsListEntry(entry.Substring(0, at), entry.Substring(at + 1));
    }
}

public class ModsListConfig(string filePath) : ServerConfigFile<ModsList>(filePath);
