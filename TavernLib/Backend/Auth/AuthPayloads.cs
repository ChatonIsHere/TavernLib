using System.Collections.Generic;
using Newtonsoft.Json;
using TavernLib.Backend.Mods;

namespace TavernLib.Backend.Auth;

internal static class AuthPayloads
{
    public struct PingRequest
    {
        [JsonProperty(PropertyName = "ping")] private bool Ping { get; set; }
    }


    public struct PingResponse(string serverName, bool passwordRequired, bool whitelistEnabled, int gamePort,
        string modsHash, int modsCount)
    {
        [JsonProperty(PropertyName = "status")] private string Pong => "pong";
        [JsonProperty(PropertyName = "server_name")] private string ServerName { get; set; } = serverName;
        [JsonProperty(PropertyName = "password_required")] private bool PasswordRequired { get; set; } = passwordRequired;
        [JsonProperty(PropertyName = "whitelist_enabled")] private bool WhitelistEnabled { get; set; } = whitelistEnabled;
        [JsonProperty(PropertyName = "game_port")] private int GamePort { get; set; } = gamePort;
        [JsonProperty(PropertyName = "mods_hash")] private string ModsHash { get; set; } = modsHash;
        [JsonProperty(PropertyName = "mods_count")] private int ModsCount { get; set; } = modsCount;
    }


    /// <summary>Sent length-prefixed, not as a plain single-recv reply - see
    /// AuthManager.WriteFramedResponse. Everything else on this port is small
    /// enough for the existing single recv/send; this is the one payload that
    /// can genuinely outgrow it.</summary>
    public readonly struct ModsListResponse(List<ModHandshake.Entry> mods)
    {
        [JsonProperty(PropertyName = "status")] private string Status => "ok";
        [JsonProperty(PropertyName = "mods")] private List<ModHandshake.Entry> Mods => mods;
    }


    public struct AuthenticateRequest
    {
        [JsonProperty(PropertyName = "username")] public string Username { get; private set; }
        [JsonProperty(PropertyName = "token")] public string Token { get; private set; }
        [JsonProperty(PropertyName = "password")] public string Password { get; private set; }
    }


    public readonly struct AuthenticateOk(ulong userId)
    {
        [JsonProperty(PropertyName = "status")] private string Status => "ok";
        [JsonProperty(PropertyName = "user_id")] private ulong UserId => userId;
    }


    public readonly struct NeedsPassword
    {
        [JsonProperty(PropertyName = "status")] private string Status => "needs_password";
    }
        
        
    public readonly struct WrongPassword
    {
        [JsonProperty(PropertyName = "status")] private string Status => "wrong_password";
        [JsonProperty(PropertyName = "message")] private string Message => "Wrong Password";
    }
        
        
    public readonly struct NotWhitelisted
    {
        [JsonProperty(PropertyName = "status")] private string Status => "not_whitelisted";
        [JsonProperty(PropertyName = "message")] private string Message => "Not Whitelisted";
    }
        
        
    public readonly struct GenericFail(string message)
    {
        [JsonProperty(PropertyName = "status")] private string Status => "error";
        [JsonProperty(PropertyName = "message")] private string Message => message;
    }
}