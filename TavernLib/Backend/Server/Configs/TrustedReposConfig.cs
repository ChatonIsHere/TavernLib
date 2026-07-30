using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using TavernLib.Backend.Mods;

namespace TavernLib.Backend.Server.Configs;

/// <summary>
/// The locally-maintained set of repos this server is actually allowed to pull
/// mods from - keyed by their "Author/Repo" shorthand, mapping to the real base
/// URL. This is deliberately separate from the modlist file (see ModsList):
/// only ModsCommandModule's `addrepo` command adds an entry here, after a live
/// fetch-and-validate check of the URL, exactly mirroring modmanager.py's
/// add_repo. A shorthand appearing in a modlist's `repos` field is never
/// enough on its own to add or use a source - it's looked up here, and if it
/// isn't registered, that source is simply unavailable.
/// </summary>
public class TrustedRepos
{
    [JsonProperty("schema")] public int Schema { get; set; } = 1;
    [JsonProperty("repos")] public Dictionary<string, string> Repos { get; set; } = new();

    /// <summary>Every pullable base URL, DEFAULT_REPO always included (seeded,
    /// non-removable) - what ModRepoClient.FetchIndexes actually iterates.</summary>
    public List<string> EffectiveRepoUrls()
    {
        var stored = (Repos ?? new Dictionary<string, string>()).Values
            .Where(u => (u ?? "").TrimEnd('/') != ModManagerConstants.DefaultRepo.TrimEnd('/'));
        return new List<string> { ModManagerConstants.DefaultRepo }.Concat(stored).ToList();
    }
}

public class TrustedReposConfig(string filePath) : ServerConfigFile<TrustedRepos>(filePath);
