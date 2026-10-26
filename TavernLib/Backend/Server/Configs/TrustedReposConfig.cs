using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using TavernLib.Backend.Mods;

namespace TavernLib.Backend.Server.Configs;

/// <summary>mod_repos.json: the repos this server may pull from, shorthand -> base URL. Only `modmanager
/// addrepo` adds one, after validating its index; a modlist naming a repo never does.</summary>
public class TrustedRepos
{
    [JsonProperty("schema")] public int Schema { get; set; } = 1;
    [JsonProperty("repos")] public Dictionary<string, string> Repos { get; set; } = new();

    // The default repo always comes first and can't be removed.
    public List<string> EffectiveRepoUrls() =>
        [ModManagerConstants.DefaultRepo, .. Repos?.Values.Where(u => !ModManagerConstants.IsDefaultRepo(u)) ?? []];
}

public class TrustedReposConfig(string filePath) : ServerConfigFile<TrustedRepos>(filePath);
