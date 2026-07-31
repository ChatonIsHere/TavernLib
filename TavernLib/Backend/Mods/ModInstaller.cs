using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace TavernLib.Backend.Mods;

/// <summary>
/// Downloads, verifies, and installs mods/libraries into Mods/&lt;id&gt;/ and
/// UserLibs/, and manages the enable/disable-in-place state - the native-C#
/// counterpart of modmanager.py's install/uninstall/disable/enable logic,
/// reusing the exact same on-disk record convention.
/// </summary>
public static class ModInstaller
{
    private static readonly HttpClient DownloadHttp = new();

    private static string Sha256File(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        var hash = sha.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Streams url to destPath, checking the shared reconcile deadline as it
    /// goes so one slow/stalled transfer can't blow past the whole pass's
    /// wall-clock cap. Throws (and leaves nothing at destPath) on failure.
    /// </summary>
    private static void DownloadFile(string url, string destPath, DateTime deadlineUtc)
    {
        try
        {
            var secondsLeft = Math.Max(1, (deadlineUtc - DateTime.UtcNow).TotalSeconds);
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(secondsLeft));
            using var response = DownloadHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using var output = File.Create(destPath);
            var buffer = new byte[1 << 16];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (DateTime.UtcNow > deadlineUtc)
                    throw new ModManagerException($"Download of {url} exceeded the reconcile pass's time budget; giving up.");
                output.Write(buffer, 0, read);
            }
        }
        catch (Exception e) when (e is not ModManagerException)
        {
            throw new ModManagerException($"Couldn't download {url}: {e.Message}");
        }
    }

    /// <summary>
    /// Downloads url into a temp file inside workDir (never the system temp dir,
    /// so the follow-up move is a same-volume atomic rename), verifies its
    /// sha256, then hands the temp path to the caller to move or extract. Always
    /// cleaned up, so a failed verify/move leaves nothing behind.
    /// </summary>
    private static T VerifiedDownload<T>(string url, string sha256, string workDir, DateTime deadlineUtc, Func<string, T> withTempFile)
    {
        Directory.CreateDirectory(workDir);
        var tmp = Path.Combine(workDir, $".tavern_dl_{Guid.NewGuid():N}.part");
        try
        {
            DownloadFile(url, tmp, deadlineUtc);
            var actual = Sha256File(tmp);
            if (!string.Equals(actual, sha256 ?? "", StringComparison.OrdinalIgnoreCase))
                throw new ModManagerException(
                    $"Downloaded file failed its checksum: expected {sha256}, got {actual}. The file may be corrupted or tampered with; nothing was installed.");
            return withTempFile(tmp);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static void DownloadVerifyMove(string url, string sha256, string destPath, DateTime deadlineUtc)
    {
        var destDir = Path.GetDirectoryName(destPath);
        VerifiedDownload(url, sha256, destDir, deadlineUtc, tmp =>
        {
            // Move, not copy: the temp already sits in destPath's own directory
            // (VerifiedDownload guarantees that), so this is a same-volume
            // rename and therefore atomic. A copy that's interrupted partway
            // leaves a truncated file at destPath that the sidecar written
            // afterwards would claim is hash-verified.
            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tmp, destPath);
            return true;
        });
    }

    /// <summary>
    /// Extract a .zip into destDir, refusing any member that would escape it
    /// (zip-slip) or that is a symlink, and capping entry count + total
    /// decompressed size so a decompression bomb can't fill the disk. A mod can
    /// come from an unreviewed source, so every member's resolved path must stay
    /// within destDir and the running decompressed total must stay under the
    /// configured cap; the first unsafe/oversized entry throws, having written
    /// only whatever came before it.
    /// </summary>
    private static void SafeExtractZip(string zipPath, string destDir)
    {
        var destRoot = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > ModManagerConstants.ZipMaxEntries)
            throw new ModManagerException($"This mod archive has {zip.Entries.Count} entries, over the {ModManagerConstants.ZipMaxEntries} limit; refusing to extract it.");

        long written = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (name.StartsWith("/") || name.StartsWith("\\") || (name.Length >= 2 && name[1] == ':'))
                throw new ModManagerException($"Unsafe archive entry '{name}': absolute path.");

            // Symlinks are stored in the high 16 bits of ExternalAttributes as a unix
            // file-mode; 0xA000 is S_IFLNK. A symlink could point outside destDir.
            var mode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            if (mode != 0 && (mode & 0xF000) == 0xA000)
                throw new ModManagerException($"Unsafe archive entry '{name}': symlink.");

            var rel = name.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(destRoot, rel));
            if (target != destRoot && !target.StartsWith(destRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ModManagerException($"Unsafe archive entry '{name}': escapes the mod folder.");

            if (name.EndsWith("/") || name.EndsWith("\\"))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using var src = entry.Open();
            using var dst = File.Create(target);
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > ModManagerConstants.ZipMaxTotalUncompressed)
                    throw new ModManagerException(
                        $"This mod archive extracts to more than {ModManagerConstants.ZipMaxTotalUncompressed / (1024 * 1024)} MB; refusing to extract it (possible zip bomb).");
                dst.Write(buffer, 0, read);
            }
        }
    }

    private static void ResetDir(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
    }

    /// <summary>
    /// Installs one mod into its own folder, Mods/&lt;id&gt;/, and writes the record +
    /// MelonLoader marker inside it. Assembled in a throwaway staging folder and
    /// swapped into place only once complete, so a failed download/verify/extract
    /// never leaves a partial or half-upgraded mod folder.
    /// </summary>
    public static void InstallMod(string gameDir, ModManifest mod, DateTime deadlineUtc)
    {
        var modDir = ModPaths.ModDirPath(gameDir, mod.Id);
        var staging = ModPaths.StagingPath(gameDir, mod.Id);
        ResetDir(staging);
        try
        {
            if (mod.Package == "zip")
            {
                VerifiedDownload<object>(mod.DownloadUrl, mod.Sha256, staging, deadlineUtc, tmp =>
                {
                    SafeExtractZip(tmp, staging);
                    return null;
                });
            }
            else
            {
                DownloadVerifyMove(mod.DownloadUrl, mod.Sha256, Path.Combine(staging, mod.InstallName()), deadlineUtc);
            }
            ModRecord.FromManifest(mod).WriteTo(Path.Combine(staging, ModManagerConstants.RecordName));

            if (Directory.Exists(modDir)) Directory.Delete(modDir, recursive: true);
            Directory.Move(staging, modDir);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>
    /// Gathers every library_dependencies entry across a resolved set of mods,
    /// deduplicated by filename. Same filename + matching sha256/download_url
    /// collapses to one install; same filename + a different sha256/download_url
    /// throws a conflict naming both mods - the library equivalent of a diamond
    /// conflict, since there's no version-range concept for a pinned file.
    /// </summary>
    public static List<LibraryDependency> CollectLibraryDependencies(IEnumerable<ModManifest> mods)
    {
        var byFilename = new Dictionary<string, (LibraryDependency Lib, string Owner)>();
        foreach (var mod in mods)
        {
            foreach (var lib in mod.LibraryDependencies ?? new List<LibraryDependency>())
            {
                var key = ModPaths.SafeBasename(lib.Filename);
                if (!byFilename.TryGetValue(key, out var existing))
                {
                    byFilename[key] = (lib, mod.Name);
                    continue;
                }
                if (!string.Equals(existing.Lib.Sha256, lib.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    existing.Lib.DownloadUrl.TrimEnd('/') != lib.DownloadUrl.TrimEnd('/'))
                    throw new ModManagerException(
                        $"Library conflict on '{lib.Filename}': {existing.Owner} pins {existing.Lib.DownloadUrl} (sha {existing.Lib.Sha256.Substring(0, 12)}...) but {mod.Name} pins {lib.DownloadUrl} (sha {lib.Sha256.Substring(0, 12)}...). They can't both be installed.");
            }
        }
        return byFilename.Values.Select(v => v.Lib).ToList();
    }

    /// <summary>Same shape as InstallMod: downloads, verifies, writes
    /// UserLibs/&lt;safe filename&gt; plus its .meta.json sidecar.</summary>
    public static void InstallLibraryDependency(string gameDir, LibraryDependency lib, DateTime deadlineUtc)
    {
        var name = ModPaths.SafeBasename(lib.Filename);
        var dest = Path.Combine(ModPaths.UserLibsDir(gameDir), name);
        DownloadVerifyMove(lib.DownloadUrl, lib.Sha256, dest, deadlineUtc);
        var sidecar = new
        {
            filename = name,
            sha256 = lib.Sha256,
            download_url = lib.DownloadUrl,
        };
        File.WriteAllText(ModPaths.LibrarySidecarPath(gameDir, name), Newtonsoft.Json.JsonConvert.SerializeObject(sidecar, Newtonsoft.Json.Formatting.Indented));
    }

    /// <summary>
    /// Installs a mod AND its full closure: fetches the root's manifest, walks
    /// dependencies, gathers pinned libraries, and only then installs anything -
    /// every cycle/depth/diamond/library conflict is raised before anything
    /// touches disk.
    /// </summary>
    public static void InstallModClosure(string gameDir, ModManifest root, List<ModManifest> deps, List<LibraryDependency> libs, DateTime deadlineUtc)
    {
        foreach (var m in new[] { root }.Concat(deps))
            InstallMod(gameDir, m, deadlineUtc);
        foreach (var lib in libs)
            InstallLibraryDependency(gameDir, lib, deadlineUtc);
    }

    /// <summary>Delete a UserLibs/ library and its sidecar. Returns whether
    /// anything was actually removed.</summary>
    private static bool RemoveLibrary(string gameDir, string filename)
    {
        var removed = false;
        foreach (var path in new[] { Path.Combine(ModPaths.UserLibsDir(gameDir), filename), ModPaths.LibrarySidecarPath(gameDir, filename) })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                removed = true;
            }
        }
        return removed;
    }

    /// <summary>
    /// Removes an installed community mod: its whole Mods/&lt;id&gt;/ folder, then
    /// each library it pinned, UNLESS another still-installed mod's record lists
    /// it (shared libraries stay, orphaned ones go). Returns whether anything was
    /// deleted.
    /// </summary>
    public static bool UninstallMod(string gameDir, string modId, IEnumerable<ModRecord> allInstalled)
    {
        var meta = ModRecord.Read(gameDir, modId);
        var removed = false;
        var libs = (meta?.Libraries ?? new List<string>()).Select(ModPaths.SafeBasename).ToList();

        var modDir = ModPaths.ModDirPath(gameDir, modId);
        if (Directory.Exists(modDir))
        {
            Directory.Delete(modDir, recursive: true);
            removed = true;
        }

        if (libs.Count > 0)
        {
            var stillNeeded = new HashSet<string>();
            foreach (var other in allInstalled)
                if (other.Id != modId)
                    foreach (var lib in other.Libraries ?? new List<string>())
                        stillNeeded.Add(ModPaths.SafeBasename(lib));

            foreach (var lib in libs)
            {
                if (stillNeeded.Contains(lib)) continue;
                if (RemoveLibrary(gameDir, lib)) removed = true;
            }
        }
        return removed;
    }

    /// <summary>
    /// Disable an installed folder-layout mod WITHOUT uninstalling it: rename its
    /// manifest.json to manifest.disabled.json so MelonLoader stops scanning the
    /// folder, while every file stays in place. Returns whether it was enabled
    /// and is now disabled.
    /// </summary>
    public static bool DisableMod(string gameDir, string modId)
    {
        var src = ModPaths.ModRecordPath(gameDir, modId);
        if (!File.Exists(src)) return false;
        File.Move(src, ModPaths.DisabledRecordPath(gameDir, modId));
        return true;
    }

    /// <summary>Re-enable a disabled mod: rename manifest.disabled.json back to
    /// manifest.json. Returns whether it was disabled and is now enabled.</summary>
    public static bool EnableMod(string gameDir, string modId)
    {
        var src = ModPaths.DisabledRecordPath(gameDir, modId);
        if (!File.Exists(src)) return false;
        File.Move(src, ModPaths.ModRecordPath(gameDir, modId));
        return true;
    }

    /// <summary>
    /// Rewrites an installed mod's record from a freshly fetched manifest,
    /// without touching a single file the mod ships. For fields that describe
    /// the mod rather than its contents and can therefore change without the
    /// version changing - parity_required is the one that matters, since a
    /// server enforces it on joining clients and would otherwise keep reporting
    /// whatever was true the day it installed.
    ///
    /// Writes to whichever record the mod currently has, so a disabled mod stays
    /// disabled. Returns whether anything actually changed.
    /// </summary>
    public static bool RefreshRecordMetadata(string gameDir, ModManifest manifest)
    {
        var enabledPath = ModPaths.ModRecordPath(gameDir, manifest.Id);
        var path = File.Exists(enabledPath) ? enabledPath : ModPaths.DisabledRecordPath(gameDir, manifest.Id);
        if (!File.Exists(path)) return false;

        var record = JsonConvert.DeserializeObject<ModRecord>(File.ReadAllText(path));
        if (record == null || record.Id != manifest.Id) return false;

        if (record.ParityRequired == manifest.ParityRequired) return false;

        record.ParityRequired = manifest.ParityRequired;
        File.WriteAllText(path, JsonConvert.SerializeObject(record, Formatting.Indented));
        return true;
    }

    /// <summary>Every mod currently on disk (enabled or disabled), with each
    /// one's current enabled/disabled state - needed to decide what reconcile
    /// still has to enable/disable rather than just what's on disk.
    ///
    /// Matches modmanager.py's list_installed_mods exactly, and both rules
    /// matter. Dot/tilde-prefixed folders are skipped: they're our own staging
    /// dirs (Mods/.&lt;id&gt;.installing), MelonLoader ignores them, and a crash
    /// mid-install can leave one behind holding a complete record - which would
    /// otherwise report a mod as installed that isn't loaded. And the result is
    /// keyed by record id, not by folder, so two folders carrying the same id
    /// (a leftover staging dir, or an operator's hand-made backup copy) collapse
    /// to one entry instead of producing a duplicate that makes callers'
    /// ToDictionary throw.</summary>
    public static List<InstalledModInfo> ListInstalledModsWithState(string gameDir)
    {
        var modsDir = ModPaths.ModsBase(gameDir);
        if (!Directory.Exists(modsDir)) return new List<InstalledModInfo>();

        var byId = new Dictionary<string, InstalledModInfo>();
        foreach (var dir in Directory.GetDirectories(modsDir))
        {
            var id = Path.GetFileName(dir);
            if (id.StartsWith(".") || id.StartsWith("~")) continue;
            var rec = ModRecord.Read(gameDir, id);
            if (rec == null) continue;
            var enabled = File.Exists(ModPaths.ModRecordPath(gameDir, id));
            byId[rec.Id] = new InstalledModInfo { Record = rec, Enabled = enabled };
        }
        return byId.Values.ToList();
    }
}

public class InstalledModInfo
{
    public ModRecord Record { get; set; }
    public bool Enabled { get; set; }
}
