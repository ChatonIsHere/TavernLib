using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using TavernLib.Backend.Mods;

namespace TavernLib.Backend.Auth;

/// <summary>
/// Defense-in-depth: exact-version mod parity for a joining client. A
/// well-behaved launcher should already have rendered its Mods/ to match the
/// server before connecting - this exists for what bypasses or predates that
/// (a direct connect, a stale pre-join check, a modified/older launcher).
/// </summary>
public static class ModParity
{
    /// <summary>Same {id, version, source_repo} shape the rejection payload
    /// uses (source_repo is a hint, never authority) - so a mismatch list
    /// computed here drops straight into that file with no reshaping.</summary>
    public class RequiredMod
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("version")] public string Version;
        [JsonProperty("source_repo")] public string SourceRepo;
    }

    /// <summary>
    /// A server mod is enforced on the joining client when it has ClientSide
    /// true AND ParityRequired, and then at that EXACT version - a build
    /// within the same major can still diverge in ways that break a shared
    /// session, so "close enough" isn't good enough.
    ///
    /// Mods with ParityRequired false are never enforced. The server runs them and
    /// tells the client about them, but they're the client's choice: their two
    /// halves work independently, so a client without one costs nobody else
    /// anything. Refusing a join over one would make "recommended" mean the
    /// same as "required".
    ///
    /// clientMods is id -> version, parsed from the client's own claim
    /// (whatever its Mods/ currently has enabled); a client with no claim at
    /// all is treated as having nothing, so every required mod mismatches
    /// rather than silently passing. Server-only mods (ClientSide false) are
    /// never checked here - the client's own extra client-only mods are its own
    /// business. Returns the mods the client is missing or holding at the wrong
    /// version; empty means parity holds.
    /// </summary>
    public static List<RequiredMod> ValidateClient(List<ModHandshake.Entry> serverMods, Dictionary<string, string> clientMods)
    {
        var mismatches = new List<RequiredMod>();
        foreach (var mod in serverMods.Where(m => m.ClientSide && m.ParityRequired))
        {
            clientMods.TryGetValue(mod.Id, out var have);
            if (have != mod.Version)
                mismatches.Add(new RequiredMod { Id = mod.Id, Version = mod.Version, SourceRepo = mod.SourceRepo });
        }
        return mismatches;
    }
}
