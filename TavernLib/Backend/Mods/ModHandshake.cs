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
                SourceRepo = m.Record.SourceRepo
            })
            .ToList();

        var fingerprint = string.Join("\n", mods.Select(m => $"{m.Id}@{m.Version}"));
        string hash;
        using (var sha = SHA256.Create())
        {
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(fingerprint));
            hash = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        return (hash, mods.Count, mods);
    }
}
