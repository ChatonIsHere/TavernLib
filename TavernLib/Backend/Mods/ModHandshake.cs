using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using TavernLib.Backend;

namespace TavernLib.Backend.Mods;

/// <summary>
/// The ping/pong mods_hash and mods_list from what's enabled in Mods/. Twin of
/// install.py's handshake_snapshot; the hash must stay byte-identical.
/// </summary>
public static class ModHandshake
{
    /// <summary>An untracked mod the server runs: name and shape only, so a client
    /// can show it but never resolve it or be blocked over it.</summary>
    public class UntrackedEntry
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
    }
    public class Entry
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("parity_required")] public bool ParityRequired { get; set; }

        /// <summary>A hint for which repo to suggest adding; never a pull source.</summary>
        [JsonProperty("source_repo")] public string SourceRepo { get; set; }

        /// <summary>Every known target the record's artifacts build, sorted: what
        /// a client decides whether the mod concerns it by. Not in the hash.</summary>
        [JsonProperty("targets")] public List<string> Targets { get; set; }

        /// <summary>Advisory, and not in the hash: lets a client notice the same
        /// version built from different bytes.</summary>
        [JsonProperty("target_sha256")] public SortedDictionary<string, string> TargetSha256 { get; set; }

        /// <summary>Omitted when null (every scene), as install.py omits it; not in
        /// the hash.</summary>
        [JsonProperty("scenes", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> Scenes { get; set; }
    }

    /// <summary>
    /// Twin of install.py's _untracked_stamp: whole-second mtime, never a content
    /// hash (this runs on every ping), and not size, which a directory has no
    /// portable value for. Anything unreadable stamps "?".
    /// </summary>
    private static string UntrackedStamp(string gameDir, UntrackedEntry entry)
    {
        try
        {
            var path = Path.Combine(ModPaths.ModsBase(gameDir), entry.Name);
            // A missing path doesn't throw here (it reads as 1601), where os.stat raises
            // and stamps "?"; checked explicitly so the two hashes can't split.
            var info = entry.Kind == UntrackedMod.KindFolder
                ? new DirectoryInfo(path)
                : (FileSystemInfo)new FileInfo(path);
            if (!info.Exists) return "?";
            return new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeSeconds()
                .ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return "?";
        }
    }

    // Snapshot runs on every ping, mods_list and join, and Mods/ only changes in
    // boot reconcile, which finishes before the listener starts.
    private const int CacheTtlSeconds = 5;
    private static readonly object CacheLock = new();
    private static string _cachedGameDir;
    private static DateTime _cachedAtUtc;
    private static (string Hash, int Count, List<Entry> Mods, List<UntrackedEntry> Untracked) _cached;

    public static (string Hash, int Count, List<Entry> Mods, List<UntrackedEntry> Untracked) Snapshot(string gameDir)
    {
        lock (CacheLock)
        {
            if (_cachedGameDir == gameDir && (DateTime.UtcNow - _cachedAtUtc).TotalSeconds < CacheTtlSeconds)
                return _cached;
        }

        var snapshot = BuildSnapshot(gameDir);
        lock (CacheLock)
        {
            _cachedGameDir = gameDir;
            _cachedAtUtc = DateTime.UtcNow;
            _cached = snapshot;
        }
        return snapshot;
    }

    private static (string Hash, int Count, List<Entry> Mods, List<UntrackedEntry> Untracked) BuildSnapshot(string gameDir)
    {
        var (installed, untrackedMods) = ModInstaller.ScanMods(gameDir);
        // Only what this server runs: a build installed just for pc in the same
        // folder is invisible to it.
        var mods = installed
            .Where(m => m.Enabled && m.Record.Lists(ModManagerConstants.TargetServer))
            .OrderBy(m => m.Record.Id, StringComparer.Ordinal)
            .Select(m =>
            {
                var targets = m.Record.ArtifactTargets();
                return new Entry
                {
                    Id = m.Record.Id,
                    Version = m.Record.Version,
                    ParityRequired = m.Record.ParityRequired,
                    SourceRepo = m.Record.SourceRepo,
                    Targets = targets.Keys.ToList(),
                    TargetSha256 = targets,
                    Scenes = m.Record.Scenes
                };
            })
            .ToList();

        var untracked = untrackedMods
            .Where(u => u.Enabled)
            // Lowercase then ordinal, as install.py's name.lower() sort.
            // OrdinalIgnoreCase upper-cases, which orders '_' differently.
            .OrderBy(u => u.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .Select(u => new UntrackedEntry { Name = u.Name, Kind = u.Kind })
            .ToList();

        // parity_required is in the hash so a required/recommended flip without a
        // version bump reaches clients; untracked stamps so a dropped-in DLL does.
        var lines = mods
            .Select(m => $"{m.Id}@{m.Version}@{(m.ParityRequired ? "req" : "opt")}")
            .Concat(untracked.Select(u => $"untracked:{u.Name}@{UntrackedStamp(gameDir, u)}"));
        var fingerprint = string.Join("\n", lines);
        var hash = BackendUtils.HashDigest(fingerprint);

        // Managed mods only: a client checks it against its cached mods list.
        return (hash, mods.Count, mods, untracked);
    }
}
