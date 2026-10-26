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

    // No readable claim: the Quest app, an in-game board or a direct connect. A mod with no recorded version
    // always mismatches, as in the launcher's _verify_mod_parity, so "no version" can't match "no mod".
    public static List<RequiredMod> CheckJoin(List<ModHandshake.Entry> serverMods, string claim)
    {
        var clientMods = ReadClaim(claim);
        bool Fails(ModHandshake.Entry mod) => clientMods == null
            ? BlocksClaimless(mod)
            : mod.ParityRequired && mod.Targets.Contains(ModManagerConstants.TargetClientWindows)
              && (string.IsNullOrEmpty(mod.Version) || !clientMods.TryGetValue(mod.Id, out var have) || have != mod.Version);
        return serverMods.Where(Fails).Select(m => new RequiredMod { Id = m.Id, Version = m.Version, SourceRepo = m.SourceRepo }).ToList();
    }

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
}
