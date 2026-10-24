using System.Collections.Generic;
using Newtonsoft.Json;

namespace TavernLib.Backend.Server.Configs;

/// <summary>
/// A headless server's modlist: the same file a launcher exports. Each `mods`
/// entry is an id (latest) or id@version. Its `repos` and `untracked` keys are
/// informational and deliberately not read: reconcile pulls only from
/// TrustedRepos, and untracked entries are filenames, not ids.
/// </summary>
public class ModsList
{
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
