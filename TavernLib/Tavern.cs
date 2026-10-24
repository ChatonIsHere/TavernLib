using System;
using System.Reflection;
using Alta.Console.Commands;
using MelonLoader;
using MonoMod.RuntimeDetour;
using TavernLib.Backend;
using TavernLib.Backend.Api;
using TavernLib.Backend.Mods;
using TavernLib.Backend.Server;
using TavernLib.Debugging;
using TavernLib.ModdingApi;
using TavernLib.Patches;
using TavernLib.Services;


[assembly: MelonInfo(typeof(TavernLib.Tavern), "TavernLib", "1.5.4", "Tavern Team", "https://github.com/ModdingTavern/TavernLib")]
namespace TavernLib;

public class Tavern : MelonPlugin
{
    internal static MelonLogger.Instance Logger { get; private set; }
    public const string Version = "1.5.4";

    private System.Threading.Timer _logArchiveTimer;


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

    private bool _didInitialLogArchive;

    private void StartLogArchiveTimer()
    {
        _logArchiveTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                if (!_didInitialLogArchive)
                {
                    _didInitialLogArchive = true;
                    LogArchiver.ArchiveLogNow();
                }
                else if (LogArchiver.ShouldAutoArchive())
                {
                    LogArchiver.ArchiveLogNow();
                }
            }
            catch (Exception e)
            {
                TavernLogger.Error($"Log archive check failed: {e}");
            }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));
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
                if (!CommandLineArguments.Contains(TavernArgs.DontManageAuth)) TeenyPatches.EnsureConsoleToken();

                // Only a genuinely headless server reconciles Mods/: /launcherauth means a
                // launcher owns them. Before TavernManager, whose port-1762 listener answers
                // from Mods/, and before the log-archive timer, whose first tick gives up for
                // good if NLog's target isn't up 10 s later.
                if (CommandLineArguments.Contains(TavernArgs.DontManageAuth))
                {
                    if (CommandLineArguments.Contains(TavernArgs.ModsListFile))
                        TavernLogger.Warn("mod reconcile: /modlist was passed but this server is launcher-managed (/launcherauth present); native mod reconciliation only runs for a genuinely headless server, so it was skipped. The launcher's own mod manager manages Mods/ for this server instead.");
                }
                else if (CommandLineArguments.TryGetNextArguments(TavernArgs.ModsListFile, 1, out var modListArgs))
                    ModReconciler.ReconcileModList(modListArgs[0], SceneIndexPatches.ForcedSceneName());

                // After reconcile, so what loads is what it left on disk.
                MelonModFolders.SyncForBoot(serverProcess: true);

                if (!CommandLineArguments.Contains(TavernArgs.DontManageAuth)) StartLogArchiveTimer();

                TavernServices.AddService(new TavernManager());
                Roles.Start();

                // Servers run through TavernLauncher are backed up by the launcher itself
                if (!CommandLineArguments.Contains(TavernArgs.DontManageAuth)) ServerBackup.Start();
            }
            else MelonModFolders.SyncForBoot(serverProcess: false);
            
            // Client & Server Services
            TavernServices.AddService(new EntranceMessageHandler());
        }
        catch (Exception e)
        {
            Logger.BigError($"Error when setting up base TavernLib services!!!!! {e}");
            throw;
        }
    }
}