using System;
using System.Collections.Generic;
using System.IO;
using Alta.Networking;
using HarmonyLib;
using Newtonsoft.Json;
using TavernLib.Backend.Auth;

namespace TavernLib.Patches;

/// <summary>
/// Client-side half of post-reject recovery. Every join - the normal
/// matchmade path and TavernLauncher's /dev_server_ip direct-connect path
/// alike - goes through ConnectToServerUtility.ConnectToServer ->
/// JoinServerPipeline.AttemptToConnectToServer (confirmed by decompiling the
/// installed game: DevGameServerInfo.GetDevServer feeds the same
/// GameModeManager.JoinServer entry point as a normal join). When the server
/// rejects with a mod-mismatch reason, JoinServerPipeline calls its own
/// OnServerRejectedRequest before giving up - this patches that method to
/// write last_rejection.json so the waiting launcher (which spawned this
/// game process and is blocked on it exiting) can recover the missing mods
/// and relaunch. A rejection for any other reason (wrong password, banned,
/// server full, a stale/incompatible client, etc.) is left alone entirely -
/// only the exact "Mod mismatch: [...]" shape ModParity/PlayerJoinFilter
/// itself produces is ever written here.
/// </summary>
[HarmonyPatch]
public static class JoinRejectionRecorder
{
    private const string ModMismatchPrefix = "Mod mismatch: ";

    [HarmonyPatch(typeof(JoinServerPipeline), nameof(JoinServerPipeline.OnServerRejectedRequest)), HarmonyPostfix]
    public static void OnServerRejectedRequest(Connection connection, ConfirmJoinMessage joinMessage, JoinServerPipeline __instance)
    {
        try
        {
            var error = joinMessage.Error;
            if (string.IsNullOrEmpty(error) || !error.StartsWith(ModMismatchPrefix))
                return;

            var missing = JsonConvert.DeserializeObject<List<ModParity.RequiredMod>>(
                error.Substring(ModMismatchPrefix.Length));
            if (missing == null || missing.Count == 0)
                return;

            WriteRejectionFile(connection, __instance, missing);
        }
        catch (Exception e)
        {
            TavernLogger.Error($"Error recording join rejection: {e}");
        }
    }

    /// <summary>server/server_name are a cross-check for the launcher, not
    /// the mechanism itself (that's written_at plus the launcher deleting any
    /// stale copy before it launches). __instance.connectionInfo is null
    /// until AttemptToConnectToServer sets it, which always happens before a
    /// rejection can occur, so this is safe without an extra guard beyond the
    /// outer try/catch.</summary>
    private static void WriteRejectionFile(Connection connection, JoinServerPipeline pipeline, List<ModParity.RequiredMod> missing)
    {
        var payload = new RejectionPayload
        {
            WrittenAt = DateTime.UtcNow.ToString("o"),
            Server = new RejectionServer
            {
                Host = pipeline.connectionInfo?.Address?.ToString() ?? connection.IpAddress,
                GamePort = pipeline.connectionInfo?.GamePort ?? 0,
            },
            ServerName = pipeline.ServerConnectingTo?.Name,
            Missing = missing,
        };

        var path = TavernDirectories.LastRejection;
        var tmp = path + ".tmp";
        Directory.CreateDirectory(TavernDirectories.ModdingTavern);
        File.WriteAllText(tmp, JsonConvert.SerializeObject(payload, Formatting.Indented));
        File.Delete(path);
        File.Move(tmp, path);

        TavernLogger.Warn($"Join rejected for a mod mismatch; wrote {path} for the launcher to recover.");
    }
}
