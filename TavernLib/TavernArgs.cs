namespace TavernLib;

public static class TavernArgs
{
    public const string DontManageAuth = "/launcherauth";
    public const string JoinLocalServer = "/join_local_server";
    public const string DevServerIp = "/dev_server_ip";
    public const string DevServerPort = "/dev_server_port";
    public const string DevServerWebPort = "/dev_server_web_port";   // the server's web_port, from its auth reply
    public const string QuestScene = "/questScene";
    public const string TutorialScene = "/tutorialScene";
    public const string ModsListFile = "/modlist";
    public const string ListingApi = "/listing_api";
    public const string Rejoin = "/tavern_rejoin";     // the launcher's rejoin after installing a server's mods
}