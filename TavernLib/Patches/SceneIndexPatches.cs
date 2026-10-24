using Alta.Api.DataTransferModels.Models.Responses;
using HarmonyLib;

namespace TavernLib.Patches;

[HarmonyPatch]
public class SceneIndexPatches
{
    internal static int? ForcedSceneIndex()
    {
        if (CommandLineArguments.Contains(TavernArgs.TutorialScene)) return 1;
        if (CommandLineArguments.Contains(TavernArgs.QuestScene)) return 4;
        return null;
    }

    /// <summary>This process's scene by manifest "scenes" names, from the same
    /// arguments as <see cref="ForcedSceneIndex"/>.</summary>
    internal static string ForcedSceneName()
    {
        if (CommandLineArguments.Contains(TavernArgs.TutorialScene)) return "tutorial";
        if (CommandLineArguments.Contains(TavernArgs.QuestScene)) return "quest";
        return "normal";
    }

    [HarmonyPatch(typeof(JoinedServerGameMode), MethodType.Constructor, [typeof(GameServerInfo)]), HarmonyPostfix]
    public static void OverrideSceneIndex(GameServerInfo server)
    {
        if (ForcedSceneIndex() is int index) server.SceneIndex = index;
    }
}