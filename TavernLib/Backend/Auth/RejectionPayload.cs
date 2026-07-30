using System.Collections.Generic;
using Newtonsoft.Json;

namespace TavernLib.Backend.Auth;

/// <summary>
/// The on-disk shape of last_rejection.json, written by this client's own
/// game process right before it exits after a mod-mismatch denial
/// (JoinRejectionRecorder), read by the launcher afterward. `missing` reuses
/// ModParity.RequiredMod's {id, version, source_repo} shape verbatim - the
/// same entries already embedded in the deny reason, just persisted to disk
/// instead of only logged.
/// </summary>
public class RejectionPayload
{
    [JsonProperty("schema")] public int Schema { get; set; } = 1;
    [JsonProperty("written_at")] public string WrittenAt { get; set; }
    [JsonProperty("server")] public RejectionServer Server { get; set; }
    [JsonProperty("server_name")] public string ServerName { get; set; }
    [JsonProperty("missing")] public List<ModParity.RequiredMod> Missing { get; set; }
}

public class RejectionServer
{
    [JsonProperty("host")] public string Host { get; set; }
    [JsonProperty("game_port")] public int GamePort { get; set; }
}
