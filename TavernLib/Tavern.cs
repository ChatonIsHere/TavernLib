using System;
using System.IO;
using System.Reflection;
using Alta.Console.Commands;
using MelonLoader;
using MelonLoader.Utils;
using MonoMod.RuntimeDetour;
using TavernLib.Backend;
using TavernLib.Backend.Api;
using TavernLib.Backend.Mods;
using TavernLib.Backend.Server.Configs;
using TavernLib.Debugging;
using TavernLib.Patches;
using TavernLib.Services;


[assembly: MelonInfo(typeof(TavernLib.Tavern), "TavernLib", "v1.4.2", "Tavern Team", "https://github.com/ModdingTavern/TavernLib")]
namespace TavernLib;

public class Tavern : MelonPlugin
{
    internal static MelonLogger.Instance Logger { get; private set; }
    public const string Version = "1.5.0";


    public override void OnEarlyInitializeMelon()
    {
        Logger = LoggerInstance;
        SetupServices();
    }

    public override void OnInitializeMelon()
    {
        Alta.Console.CommandService.CommandCollection.Collect(Assembly.GetExecutingAssembly());
    }

    public override void OnLateInitializeMelon()
    {
        SetupSelectionFixPatch();
    }


    private void SetupSelectionFixPatch()
    {
        var findObjectsMethod = typeof(SelectionCommandModule).GetMethod(nameof(SelectionCommandModule.FindObjects), BindingFlags.Static | BindingFlags.NonPublic);
        var selectionFixMethod = typeof(SelectFixPatch).GetMethod(nameof(SelectFixPatch.SelectionFix), BindingFlags.Static | BindingFlags.Public);
        
        _ = new Hook(findObjectsMethod, selectionFixMethod);
    }
    
    private void SetupServices()
    {
        try
        {
            // Converts Alta's Real Logs Into MelonLoader Logs
            if (CommandLineArguments.Contains("/debug_helper")) TavernServices.AddService(new DebugHelper());

            // Server Only Services
            if (CommandLineArguments.Contains(CommandLineArguments.StartServerArgument))
            {
                TavernLogger.Msg("Booting TavernLib in server mode");
                // /launcherauth means a Tavern launcher started this process and
                // already owns its lifecycle - auth (below) AND mods. It's the
                // one signal that actually distinguishes "launcher-run" from
                // "genuinely headless", so native mod reconciliation is gated on
                // its ABSENCE, not merely on whether /modlist happens to be
                // passed - a launcher-run server must never self-manage Mods/
                // here even if /modlist were passed to it by mistake.
                var isLauncherManaged = CommandLineArguments.Contains(TavernArgs.DontManageAuth);
                if (!isLauncherManaged) TeenyPatches.EnsureConsoleToken();

                TavernServices.AddService(new TavernManager());

                if (!isLauncherManaged && CommandLineArguments.TryGetNextArguments(TavernArgs.ModsListFile, 1, out var modListArgs))
                    ReconcileMods(modListArgs[0]);
                else if (isLauncherManaged && CommandLineArguments.Contains(TavernArgs.ModsListFile))
                    TavernLogger.Warn("mod reconcile: /modlist was passed but this server is launcher-managed (/launcherauth present); native mod reconciliation only runs for a genuinely headless server, so it was skipped. The launcher's own mod manager manages Mods/ for this server instead.");
            }
            
            // Client & Server Services
            TavernServices.AddService(new EntranceMessageHandler());
        }
        catch (Exception e)
        {
            Logger.BigError($"Error when setting up base TavernLib services!!!!! {e}");
            throw;
        }
    }

    /// <summary>
    /// Native (C#) mod install/update/disable for a genuinely headless server,
    /// run synchronously before this method returns - MelonLoader doesn't scan
    /// Mods/ until later in startup (Core.Initialize -> LoadMelons(Plugins),
    /// this hook, before Core.Start -> LoadMelons(Mods)), so whatever's on disk
    /// once this returns is what loads this session. Only reached when both
    /// /launcherauth is absent (see SetupServices) and TavernArgs.ModsListFile
    /// is passed; a launcher-run server manages its own Mods/ via Python and
    /// is never routed here even if /modlist were passed to it.
    /// </summary>
    private void ReconcileMods(string modsListPathArg)
    {
        TavernLogger.Msg("mod reconcile: running before Mods/ is scanned (OnEarlyInitializeMelon).");
        try
        {
            var path = Path.IsPathRooted(modsListPathArg)
                ? modsListPathArg
                : Path.Combine(TavernDirectories.ModdingTavern, modsListPathArg);

            var modsList = new ModsListConfig(path);
            modsList.ReadFromFile();

            // Fixed location, unlike the modlist above - the trusted-repos
            // allow-list persists across whichever modlist file /modlist
            // points at (see TrustedReposConfig / ModsCommandModule.addrepo).
            var trustedRepos = new TrustedReposConfig(TavernDirectories.ModRepos);
            trustedRepos.ReadFromFile();

            ModReconciler.Reconcile(MelonEnvironment.GameRootDirectory, modsList.LastRead, trustedRepos.LastRead);
        }
        catch (Exception e)
        {
            TavernLogger.Error($"mod reconcile: couldn't read '{modsListPathArg}' or reconcile failed ({e}); booting with Mods/ unchanged.");
        }
    }
}