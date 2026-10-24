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

    /// <summary>Parity-required and built for pc: what a join carrying a
    /// claim must match.</summary>
    public static bool IsEnforced(ModHandshake.Entry mod) =>
        mod.ParityRequired && mod.Targets.Contains(ModManagerConstants.TargetPc);

    /// <summary>Parity-required and built for any client target: what refuses a
    /// join with no readable claim. A superset of IsEnforced, so also the join
    /// gate's early exit.</summary>
    public static bool BlocksClaimless(ModHandshake.Entry mod) =>
        mod.ParityRequired && (mod.Targets.Contains(ModManagerConstants.TargetPc)
                               || mod.Targets.Contains(ModManagerConstants.TargetQuest));

    /// <summary>The rejection payload's {id, version, source_repo} shape;
    /// source_repo is a hint, never authority.</summary>
    public class RequiredMod
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("version")] public string Version;
        [JsonProperty("source_repo")] public string SourceRepo;
    }

    private static RequiredMod Required(ModHandshake.Entry mod) =>
        new() { Id = mod.Id, Version = mod.Version, SourceRepo = mod.SourceRepo };

    /// <summary>
    /// The join verdict for a TavernMods claim: the server mods it fails on,
    /// empty to admit. No claim, or one that isn't a JSON object of id to
    /// version (the Quest app, an in-game board, a direct connect), fails on
    /// every mod that <see cref="BlocksClaimless"/>; a readable one goes
    /// through <see cref="ValidateClient"/>.
    /// </summary>
    public static List<RequiredMod> CheckJoin(List<ModHandshake.Entry> serverMods, string claim)
    {
        var clientMods = ReadClaim(claim);
        return clientMods == null
            ? serverMods.Where(BlocksClaimless).Select(Required).ToList()
            : ValidateClient(serverMods, clientMods);
    }

    /// <summary>The claim as id -> version, or null when it's missing or unreadable.</summary>
    public static Dictionary<string, string> ReadClaim(string claim)
    {
        if (string.IsNullOrEmpty(claim)) return null;
        try
        {
            // No date parsing, so a value reads back exactly as sent.
            return JsonConvert.DeserializeObject<Dictionary<string, string>>(claim,
                new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The enforced server mods the client's claim (id -> version) lacks or holds
    /// at another version; empty means parity holds. An enforced mod with no
    /// recorded version always mismatches, as in the launcher's
    /// _verify_mod_parity, so "no version" can't match "no mod".
    /// </summary>
    public static List<RequiredMod> ValidateClient(List<ModHandshake.Entry> serverMods, Dictionary<string, string> clientMods)
    {
        var mismatches = new List<RequiredMod>();
        foreach (var mod in serverMods.Where(IsEnforced))
        {
            clientMods.TryGetValue(mod.Id, out var have);
            if (string.IsNullOrEmpty(mod.Version) || have != mod.Version)
                mismatches.Add(Required(mod));
        }
        return mismatches;
    }
}
