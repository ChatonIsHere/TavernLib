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

    /// <summary>The scene this process runs, by the names a mod manifest's
    /// "scenes" uses. Read from the same launch arguments, in the same order, as
    /// <see cref="ForcedSceneIndex"/>: the arguments are what actually pick the
    /// scene, whatever server_settings.json says.</summary>
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