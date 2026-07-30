using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Alta.Api.DataTransferModels.Converters;
using Alta.Networking;
using Alta.Networking.Servers;
using Alta.Serialization;
using HarmonyLib;
using MelonLoader.Utils;
using Newtonsoft.Json;
using TavernLib.Backend.Api;
using TavernLib.Backend.Auth;
using TavernLib.Backend.Mods;
using TavernLib.Services;

namespace TavernLib.Patches;

[HarmonyPatch]
public static class PlayerJoinFilter
{
    [HarmonyPatch(typeof(ServerPlayerConnectionHandlerOld), nameof(ServerPlayerConnectionHandlerOld.InitializeConnection)), HarmonyPrefix]
    public static bool FlyCamFilter(Connection connection)
    {
        connection.SetHandler(MessageType.RequestJoin, FilterJoinRequest);
        return false;
    }

    private static async void FilterJoinRequest(Connection connection, Stream stream)
    {
        TavernLogger.Msg($"Filtering join request for user at IP {connection.IpAddress}");

        try
        {
            TavernLogger.Msg("Filtering flycam joiners");
            if (!await FilterFlyCam(connection, stream)) return;

            TavernLogger.Msg("User passed flycam check, checking for valid account token");
            if (!await FilterInvalidTokens(connection, stream)) return;

            TavernLogger.Msg("User passed token check, checking mod parity");
            if (!await FilterModParity(connection, stream)) return;

            TavernLogger.Msg("User passed mod parity check, moving onto vanilla check");
            ServerHandler.Current.playerJoinHandler.CheckApproved(connection, stream);
        }
        catch (Exception e)
        {
            TavernLogger.Error($"Error when filtering join request! {e}");
            await ServerPlayerConnectionHandlerOld.PlayerDenied(connection, "Error when checking authenticity");
            throw;
        }
    }

    private static async Task<bool> FilterInvalidTokens(Connection connection, Stream stream)
    {
        try
        {
            using var readingStream = stream.Clone() as Stream;
            var requestJoinMessage = new RequestJoinMessage();
            requestJoinMessage.Serialize(connection, readingStream);

            TavernLogger.Msg($"User trying to join with JWT {requestJoinMessage.UserCredentials}");

            var token = JWTUtility.CreateFromString(requestJoinMessage.UserCredentials, true);
            var tavernToken = token.Claims.FirstOrDefault(claim => claim.Type == "TavernToken")?.Value;
            var id = ulong.Parse(token.Claims.FirstOrDefault(claim => claim.Type == "UserId")?.Value ?? "0");
            var username = token.Claims.FirstOrDefault(claim => claim.Type == "Username")?.Value ?? "";

            var users = TavernServices.GetService<TavernManager>().UserConfig.LastRead.Users;
            if (users.TryGetValue(username.ToLowerInvariant(), out var user) && user.UserId == id && user.Token == tavernToken) return true;

            await ServerPlayerConnectionHandlerOld.PlayerDenied(connection, "Data mismatch or account not found");
            return false;
        }

        catch (Exception e)
        {
            TavernLogger.Error($"Error in FilterInvalidTokens! {e}");
            throw;
        }
    }

    /// <summary>
    /// Defense-in-depth: exact-version mod parity. Reads the joining client's
    /// own installed-mods claim ("TavernMods" on the same JWT
    /// FilterInvalidTokens already reads) and denies the join if it disagrees
    /// with what this server requires (ModParity.ValidateClient). By the time
    /// this runs, a well-behaved launcher's pre-join render should already
    /// match - this exists for what bypasses or predates that (a direct
    /// connect, a stale pre-join check, a modified/older launcher). A server
    /// with no ClientSide mods at all has nothing to check, so every join
    /// passes through untouched.
    /// </summary>
    private static async Task<bool> FilterModParity(Connection connection, Stream stream)
    {
        using var readingStream = stream.Clone() as Stream;
        var requestJoinMessage = new RequestJoinMessage();
        requestJoinMessage.Serialize(connection, readingStream);

        var (_, _, serverMods) = ModHandshake.Snapshot(MelonEnvironment.GameRootDirectory);
        if (!serverMods.Any(m => m.ClientSide)) return true;

        var token = JWTUtility.CreateFromString(requestJoinMessage.UserCredentials, true);
        var modsClaim = token.Claims.FirstOrDefault(claim => claim.Type == "TavernMods")?.Value;

        Dictionary<string, string> clientMods;
        try
        {
            clientMods = string.IsNullOrEmpty(modsClaim)
                ? new Dictionary<string, string>()
                : JsonConvert.DeserializeObject<Dictionary<string, string>>(modsClaim) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            // Malformed claim - treat as having nothing rather than trusting
            // it, so a broken/tampered claim fails closed, not open.
            clientMods = new Dictionary<string, string>();
        }

        var mismatches = ModParity.ValidateClient(serverMods, clientMods);
        if (mismatches.Count == 0) return true;

        var reason = $"Mod mismatch: {JsonConvert.SerializeObject(mismatches)}";
        TavernLogger.Warn($"User at {connection.IpAddress} rejected for mod mismatch: {reason}");
        await ServerPlayerConnectionHandlerOld.PlayerDenied(connection, reason);
        return false;
    }

    private static async Task<bool> FilterFlyCam(Connection connection, Stream stream)
    {
        try
        {
            using var readingStream = stream.Clone() as Stream;

            var requestJoinMessage = new RequestJoinMessage();
            requestJoinMessage.Serialize(connection, readingStream);

            if (requestJoinMessage.PlayerMode is PlayerMode.Fly or PlayerMode.AutoCam or PlayerMode.Unassigned)
            {
                var token = JWTUtility.CreateFromString(requestJoinMessage.UserCredentials, true);
                
                var username = token.Claims.FirstOrDefault(c => c.Type == "Username")?.Value ?? "";
                var users = TavernServices.GetService<TavernManager>().UserConfig;
                
                if (users.TryGetUser(username, out var user) && user.CanEnterFlyMode)
                {
                    TavernLogger.Msg($"User {username} allowed unorthodox role via role");
                    return true;
                }

                TavernLogger.Warn($"User kicked for bizarre mode");
                await ServerPlayerConnectionHandlerOld.PlayerDenied(connection, "Bizarre mode detected.");
                return false;
            }
        }
        catch (Exception e)
        {
            TavernLogger.Error($"Error in FilterFlyCam {e}");
            await ServerPlayerConnectionHandlerOld.PlayerDenied(connection, "FlyCam filter error.");
            return false;
        }

        return true;
    }

    [HarmonyPatch(typeof(ConfirmJoinMessage), MethodType.Constructor, [typeof(bool), typeof(bool), typeof(ClientJoinResult), typeof(JoinedServerInfo), typeof(string)]), HarmonyPostfix]
    public static void LogDisconnectAttempts(bool isAllowed, bool isDoingPrerequisites, ClientJoinResult joinResult, JoinedServerInfo serverInfo, string error, ref ConfirmJoinMessage __instance)
    {
        TavernLogger.Warn($"ConfirmJoinMessage created {JsonConvert.SerializeObject(__instance, Formatting.Indented)}");
    }
}