using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using TavernLib.Backend.Mods;

namespace TavernLib.Backend.Server.Configs;

// mod_repos.json. Only `modmanager addrepo` adds a repo; a modlist naming one never does.
public class TrustedRepos
{
    [JsonProperty("schema")] public int Schema { get; set; } = 1;
    [JsonProperty("repos")] public Dictionary<string, string> Repos { get; set; } = new();

    public List<string> EffectiveRepoUrls() =>
        [ModManagerConstants.DefaultRepo, .. Repos?.Values.Where(u => !ModManagerConstants.IsDefaultRepo(u)) ?? []];
}

public class TrustedReposConfig(string filePath) : ServerConfigFile<TrustedRepos>(filePath);
