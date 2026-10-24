using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using TavernLib.Backend.Mods;

namespace TavernLib.Backend.Auth;

/// <summary>
/// Exact-version mod parity for a joining client: defense in depth behind the
/// launcher's pre-join render, for direct connects and stale or older launchers.
/// </summary>
public static class ModParity
{
    /// <summary>Wire format, not a message: JoinRejectionRecorder matches on it to
    /// recognise a recoverable rejection.</summary>
    public const string MismatchReasonPrefix = "Mod mismatch: ";

    /// <summary>Built for pc and parity-required: the one definition, shared by the
    /// join gate's early exit and ValidateClient.</summary>
    public static bool IsEnforced(ModHandshake.Entry mod) =>
        mod.ParityRequired && mod.Targets.Contains(ModManagerConstants.TargetPc);

    /// <summary>The rejection payload's {id, version, source_repo} shape;
    /// source_repo is a hint, never authority.</summary>
    public class RequiredMod
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("version")] public string Version;
        [JsonProperty("source_repo")] public string SourceRepo;
    }

    /// <summary>
    /// The enforced server mods the client's claim (id -> version) lacks or holds
    /// at another version; empty means parity holds. A missing claim is an empty
    /// one, and an enforced mod with no recorded version always mismatches, as in
    /// the launcher's _verify_mod_parity, so "no version" can't match "no mod".
    /// </summary>
    public static List<RequiredMod> ValidateClient(List<ModHandshake.Entry> serverMods, Dictionary<string, string> clientMods)
    {
        var mismatches = new List<RequiredMod>();
        foreach (var mod in serverMods.Where(IsEnforced))
        {
            clientMods.TryGetValue(mod.Id, out var have);
            if (string.IsNullOrEmpty(mod.Version) || have != mod.Version)
                mismatches.Add(new RequiredMod { Id = mod.Id, Version = mod.Version, SourceRepo = mod.SourceRepo });
        }
        return mismatches;
    }
}
