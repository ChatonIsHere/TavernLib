using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace TavernLib.Backend.Mods;

/// <summary>
/// Builds the ping/pong mod-sync fingerprint from whatever's currently
/// enabled in Mods/ - the C# counterpart of modmanager.py's
/// handshake_snapshot. Only enabled mods count; a disabled one isn't loaded,
/// so a joining client shouldn't be asked to match it.
/// </summary>
public static class ModHandshake
{
    public class Entry
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("client_side")] public bool ClientSide { get; set; }
        [JsonProperty("server_side")] public bool ServerSide { get; set; }

        /// <summary>Whether a joining client must match this exactly, or is only
        /// recommended to (see <see cref="ModParityField"/>). Sent so the client
        /// can tell what it has to have from what it may decline, without
        /// resolving anything itself.</summary>
        [JsonProperty("parity_required")] public bool ParityRequired { get; set; }

        /// <summary>Hint only: which repo this mod actually came from, so a
        /// client that can't resolve it from any repo it has added knows what to
        /// suggest adding. Never resolved into a pull on its own.</summary>
        [JsonProperty("source_repo")] public string SourceRepo { get; set; }
    }

    public static (string Hash, int Count, List<Entry> Mods) Snapshot(string gameDir)
    {
        var mods = ModInstaller.ListInstalledModsWithState(gameDir)
            .Where(m => m.Enabled)
            .OrderBy(m => m.Record.Id, StringComparer.Ordinal)
            .Select(m => new Entry
            {
                Id = m.Record.Id,
                Version = m.Record.Version,
                ClientSide = m.Record.ClientSide,
                ServerSide = m.Record.ServerSide,
                ParityRequired = m.Record.ParityRequired,
                SourceRepo = m.Record.SourceRepo
            })
            .ToList();

        // ParityRequired is part of the fingerprint, not just the list. A server
        // can flip a mod between required and recommended without its version
        // moving, and a client caches this whole list against this hash - so
        // leaving it out would let a client keep planning against the old answer
        // until it restarted, and skip a mod that had since become mandatory.
        // Must stay byte-identical to modmanager.py's handshake_snapshot.
        var fingerprint = string.Join("\n",
            mods.Select(m => $"{m.Id}@{m.Version}@{(m.ParityRequired ? "req" : "opt")}"));
        string hash;
        using (var sha = SHA256.Create())
        {
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(fingerprint));
            hash = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        return (hash, mods.Count, mods);
    }
}
