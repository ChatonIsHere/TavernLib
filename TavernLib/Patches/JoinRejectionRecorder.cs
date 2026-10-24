using System;
using System.Collections.Generic;
using System.IO;
using Alta.Networking;
using HarmonyLib;
using Newtonsoft.Json;
using TavernLib.Backend.Auth;

namespace TavernLib.Patches;

/// <summary>
/// Client side of rejection recovery: a join refused with ModParity's mismatch
/// reason writes last_rejection.json, so the launcher waiting on this process
/// can install what's missing and relaunch. Every join path, direct connects
/// included, ends in JoinServerPipeline; other rejections are left alone.
/// </summary>
[HarmonyPatch]
public static class JoinRejectionRecorder
{
    [HarmonyPatch(typeof(JoinServerPipeline), nameof(JoinServerPipeline.OnServerRejectedRequest)), HarmonyPostfix]
    public static void OnServerRejectedRequest(Connection connection, ConfirmJoinMessage joinMessage, JoinServerPipeline __instance)
    {
        try
        {
            var error = joinMessage.Error;
            if (string.IsNullOrEmpty(error) || !error.StartsWith(ModParity.MismatchReasonPrefix))
                return;

            var missing = JsonConvert.DeserializeObject<List<ModParity.RequiredMod>>(
                error.Substring(ModParity.MismatchReasonPrefix.Length));
            if (missing == null || missing.Count == 0)
                return;

            WriteRejectionFile(connection, __instance, missing);
        }
        catch (Exception e)
        {
            TavernLogger.Error($"Error recording join rejection: {e}");
        }
    }

    /// <summary>server/server_name only cross-check for the launcher, which also
    /// deletes any stale file before launching. connectionInfo is always set
    /// before a rejection can arrive.</summary>
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
