using System;
using System.Collections.Generic;
using System.IO;
using Alta.Networking;
using HarmonyLib;
using Newtonsoft.Json;
using TavernLib.Backend.Auth;

namespace TavernLib.Patches;

// Writes last_rejection.json, so the launcher waiting on this process can install what's missing and
// relaunch. Every join path, direct connects included, ends in JoinServerPipeline.
[HarmonyPatch]
public static class JoinRejectionRecorder
{
    [HarmonyPatch(typeof(JoinServerPipeline), nameof(JoinServerPipeline.OnServerRejectedRequest)), HarmonyPostfix]
    public static void OnServerRejectedRequest(Connection connection, ConfirmJoinMessage joinMessage, JoinServerPipeline __instance)
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

            var path = TavernDirectories.LastRejection;
            Directory.CreateDirectory(TavernDirectories.ModdingTavern);
            File.WriteAllText(path + ".tmp", JsonConvert.SerializeObject(payload, Formatting.Indented));
            if (File.Exists(path)) File.Replace(path + ".tmp", path, null);
            else File.Move(path + ".tmp", path);

            TavernLogger.Warn($"Join rejected for a mod mismatch; wrote {path} for the launcher to recover.");
        }
        catch (Exception e)
        {
            TavernLogger.Error($"Error recording join rejection: {e}");
        }
    }
}
