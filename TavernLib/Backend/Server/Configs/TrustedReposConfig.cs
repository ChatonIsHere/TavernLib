using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using TavernLib.Backend.Mods;

namespace TavernLib.Backend.Server.Configs;

/// <summary>
/// mod_repos.json: the repos this server may pull from, shorthand -> base URL.
/// Only `modmanager addrepo` adds one, after fetching and validating its index;
/// a modlist naming a repo never does.
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
            .Where(u => !ModManagerConstants.IsDefaultRepo(u));
        return new List<string> { ModManagerConstants.DefaultRepo }.Concat(stored).ToList();
    }
}

public class TrustedReposConfig(string filePath) : ServerConfigFile<TrustedRepos>(filePath);
