using System.Collections.Generic;
using Newtonsoft.Json;

namespace TavernLib.Backend.Server.Configs;

// The file a launcher exports. Its `repos` and `untracked` keys are deliberately not read: reconcile pulls
// only from TrustedRepos, and untracked entries are filenames, not ids.
public class ModsList
{
    [JsonProperty("mods")] public List<string> Mods { get; set; } = new();

    // A bare "id@" means latest, as in pins.py's _parse_pin_entry.
    public static (string Id, string PinnedVersion) ParseEntry(string entry)
    {
        var at = entry.IndexOf('@');
        if (at < 0) return (entry, null);
        var version = entry.Substring(at + 1);
        return (entry.Substring(0, at), version.Length == 0 ? null : version);
    }
}

public class ModsListConfig(string filePath) : ServerConfigFile<ModsList>(filePath);
