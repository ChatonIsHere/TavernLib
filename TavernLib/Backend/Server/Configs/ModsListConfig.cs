using System.Collections.Generic;
using Newtonsoft.Json;

namespace TavernLib.Backend.Server.Configs;

// The file a launcher exports. Its `repos` and `untracked` keys are deliberately not read: reconcile pulls
// only from TrustedRepos, and untracked entries are filenames, not ids.
public class ModsList
{
    [JsonProperty("mods")] public List<string> Mods { get; set; } = new();

    public static (string Id, string PinnedVersion) ParseEntry(string entry)
    {
        var at = entry.IndexOf('@');
        return at < 0 ? (entry, null) : (entry.Substring(0, at), entry.Substring(at + 1));
    }
}

public class ModsListConfig(string filePath) : ServerConfigFile<ModsList>(filePath);
