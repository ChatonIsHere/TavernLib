using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Alta.Networking;
using Alta.Networking.Servers;
using HarmonyLib;
using MelonLoader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TavernLib.Backend.Auth;

namespace TavernLib.Patches;

// Writes last_rejection.json for the launcher to install what's missing and rejoin. Every join path ends in
// JoinServerPipeline. Joining the same server again soon after quits the game for the launcher to do that;
// with Quick join on, the first refusal does.
[HarmonyPatch]
public static class JoinRejectionRecorder
{
    private static string _refusedBy;
    private static DateTime _refusedAt;
    private static MelonPreferences_Entry<bool> _quickJoin;

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

    // Quick join is the quick_join file, which the launcher shares. This preference mirrors it, so a mod
    // settings board (TavernEssentials') can switch it in the game.
    public static void InitQuickJoin()
    {
        _quickJoin = MelonPreferences.CreateCategory("TavernLib").CreateEntry("QuickJoin", false, "Quick join",
            "When a server needs mods you don't have, close the game, install them and rejoin without asking.");
        _quickJoin.Value = File.Exists(TavernDirectories.QuickJoin);
        _quickJoin.OnEntryValueChanged.Subscribe((_, on) =>
        {
            try
            {
                if (!on) File.Delete(TavernDirectories.QuickJoin);
                else
                {
                    Directory.CreateDirectory(TavernDirectories.ModdingTavern);
                    File.WriteAllText(TavernDirectories.QuickJoin, "");
                }
            }
            catch (Exception e)
            {
                TavernLogger.Error($"Couldn't save Quick join: {e.Message}");
            }
        });
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
            // Not straight after the launcher's own rejoin, so a server that still refuses can't loop.
            var quick = recoverable && File.Exists(TavernDirectories.QuickJoin)
                && !CommandLineArguments.Contains(TavernArgs.Rejoin);
            _refusedBy = recoverable ? __instance.ServerConnectingTo?.Name : null;
            _refusedAt = DateTime.UtcNow;
            // The server sends JoinAccepted, which the game retries; a retry would count as joining again.
            joinMessage.joinResult = (int)ClientJoinResult.JoinRejected;
            joinMessage.error = quick
                ? "This server needs mods you don't have. Closing the game to install them and rejoin."
                : recoverable
                    ? "This server needs mods you don't have. Join it again to quit and install them with the Tavern Launcher, or turn on Quick join to do this straight away."
                    : "This server needs mods you don't have. Join it from the Tavern Launcher to install them.";
            // A few seconds to read that first.
            if (quick) MelonCoroutines.Start(HandOverSoon());
        }
        catch (Exception e)
        {
            TavernLogger.Error($"Error recording join rejection: {e}");
        }
    }

    [HarmonyPatch(typeof(JoinServerPipeline), nameof(JoinServerPipeline.AttemptToConnectToServer)), HarmonyPrefix]
    public static bool AttemptToConnectToServer(JoinServerPipeline __instance, ref Task<JoinResult> __result)
    {
        if (_refusedBy == null || __instance.ServerConnectingTo?.Name != _refusedBy
            || DateTime.UtcNow - _refusedAt > TimeSpan.FromMinutes(10)) return true;
        if (!HandOver()) return true;
        __result = Task.FromResult(JoinResult.FailedResult(ClientJoinResult.JoinRejected, "Quitting to install this server's mods."));
        return false;
    }

    private static IEnumerator HandOverSoon()
    {
        yield return new UnityEngine.WaitForSecondsRealtime(4);
        HandOver();
    }

    // Marks the record for the launcher to install without asking, starts the launcher if it isn't watching,
    // and quits. False, joining as usual, if that fails.
    private static bool HandOver()
    {
        if (_refusedBy == null) return false;
        try
        {
            var record = JObject.Parse(File.ReadAllText(TavernDirectories.LastRejection));
            record["rejoin"] = true;
            Save(record.ToString());
            if (!LauncherWatching) Process.Start(LauncherExe ?? throw new FileNotFoundException("the launcher has moved"));

            TavernLogger.Msg($"Quitting for the launcher to install {_refusedBy}'s mods and rejoin.");
            _refusedBy = null;
            UnityEngine.Application.Quit();
            return true;
        }
        catch (Exception e)
        {
            TavernLogger.Error($"Couldn't hand the join to the launcher, joining as usual: {e.Message}");
            _refusedBy = null;
            return false;
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
