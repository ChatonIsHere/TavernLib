using System.Collections.Generic;
using System.Reflection.Emit;
using Alta.Console;
using Alta.WebServer;
using HarmonyLib;
using TavernLib.Backend.Api;
using TavernLib.Backend.Server.Configs;
using TavernLib.Services;

namespace TavernLib.Patches;

// The game hard-codes its web server (1761) and websocket console (1760) ports. These move them to
// tavern_server.json's web_port and console_port, so two servers can share an address. Joining
// clients learn the web port from the auth reply (see TeenyPatches' /dev_server_web_port).
[HarmonyPatch]
public static class ServerPortPatches
{
    private static TavernServer Ports => TavernServices.GetService<TavernManager>()?.TavernConfig.LastRead;

    // ServerHandler calls new WebServerThread(), which the compiler turns into new WebServerThread(1761).
    [HarmonyPatch(typeof(WebServerThread), MethodType.Constructor, typeof(int)), HarmonyPrefix]
    public static void WebPort(ref int port)
    {
        if (Ports is not { } ports || port != TavernServer.DefaultWebPort) return;
        port = ports.Web;
        if (port != TavernServer.DefaultWebPort) TavernLogger.Msg($"Game web server on port {port}");
    }

    public static int ConsolePort() => Ports?.Console ?? TavernServer.DefaultConsolePort;

    // Start builds "ws://0.0.0.0:" + 1760 inline, so the constant is swapped for ConsolePort().
    [HarmonyPatch(typeof(WebSocketRemoteConsole), nameof(WebSocketRemoteConsole.Start)), HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> ConsolePortTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var swapped = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.LoadsConstant(TavernServer.DefaultConsolePort))
            {
                swapped++;
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ServerPortPatches), nameof(ConsolePort)))
                    .MoveLabelsFrom(instruction);
                continue;
            }
            yield return instruction;
        }
        if (swapped != 1) TavernLogger.Warn($"console_port: expected one 1760 in WebSocketRemoteConsole.Start, found {swapped}; the console may stay on 1760.");
    }
}
