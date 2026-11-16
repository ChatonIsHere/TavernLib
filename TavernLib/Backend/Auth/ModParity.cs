using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using TavernLib.Backend.Mods;

namespace TavernLib.Backend.Auth;

// Defense in depth behind the launcher's pre-join check, for direct connects and stale launchers.
public static class ModParity
{
    // Wire format: JoinRejectionRecorder matches on it to recognise a recoverable rejection.
    public const string MismatchReasonPrefix = "Mod mismatch: ";

    // A superset of what a claim is checked against, so also the join gate's early exit.
    public static bool BlocksClaimless(ModHandshake.Entry mod) =>
        mod.ParityRequired && (mod.Targets.Contains(ModManagerConstants.TargetClientWindows) || mod.Targets.Contains(ModManagerConstants.TargetClientAndroid));

    public class RequiredMod
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("version")] public string Version;
        [JsonProperty("source_repo")] public string SourceRepo;
    }

    // clientMods is the player's set_mods list, null when they sent none: the Quest app or a direct connect.
    // A mod with no recorded version always mismatches, as in the launcher's _verify_mod_parity, so
    // "no version" can't match "no mod".
    public static List<RequiredMod> CheckJoin(List<ModHandshake.Entry> serverMods, Dictionary<string, string> clientMods)
    {
        bool Fails(ModHandshake.Entry mod) => clientMods == null
            ? BlocksClaimless(mod)
            : mod.ParityRequired && mod.Targets.Contains(ModManagerConstants.TargetClientWindows)
              && (string.IsNullOrEmpty(mod.Version) || !clientMods.TryGetValue(mod.Id, out var have) || have != mod.Version);
        return serverMods.Where(Fails).Select(m => new RequiredMod { Id = m.Id, Version = m.Version, SourceRepo = m.SourceRepo }).ToList();
    }

    // The list the launcher sends in set_mods (launcher_window._mods_list), as JSON: {id: version} of the
    // enabled mods installed for client_windows. Public for in-game joins, like TavernEssentials' board,
    // which call it by reflection.
    public static string LocalMods(string gameDir)
    {
        var mods = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in ModInstaller.ScanMods(gameDir).Installed)
            if (m.Enabled && m.Record.Lists(ModManagerConstants.TargetClientWindows))
                mods[m.Record.Id] = m.Record.Version ?? "";
        return JsonConvert.SerializeObject(mods);
    }
}
