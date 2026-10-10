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


[assembly: MelonInfo(typeof(TavernLib.Tavern), "TavernLib", "1.5.3", "Tavern Team", "https://github.com/ModdingTavern/TavernLib")]
namespace TavernLib;

public class Tavern : MelonPlugin
{
    internal static MelonLogger.Instance Logger { get; private set; }
    public const string Version = "1.5.3";

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
                // /launcherauth means a Tavern launcher started this process and
                // already owns its lifecycle - auth (below) AND mods. It's the
                // one signal that actually distinguishes "launcher-run" from
                // "genuinely headless", so native mod reconciliation is gated on
                // its ABSENCE, not merely on whether /modlist happens to be
                // passed - a launcher-run server must never self-manage Mods/
                // here even if /modlist were passed to it by mistake.
                var isLauncherManaged = CommandLineArguments.Contains(TavernArgs.DontManageAuth);
                if (!isLauncherManaged) TeenyPatches.EnsureConsoleToken();

                // Reconcile BEFORE TavernManager goes up: constructing it starts
                // AuthManager's port-1762 listener, whose ping/mods_list answers
                // snapshot Mods/ - the very directory reconcile may still be
                // rewriting for minutes. Started in this order, a client can't
                // cache a mods hash for a state that never finishes existing.
                if (!isLauncherManaged && CommandLineArguments.TryGetNextArguments(TavernArgs.ModsListFile, 1, out var modListArgs))
                    ReconcileMods(modListArgs[0]);
                else if (isLauncherManaged && CommandLineArguments.Contains(TavernArgs.ModsListFile))
                    TavernLogger.Warn("mod reconcile: /modlist was passed but this server is launcher-managed (/launcherauth present); native mod reconciliation only runs for a genuinely headless server, so it was skipped. The launcher's own mod manager manages Mods/ for this server instead.");

                // After reconcile, not before: the timer's first tick archives
                // 10s later and gives up for good if NLog's unity-log target
                // isn't up yet, so a reconcile that runs for minutes would
                // silently cost us the boot-time archive.
                if (!isLauncherManaged) StartLogArchiveTimer();

                TavernServices.AddService(new TavernManager());
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
    /// run synchronously before this method returns - MelonLoader doesn't load
    /// Mods/ until later in startup (Core.Initialize -> LoadMelons(Plugins),
    /// this hook, before Core.Start -> LoadMelons(Mods)). It has already listed
    /// the Mods/ subfolders by then, though, so MelonModFolders.Sync updates that
    /// list afterwards; with it, whatever's on disk once this returns is what
    /// loads this session. Only reached when both
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

            // The operator named this file explicitly, so its absence is almost
            // certainly a typo'd path - not a request for an empty list. Reading
            // on regardless would auto-create an empty modlist and the reconcile
            // below would then disable every managed mod against it.
            if (!File.Exists(path))
            {
                TavernLogger.Error($"mod reconcile: modlist file '{path}' does not exist; skipping reconcile and booting with Mods/ unchanged. Fix the /modlist path or create the file.");
                return;
            }

            var modsList = new ModsListConfig(path);
            modsList.ReadFromFile();

            // Fixed location, unlike the modlist above - the trusted-repos
            // allow-list persists across whichever modlist file /modlist
            // points at (see TrustedReposConfig / ModsCommandModule.addrepo).
            var trustedRepos = new TrustedReposConfig(TavernDirectories.ModRepos);
            trustedRepos.ReadFromFile();

            ModReconciler.Reconcile(MelonEnvironment.GameRootDirectory, modsList.LastRead, trustedRepos.LastRead);
            MelonModFolders.Sync(MelonEnvironment.ModsDirectory);
        }
        catch (Exception e)
        {
            TavernLogger.Error($"mod reconcile: couldn't read '{modsListPathArg}' or reconcile failed ({e}); booting with Mods/ unchanged.");
        }
    }
}