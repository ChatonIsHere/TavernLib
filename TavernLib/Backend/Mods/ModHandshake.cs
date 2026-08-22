using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using TavernLib.Backend;

namespace TavernLib.Backend.Mods;

/// <summary>
/// Builds the ping/pong mod-sync fingerprint from whatever's currently
/// enabled in Mods/ - the C# counterpart of modmanager.py's
/// handshake_snapshot. Only enabled mods count; a disabled one isn't loaded,
/// so a joining client shouldn't be asked to match it.
/// </summary>
public static class ModHandshake
{
    /// <summary>An untracked mod the server is running - one MelonLoader loads
    /// that this manager didn't install. Name and shape are all a server can
    /// honestly say about one: there's no id, version, or source to send, which
    /// is exactly why a client can display it but never resolve, install, or be
    /// blocked over it.</summary>
    public class UntrackedEntry
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
    }
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

        /// <summary>The manifest's artifact sha256, off the install record.
        /// Advisory and additive - deliberately NOT part of the fingerprint
        /// hash below (which must stay byte-identical to modmanager.py's):
        /// a client holding the same id and version but different bytes (a
        /// release re-published under its version, or the same version from
        /// a different repo) passes version parity yet genuinely diverges,
        /// and this lets the client's launcher at least say so. Empty when
        /// the record predates the field.</summary>
        [JsonProperty("sha256")] public string Sha256 { get; set; }
    }

    /// <summary>
    /// A cheap "has this changed?" marker for one untracked mod: its mtime as
    /// whole Unix seconds, never a content hash. Snapshot runs on every ping,
    /// and hashing every loose DLL in Mods/ that often would cost far more than
    /// an advisory is worth.
    ///
    /// mtime alone, not mtime+size, so this stays byte-identical to
    /// modmanager.py's _untracked_stamp: a directory has no portable size the
    /// two could agree on. A directory's own mtime moves when entries are added
    /// or removed, so a folder mod gaining or losing files still
    /// re-fingerprints. Anything unreadable stamps "?" rather than throwing - a
    /// mod vanishing mid-scan must not take the whole handshake down with it.
    /// </summary>
    private static string UntrackedStamp(string gameDir, UntrackedEntry entry)
    {
        try
        {
            var path = Path.Combine(ModPaths.ModsBase(gameDir), entry.Name);
            // FileInfo/DirectoryInfo don't throw for a missing path - they
            // return the 1601 epoch - so a mod that vanished between listing
            // and stamping must be caught here explicitly, or this would stamp
            // "-11644473600" where modmanager.py's os.stat raises and stamps
            // "?", splitting the two fingerprints.
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

    // Snapshot walks Mods/ and JSON-parses every record, and it's called from
    // every ping, mods_list request, and join - all unauthenticated or hot
    // paths. The mod set only changes at boot reconcile (which now finishes
    // before the listener starts) or through console commands (which call
    // Invalidate), so a short TTL plus explicit invalidation keeps answers
    // fresh without paying the disk walk per request.
    private const int CacheTtlSeconds = 5;
    private static readonly object CacheLock = new();
    private static string _cachedGameDir;
    private static DateTime _cachedAtUtc;
    private static (string Hash, int Count, List<Entry> Mods, List<UntrackedEntry> Untracked) _cached;

    /// <summary>Drops the cached snapshot so the next request re-reads Mods/.
    /// Call after anything that installs, removes, toggles, or repairs a mod.</summary>
    public static void Invalidate()
    {
        lock (CacheLock) _cachedGameDir = null;
    }

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
                SourceRepo = m.Record.SourceRepo,
                Sha256 = m.Record.Sha256 ?? ""
            })
            .ToList();

        var untracked = ModInstaller.ListUntrackedMods(gameDir)
            .Where(u => u.Enabled)
            // Lowercase-then-ordinal, NOT OrdinalIgnoreCase: that compares
            // upper-cased, which orders '_' and '[' the other side of the
            // letters from modmanager.py's name.lower() sort, and these lines
            // feed a hash the two implementations must agree on byte for byte.
            .OrderBy(u => u.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .Select(u => new UntrackedEntry { Name = u.Name, Kind = u.Kind })
            .ToList();

        // ParityRequired is part of the fingerprint, not just the list. A server
        // can flip a mod between required and recommended without its version
        // moving, and a client caches this whole list against this hash - so
        // leaving it out would let a client keep planning against the old answer
        // until it restarted, and skip a mod that had since become mandatory.
        //
        // Untracked mods are in it for the same reason: a client caches the
        // whole reply against this hash, so leaving them out would let an
        // operator drop a new DLL into Mods/ and have every client keep
        // reporting the old set until something else happened to move the hash.
        // Must stay byte-identical to modmanager.py's handshake_snapshot.
        var lines = mods
            .Select(m => $"{m.Id}@{m.Version}@{(m.ParityRequired ? "req" : "opt")}")
            .Concat(untracked.Select(u => $"untracked:{u.Name}@{UntrackedStamp(gameDir, u)}"));
        var fingerprint = string.Join("\n", lines);
        var hash = BackendUtils.HashDigest(fingerprint);

        // Count is MANAGED mods only, deliberately: it's sent in the pong so a
        // client can sanity-check its cached mods list without decoding
        // anything, and that list is the managed one.
        return (hash, mods.Count, mods, untracked);
    }
}
