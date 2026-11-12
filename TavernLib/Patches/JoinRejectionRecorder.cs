using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Alta.Networking;
using Alta.Networking.Servers;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TavernLib.Backend.Auth;

namespace TavernLib.Patches;

// Writes last_rejection.json for the launcher to install what's missing and rejoin. Every join path ends in
// JoinServerPipeline. Joining the same server again soon after quits the game for the launcher to do that.
[HarmonyPatch]
public static class JoinRejectionRecorder
{
    private static string _refusedBy;
    private static DateTime _refusedAt;

    // The launcher's own launches pass /force_offline, and it watches those.
    private static bool LauncherWatching => Environment.GetCommandLineArgs().Contains("/force_offline");

    private static string LauncherExe
    {
        get
        {
            try
            {
                var exe = File.ReadAllText(TavernDirectories.LauncherPath).Trim();
                return File.Exists(exe) ? exe : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    [HarmonyPatch(typeof(JoinServerPipeline), nameof(JoinServerPipeline.OnServerRejectedRequest)), HarmonyPrefix]
    public static void OnServerRejectedRequest(Connection connection, ref ConfirmJoinMessage joinMessage, JoinServerPipeline __instance)
    {
        try
        {
            var error = joinMessage.Error;
            if (error?.StartsWith(ModParity.MismatchReasonPrefix, StringComparison.Ordinal) != true) return;

            var missing = JsonConvert.DeserializeObject<List<ModParity.RequiredMod>>(error.Substring(ModParity.MismatchReasonPrefix.Length));
            if (missing == null || missing.Count == 0) return;

            var payload = new
            {
                schema = 1,
                written_at = DateTime.UtcNow.ToString("o"),
                server = new
                {
                    host = __instance.connectionInfo?.Address?.ToString() ?? connection.IpAddress,
                    game_port = __instance.connectionInfo?.GamePort ?? 0,
                },
                server_name = __instance.ServerConnectingTo?.Name,
                missing,
            };
            Save(JsonConvert.SerializeObject(payload, Formatting.Indented));
            TavernLogger.Warn($"Join rejected for a mod mismatch; wrote {TavernDirectories.LastRejection} for the launcher to recover.");

            var recoverable = LauncherWatching || LauncherExe != null;
            _refusedBy = recoverable ? __instance.ServerConnectingTo?.Name : null;
            _refusedAt = DateTime.UtcNow;
            // The server sends JoinAccepted, which the game retries; a retry would count as joining again.
            joinMessage.joinResult = (int)ClientJoinResult.JoinRejected;
            joinMessage.error = recoverable
                ? "This server needs mods you don't have. Join it again to quit and install them with the Tavern Launcher."
                : "This server needs mods you don't have. Join it from the Tavern Launcher to install them.";
        }
        catch (Exception e)
        {
            TavernLogger.Error($"Error recording join rejection: {e}");
        }
    }

    [HarmonyPatch(typeof(JoinServerPipeline), nameof(JoinServerPipeline.AttemptToConnectToServer)), HarmonyPrefix]
    public static bool AttemptToConnectToServer(JoinServerPipeline __instance, ref Task<JoinResult> __result)
    {
        try
        {
            if (_refusedBy == null || __instance.ServerConnectingTo?.Name != _refusedBy
                || DateTime.UtcNow - _refusedAt > TimeSpan.FromMinutes(10)) return true;

            // The player already chose, so the launcher doesn't ask again.
            var record = JObject.Parse(File.ReadAllText(TavernDirectories.LastRejection));
            record["rejoin"] = true;
            Save(record.ToString());
            if (!LauncherWatching) Process.Start(LauncherExe ?? throw new FileNotFoundException("the launcher has moved"));

            TavernLogger.Msg($"Quitting for the launcher to install {_refusedBy}'s mods and rejoin.");
            _refusedBy = null;
            __result = Task.FromResult(JoinResult.FailedResult(ClientJoinResult.Cancelled));
            UnityEngine.Application.Quit();
            return false;
        }
        catch (Exception e)
        {
            TavernLogger.Error($"Couldn't hand the join to the launcher, joining as usual: {e.Message}");
            _refusedBy = null;
            return true;
        }
    }

    private static void Save(string json)
    {
        var path = TavernDirectories.LastRejection;
        Directory.CreateDirectory(TavernDirectories.ModdingTavern);
        File.WriteAllText(path + ".tmp", json);
        if (File.Exists(path)) File.Replace(path + ".tmp", path, null);
        else File.Move(path + ".tmp", path);
    }
}
