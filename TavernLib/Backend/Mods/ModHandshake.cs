using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace TavernLib.Backend.Mods;

// Twin of install.py's handshake_snapshot; the hash must stay byte-identical.
public static class ModHandshake
{
    public class Entry
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("parity_required")] public bool ParityRequired { get; set; }
        [JsonProperty("source_repo")] public string SourceRepo { get; set; }
        [JsonProperty("targets")] public List<string> Targets { get; set; }
        [JsonProperty("target_sha256")] public SortedDictionary<string, string> TargetSha256 { get; set; }
        [JsonProperty("scenes", NullValueHandling = NullValueHandling.Ignore)] public List<string> Scenes { get; set; }
    }

    // Runs on every ping, mods_list and join, so a change on disk shows within this.
    private const int CacheTtlSeconds = 5;
    private static readonly object CacheLock = new();
    private static (string GameDir, DateTime AtUtc, (string, List<Entry>, List<UntrackedMod>) Value) _cache;

    public static (string Hash, List<Entry> Mods, List<UntrackedMod> Untracked) Snapshot(string gameDir)
    {
        lock (CacheLock)
            if (_cache.GameDir == gameDir && (DateTime.UtcNow - _cache.AtUtc).TotalSeconds < CacheTtlSeconds)
                return _cache.Value;

        var snapshot = BuildSnapshot(gameDir);
        lock (CacheLock) _cache = (gameDir, DateTime.UtcNow, snapshot);
        return snapshot;
    }

    private static (string, List<Entry>, List<UntrackedMod>) BuildSnapshot(string gameDir)
    {
        var (installed, untrackedMods) = ModInstaller.ScanMods(gameDir);
        var mods = installed
            .Where(m => m.Enabled && m.Record.Lists(ModManagerConstants.TargetServer))
            .Select(m => m.Record)
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .Select(r =>
            {
                var targets = r.ArtifactTargets();
                return new Entry
                {
                    Id = r.Id, Version = r.Version, ParityRequired = r.ParityRequired, SourceRepo = r.SourceRepo,
                    Targets = targets.Keys.ToList(), TargetSha256 = targets, Scenes = r.Scenes,
                };
            })
            .ToList();

        // As install.py's name.lower() sort; OrdinalIgnoreCase upper-cases, which orders '_' differently.
        var untracked = untrackedMods.Where(u => u.Enabled).OrderBy(u => u.Name.ToLowerInvariant(), StringComparer.Ordinal).ToList();

        // parity_required is in the hash so a required/optional flip without a version bump reaches clients;
        // untracked stamps so a dropped-in DLL does.
        var lines = mods
            .Select(m => $"{m.Id}@{m.Version}@{(m.ParityRequired ? "req" : "opt")}")
            .Concat(untracked.Select(u => $"untracked:{u.Name}@{UntrackedStamp(gameDir, u)}"));
        return (BackendUtils.HashDigest(string.Join("\n", lines)), mods, untracked);
    }

    // Twin of install.py's _untracked_stamp: mtime, never a content hash, since this runs on every ping.
    private static string UntrackedStamp(string gameDir, UntrackedMod mod)
    {
        try
        {
            var path = Path.Combine(ModPaths.ModsBase(gameDir), mod.Name);
            FileSystemInfo info = mod.Kind == UntrackedMod.KindFolder ? new DirectoryInfo(path) : new FileInfo(path);
            // A missing path reads as 1601 rather than throwing, where os.stat raises.
            if (!info.Exists) return "?";
            return new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return "?";
        }
    }
}
