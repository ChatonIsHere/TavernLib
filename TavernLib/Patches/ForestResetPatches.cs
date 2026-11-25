using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Alta.Chunks;
using Alta.Console.Commands;
using Alta.Forests;
using Alta.Networking;
using Alta.Networking.Servers;
using HarmonyLib;

namespace TavernLib.Patches;

// The game's ForestGenerator.Wipe picks a new seed straight away but keeps the forest it already built, so
// until a restart every cache it writes is named after the new seed and filled from the old layout, and
// the old seed's files never get deleted. A reset is queued instead: the forest's chunks are cleared and the
// players in it moved out as before, the running world keeps its seed, and only the save gets the new one.
// The next start builds the new forest and deletes the cache files of any other seed.
[HarmonyPatch]
public static class ForestResetPatches
{
    private static readonly ConditionalWeakTable<ForestGenerator, StrongBox<int>> Queued = new();
    private static readonly HashSet<ForestGenerator> Resetting = new();

    [HarmonyPatch(typeof(ForestGenerator), nameof(ForestGenerator.Wipe)), HarmonyPrefix]
    public static bool QueueReset(ForestGenerator __instance, int newSeed, ref Task __result, MethodBase __originalMethod)
    {
        // A mod with its own reset (Forest Performance's runs a whole maintenance flow) does the job instead.
        if (OtherReset(__originalMethod)) return true;
        __result = Queue(__instance, newSeed);
        return false;
    }

    private static bool? _otherReset;

    private static bool OtherReset(MethodBase wipe)
    {
        _otherReset ??= HarmonyLib.Harmony.GetPatchInfo(wipe)?.Prefixes
            .Any(patch => patch.PatchMethod.DeclaringType?.Assembly != typeof(ForestResetPatches).Assembly) == true;
        if (_otherReset == true) TavernLogger.Msg("Another mod replaces the forest reset, so TavernLib leaves it to that mod");
        return _otherReset.Value;
    }

    // True when a reset is waiting for the next start.
    public static bool IsQueued(ForestGenerator forest) => Queued.TryGetValue(forest, out _);

    private static async Task Queue(ForestGenerator forest, int newSeed)
    {
        if (!Resetting.Add(forest)) return;
        try
        {
            var generation = await forest.GetGenerationAsync();
            var chunks = LocationChunkHelper.GetAllContaining(generation.Bounds).ToList();
            var players = new HashSet<Player>();
            foreach (var chunk in chunks) players.UnionWith(chunk.Players.Items);

            var outside = SpawnArea.InstanceMap[SpawnAreaIdentifier.OutsideForest].GetRandomSpawnPosition();
            foreach (var player in players) player.PlayerController?.ForceTeleportTo(outside);

            await Task.WhenAll(chunks.Select(forest.WipeChunk));

            // The game's version ignored newSeed; 0 or more is now the seed to use.
            var seed = newSeed >= 0 ? newSeed : new Random().Next(int.MaxValue);
            Queued.Remove(forest);
            Queued.Add(forest, new StrongBox<int>(seed));
            TavernLogger.Msg($"{forest.Cache} reset: cleared {chunks.Count} chunks and moved {players.Count} players out. The new forest (seed {seed}) is made when the server restarts.");
        }
        finally
        {
            Resetting.Remove(forest);
        }
    }

    // The save holds the queued seed, so the next start loads it.
    [HarmonyPatch]
    public static class SaveQueuedSeed
    {
        public static MethodBase TargetMethod() => AccessTools.GetDeclaredMethods(typeof(ForestGenerator))
            .First(m => m.Name.EndsWith(".Save") && m.ReturnType == typeof(ForestGeneratorSaveData));

        public static void Postfix(ForestGenerator __instance, ForestGeneratorSaveData __result)
        {
            if (__result != null && Queued.TryGetValue(__instance, out var seed)) __result.seed = seed.Value;
        }
    }

    // After loading, cache files of any other seed are leftovers from a reset.
    [HarmonyPatch]
    public static class DeleteOldCaches
    {
        public static MethodBase TargetMethod() => AccessTools.GetDeclaredMethods(typeof(ForestGenerator))
            .First(m => m.Name.EndsWith(".Load") && m.GetParameters().FirstOrDefault()?.ParameterType == typeof(ForestGeneratorSaveData));

        public static void Postfix(ForestGenerator __instance, ForestGeneratorSaveData data)
        {
            if (ServerHandler.Current?.SaveUtility?.CacheFolder == null || data == null || data.seed < 0) return;
            try
            {
                var name = Regex.Escape(__instance.Cache);
                var forestFile = new Regex($@" - (Forest Nodes {name}|Forest Terrain - {name}|Forest Static - {name}|River Nodes - {name}) - (\d+)\.cache$");
                var old = ServerHandler.Current.SaveUtility.CacheFolder.AllFiles
                    .Where(file => forestFile.Match(file.Name) is { Success: true } match && match.Groups[2].Value != data.seed.ToString())
                    .ToList();
                if (old.Count == 0) return;
                foreach (var file in old) _ = file.DeleteAsync();
                TavernLogger.Msg($"Deleted {old.Count} cache files left over from an old {__instance.Cache} seed");
            }
            catch (Exception e)
            {
                TavernLogger.Warn($"Couldn't tidy {__instance.Cache}'s old cache files: {e.Message}");
            }
        }
    }

    // world forest reset answered nothing; it now says what happened and what's left.
    [HarmonyPatch(typeof(WorldCommandModule.ForestCommandModule), nameof(WorldCommandModule.ForestCommandModule.Reset)), HarmonyPrefix]
    public static bool AnswerReset(ForestGenerator forest, ref Task __result)
    {
        __result = Answer(forest);
        return false;
    }

    private static async Task<string> Answer(ForestGenerator forest)
    {
        await forest.Wipe();
        return IsQueued(forest)
            ? $"{forest.Cache} is reset. Players in it were moved out. The new forest is made when the server restarts."
            : $"{forest.Cache}: another mod's reset ran instead of TavernLib's. Check the log for what it did.";
    }
}
