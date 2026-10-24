using System.Collections.Generic;
using Newtonsoft.Json;

namespace TavernLib.Backend.Auth;

/// <summary>
/// last_rejection.json, written by JoinRejectionRecorder for the launcher;
/// `missing` is the deny reason's ModParity.RequiredMod list.
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
