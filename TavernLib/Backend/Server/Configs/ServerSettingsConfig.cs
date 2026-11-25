using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TavernLib.Backend.Server.Configs;

public class ServerSettings
{
    // The Windows launcher's default too.
    public const int DefaultMaxPlayers = 24;

    [JsonProperty(PropertyName = "name")] public string Name { get; private set; } = "My Tavern Server";
    [JsonProperty(PropertyName = "password_hash")] public string PasswordHash { get; internal set; } = "";
    // A password typed in by hand. Reading the file swaps it for password_hash.
    [JsonProperty(PropertyName = "password", NullValueHandling = NullValueHandling.Ignore)] public string Password { get; internal set; }
    [JsonProperty(PropertyName = "whitelist_enabled")] public bool WhitelistEnabled { get; private set; }
    // On by default, as in the Windows launcher: at most 5 new names from one address.
    [JsonProperty(PropertyName = "enforce_ip_limit")] public bool EnforceIpLimit { get; private set; } = true;
    [JsonProperty(PropertyName = "community_listed")] public bool CommunityListed { get; private set; }
    [JsonProperty(PropertyName = "max_players")] public int MaxPlayers { get; private set; } = DefaultMaxPlayers;
    [JsonProperty(PropertyName = "community_listing_token")] public string CommunityListingToken { get; internal set; } = "";
    [JsonProperty(PropertyName = "public_hostname")] public string PublicHostname { get; private set; }
    [JsonProperty(PropertyName = "quest_scene")] public bool QuestScene { get; private set; }
    [JsonProperty(PropertyName = "tutorial_scene")] public bool TutorialScene { get; private set; }
    [JsonProperty(PropertyName = "region")] public string Region { get; private set; } = "unknown";
    [JsonProperty(PropertyName = "garbage_collection_timer")] public float GarbageCollectionTimer { get; private set; } = 30f;
    [JsonProperty(PropertyName = "server_backup_time")] public float ServerBackupTime { get; private set; }
    [JsonProperty(PropertyName = "server_backup_max_backups")] public int ServerBackupMaxBackups { get; private set; }
    [JsonProperty(PropertyName = "console_owner_local_only")] public bool ConsoleOwnerLocalOnly { get; private set; }
    [JsonExtensionData] private IDictionary<string, JToken> Extra { get; set; }

    // 0 or less (an older file's default) means the default.
    [JsonIgnore] public int PlayerLimit => MaxPlayers > 0 ? MaxPlayers : DefaultMaxPlayers;
}

public class ServerSettingsConfig(string filePath) : ServerConfigFile<ServerSettings>(filePath)
{
    private static readonly Regex Sha256Hex = new("^[0-9a-f]{64}$");

    // password_hash is sha256(sha256(password)), as TavernLauncher writes it and as joins check it.
    // A plain "password" becomes that hash and leaves the file; an empty one is just dropped. A
    // password_hash that isn't a hash was typed in as the password itself (AMP's password field
    // does this), so it gets hashed too.
    protected override bool Tidy(ServerSettings settings)
    {
        var changed = false;
        if (settings.Password != null)
        {
            if (!string.IsNullOrEmpty(settings.Password))
            {
                settings.PasswordHash = Hash(settings.Password);
                TavernLogger.Msg("Hashed the password in server_settings.json");
            }
            settings.Password = null;
            changed = true;
        }
        else if (!string.IsNullOrEmpty(settings.PasswordHash) && !Sha256Hex.IsMatch(settings.PasswordHash))
        {
            settings.PasswordHash = Hash(settings.PasswordHash);
            changed = true;
            TavernLogger.Msg("Hashed the plain password in server_settings.json's password_hash");
        }
        return changed;
    }

    private static string Hash(string password) => BackendUtils.HashDigest(BackendUtils.HashDigest(password));

    public override void ReadFromFile()
    {
        base.ReadFromFile();
        if (!string.IsNullOrWhiteSpace(LastRead.CommunityListingToken)) return;
        
        LastRead.CommunityListingToken = BackendUtils.TokenUrlSafe(24);
        WriteToFile();
    }
}