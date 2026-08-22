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


    /// <summary>modsHash/modsCount are null when the server couldn't read its
    /// own Mods/ for this answer. A launcher treats a pong without them exactly
    /// as one from a TavernLib that predates mod sync - no sync, no join
    /// planning - which is the honest degraded answer; an empty hash would
    /// instead claim "no mods".</summary>
    public struct PingResponse(string serverName, bool passwordRequired, bool whitelistEnabled, int gamePort,
        string modsHash, int? modsCount)
    {
        [JsonProperty(PropertyName = "status")] private string Pong => "pong";
        [JsonProperty(PropertyName = "server_name")] private string ServerName { get; set; } = serverName;
        [JsonProperty(PropertyName = "password_required")] private bool PasswordRequired { get; set; } = passwordRequired;
        [JsonProperty(PropertyName = "whitelist_enabled")] private bool WhitelistEnabled { get; set; } = whitelistEnabled;
        [JsonProperty(PropertyName = "game_port")] private int GamePort { get; set; } = gamePort;
        [JsonProperty(PropertyName = "mods_hash")] private string ModsHash { get; set; } = modsHash;
        [JsonProperty(PropertyName = "mods_count")] private int? ModsCount { get; set; } = modsCount;
    }


    /// <summary>Sent length-prefixed, not as a plain single-recv reply - see
    /// AuthManager.WriteFramedResponse. Everything else on this port is small
    /// enough for the existing single recv/send; this is the one payload that
    /// can genuinely outgrow it.</summary>
    public readonly struct ModsListResponse(List<ModHandshake.Entry> mods, List<ModHandshake.UntrackedEntry> untracked)
    {
        [JsonProperty(PropertyName = "status")] private string Status => "ok";
        [JsonProperty(PropertyName = "mods")] private List<ModHandshake.Entry> Mods => mods;

        /// <summary>Mods this server runs that its own manager didn't install -
        /// advisory only. Kept a separate field rather than merged into `mods`
        /// because a client feeds that straight into join planning, which
        /// resolves every entry it's given; these have no id or version to
        /// resolve. A client too old to know the field ignores it and joins
        /// exactly as before, which is right for something that never blocks a
        /// join anyway.</summary>
        [JsonProperty(PropertyName = "untracked")] private List<ModHandshake.UntrackedEntry> Untracked => untracked;
    }


    public struct AuthenticateRequest
    {
        [JsonProperty(PropertyName = "username")] public string Username { get; private set; }
        [JsonProperty(PropertyName = "token")] public string Token { get; private set; }
        [JsonProperty(PropertyName = "password")] public string Password { get; private set; }
    }


    public readonly struct AuthenticateOk(ulong userId, bool questSceneRequired)
    {
        [JsonProperty(PropertyName = "status")] private string Status => "ok";
        [JsonProperty(PropertyName = "user_id")] private ulong UserId => userId;
        [JsonProperty(PropertyName = "quest_scene_required")] private bool QuestSceneRequired => questSceneRequired;
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


    public readonly struct WhitelistApplicationReceived(bool wasNew)
    {
        [JsonProperty(PropertyName = "status")] private string Status => "whitelist_application_received";
        [JsonProperty(PropertyName = "already_pending")] private bool AlreadyPending => !wasNew;
    }


    public readonly struct GenericFail(string message)
    {
        [JsonProperty(PropertyName = "status")] private string Status => "error";
        [JsonProperty(PropertyName = "message")] private string Message => message;
    }
}