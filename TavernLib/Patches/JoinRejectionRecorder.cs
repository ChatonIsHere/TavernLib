using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Alta.Networking;
using HarmonyLib;
using Newtonsoft.Json;
using TavernLib.Backend.Auth;

namespace TavernLib.Patches;

// Writes last_rejection.json, so the launcher waiting on this process can install what's missing and
// relaunch. Every join path, direct connects included, ends in JoinServerPipeline. Then swaps the wire
// reason for words before the game shows it, so a join from inside the game (TavernEssentials' board)
// says what to do instead of showing JSON.
[HarmonyPatch]
public static class JoinRejectionRecorder
{
    [HarmonyPatch(typeof(JoinServerPipeline), nameof(JoinServerPipeline.OnServerRejectedRequest)), HarmonyPrefix]
    public static void OnServerRejectedRequest(Connection connection, ConfirmJoinMessage joinMessage, JoinServerPipeline __instance)
    {
        try
        {
            var error = joinMessage.Error;
            if (error?.StartsWith(ModParity.MismatchReasonPrefix, StringComparison.Ordinal) != true) return;

            var missing = JsonConvert.DeserializeObject<List<ModParity.RequiredMod>>(error.Substring(ModParity.MismatchReasonPrefix.Length));
            if (missing == null || missing.Count == 0) return;
            joinMessage.error = $"This server needs {string.Join(", ", missing.Select(m => $"{m.Id} {m.Version}"))}. "
                + "Quit the game and the Tavern Launcher will install them and rejoin.";

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
