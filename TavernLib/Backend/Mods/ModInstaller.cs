using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static TavernLib.Backend.Mods.ModManagerConstants;

namespace TavernLib.Backend.Mods;

// Twin of tavern_shared/mods/install.py: records, files maps and the installed/untracked sets must match.
public static class ModInstaller
{
    private const int ZipMaxEntries = 2000;
    // Also the cap on any one download: nothing legitimate is bigger than an archive may extract to.
    private const long ZipMaxTotalUncompressed = 500L * 1024 * 1024;
    private const int DownloadStallSeconds = 30;

    private static string Sha256File(string path)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    // The temp sits in workDir, on the destination's volume, so moving it out is an atomic rename.
    private static void VerifiedDownload(NativeHttp http, string url, string sha256, string workDir, DateTime deadlineUtc, Action<string> use)
    {
        var tmp = Path.Combine(workDir, $".tavern_dl_{Guid.NewGuid():N}.part");
        try
        {
            try
            {
                using var output = File.Create(tmp);
                http.Download(url, output, deadlineUtc, TimeSpan.FromSeconds(DownloadStallSeconds), ZipMaxTotalUncompressed);
            }
            catch (TimeoutException)
            {
                throw new ModManagerException($"Download of {url} exceeded the reconcile pass's time budget; giving up.");
            }
            catch (Exception e)
            {
                throw new ModManagerException($"Couldn't download {url}: {ModManagerException.Describe(e)}");
            }
            var actual = Sha256File(tmp);
            if (actual != sha256)
                throw new ModManagerException(
                    $"Downloaded file failed its checksum: expected {sha256}, got {actual}. The file may be corrupted or tampered with; nothing was installed.");
            use(tmp);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // Moved, not copied: an interrupted write can't leave a truncated file that a record calls verified.
    private static void DownloadVerifyMove(NativeHttp http, string url, string sha256, string destPath, DateTime deadlineUtc) =>
        VerifiedDownload(http, url, sha256, Path.GetDirectoryName(destPath), deadlineUtc, tmp =>
        {
            File.Delete(destPath);
            File.Move(tmp, destPath);
        });

    // Twin of install.py's _safe_extract_zip.
    private static void SafeExtractZip(string zipPath, string destDir)
    {
        var destRoot = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > ZipMaxEntries)
            throw new ModManagerException($"This mod archive has {zip.Entries.Count} entries, over the {ZipMaxEntries} limit; refusing to extract it.");

        // A shipped root record is skipped: ours overwrites it after hashing, which would read as damage forever.
        var recordPaths = new[] { RecordName, DisabledRecordName }.Select(n => Path.Combine(destRoot, n)).ToArray();

        long written = 0;
        var buffer = new byte[81920];
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (name.StartsWith("/") || name.StartsWith("\\"))
                throw new ModManagerException($"Unsafe archive entry '{name}': absolute path.");
            // Anywhere, not just a drive letter: "mod.dll:x" is an NTFS stream, which the containment check passes.
            if (name.Contains(":"))
                throw new ModManagerException(
                    $"Unsafe archive entry '{name}': contains a colon (drive letter or NTFS alternate data stream).");
            // The high 16 bits of ExternalAttributes are a unix file mode; S_IFLNK is 0xA000.
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new ModManagerException($"Unsafe archive entry '{name}': symlink.");

            var rel = name.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            // GetFullPath drops these, so the two installers could hash different trees.
            if (rel.Split(Path.DirectorySeparatorChar).Any(p => p is not ("" or "." or "..") && p != p.TrimEnd('.', ' ')))
                throw new ModManagerException($"Unsafe archive entry '{name}': a name ends in a dot or space.");
            var target = Path.GetFullPath(Path.Combine(destRoot, rel));
            if (target != destRoot && !target.StartsWith(destRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ModManagerException($"Unsafe archive entry '{name}': escapes the mod folder.");
            var targetNoSep = target.TrimEnd(Path.DirectorySeparatorChar);
            if (recordPaths.Any(p => string.Equals(p, targetNoSep, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (name.EndsWith("/") || name.EndsWith("\\"))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target));

            using var src = entry.Open();
            using var dst = File.Create(target);
            int read;
            while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > ZipMaxTotalUncompressed)
                    throw new ModManagerException(
                        $"This mod archive extracts to more than {ZipMaxTotalUncompressed / (1024 * 1024)} MB; refusing to extract it (possible zip bomb).");
                dst.Write(buffer, 0, read);
            }
        }
    }

    // Twin of install.py's _hash_tree; each side verifies the other's records.
    private static Dictionary<string, string> HashTree(string baseDir)
    {
        var root = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            path => Path.GetFullPath(path).Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/'),
            Sha256File);
    }

    // Twin of install.py's verify_mod_files. Null (nothing to check) is not false: no evidence is not damage.
    public static bool? VerifyTarget(string gameDir, ModRecord record, string target)
    {
        var files = record.InstalledFor(target)?.Files;
        if (files == null || files.Count == 0) return null;
        var targetDir = ModPaths.TargetDirPath(gameDir, record.Id, target);
        return files.All(f =>
        {
            var path = Path.Combine(targetDir, f.Key.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) && string.Equals(Sha256File(path), f.Value, StringComparison.OrdinalIgnoreCase);
        });
    }

    // Twin of install.py's _displace_foreign. A folder that isn't ours is moved aside, not deleted: a
    // headless boot has nobody to ask, so only a recoverable answer will do.
    private static void DisplaceForeign(string gameDir, string modId)
    {
        var modDir = ModPaths.ModDirPath(gameDir, modId);
        if (!Directory.Exists(modDir)) return;

        var baseDir = Path.Combine(ModPaths.ModsBase(gameDir), ".displaced");
        Directory.CreateDirectory(baseDir);
        var dest = Path.Combine(baseDir, modId);
        for (var n = 2; Directory.Exists(dest) || File.Exists(dest); n++)
            dest = Path.Combine(baseDir, $"{modId}.{n}");
        Directory.Move(modDir, dest);

        TavernLogger.Warn(
            $"mod install: Mods/{modId}/ already existed and wasn't installed by this manager. "
            + $"Moved it to Mods/.displaced/{Path.GetFileName(dest)}/ rather than deleting it; "
            + "nothing loads from there, so remove it by hand once you've checked it.");
    }

    // Twin of install.py's _place_target: what an install replaces is moved aside, not deleted, so a folder a
    // running game or server holds open refuses the install before anything is lost. Only the dot target
    // folders are replaced, so data a mod keeps beside them survives; with legacy, every other entry goes too.
    private static void MoveAside(string modDir, string aside, ICollection<string> keep, bool legacy, string modId)
    {
        if (Directory.Exists(aside)) Directory.Delete(aside, recursive: true);
        Directory.CreateDirectory(aside);
        var moved = new List<string>();
        try
        {
            foreach (var name in Directory.GetFileSystemEntries(modDir).Select(Path.GetFileName)
                         .Where(n => !keep.Contains(n) && (legacy || n.StartsWith("."))).OrderBy(n => n, StringComparer.Ordinal))
            {
                Move(Path.Combine(modDir, name), Path.Combine(aside, name));
                moved.Add(name);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            foreach (var name in moved) Move(Path.Combine(aside, name), Path.Combine(modDir, name));
            throw new ModManagerException($"Mods/{modId}/ is in use ({e.Message}). Close the game or server running from this folder, then try again.");
        }

        static void Move(string from, string to)
        {
            if (Directory.Exists(from)) Directory.Move(from, to);
            else File.Move(from, to);
        }
    }

    // Twin of install.py's install_mod. Assembled in staging, so a failure never leaves a partial folder.
    public static void InstallMod(string gameDir, NativeHttp http, ModManifest mod, string target, DateTime deadlineUtc)
    {
        var artifact = mod.ArtifactFor(target)
            ?? throw new ModManagerException($"'{mod.Id}' {mod.Version} has no {target} build.");
        var modDir = ModPaths.ModDirPath(gameDir, mod.Id);
        var targetDir = ModPaths.TargetDirPath(gameDir, mod.Id, target);
        // Dot-prefixed, so MelonLoader ignores it even if a crash leaves one behind.
        var staging = Path.Combine(ModPaths.ModsBase(gameDir), $".{mod.Id}.installing");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            if (artifact.Package == "zip")
                VerifiedDownload(http, artifact.DownloadUrl, artifact.Sha256, staging, deadlineUtc, tmp => SafeExtractZip(tmp, staging));
            else
                DownloadVerifyMove(http, artifact.DownloadUrl, artifact.Sha256, Path.Combine(staging, artifact.InstallName(mod.Id)), deadlineUtc);
            var entry = ModRecord.InstalledEntry(artifact, HashTree(staging));

            var existing = ModRecord.ReadOwned(gameDir, mod.Id, out _);
            if (existing == null) DisplaceForeign(gameDir, mod.Id);
            var installed = new Dictionary<string, JToken>();
            // Other targets' entries are kept as written, whichever installer wrote them.
            if (existing?["installed"] is JObject kept && (string)existing["version"] == mod.Version)
                foreach (var prop in kept.Properties().Where(p => p.Name != target))
                    installed[prop.Name] = prop.Value;
            Directory.CreateDirectory(modDir);
            var aside = Path.Combine(ModPaths.ModsBase(gameDir), $".{mod.Id}.old");
            MoveAside(modDir, aside, [RecordName, DisabledRecordName, .. installed.Keys.Select(t => "." + t)],
                existing != null && existing["installed"] is not JObject, mod.Id);
            Directory.Move(staging, targetDir);
            try { Directory.Delete(aside, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

            installed[target] = entry;
            ModRecord.WriteFile(ModPaths.ModRecordPath(gameDir, mod.Id), ModRecord.Build(mod, installed));
            File.Delete(ModPaths.DisabledRecordPath(gameDir, mod.Id));
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        DisableShadowedLooseDlls(gameDir, mod.Id, targetDir);
    }

    // Twin of install.py's _disable_shadowed_loose_dlls: an older hand install MelonLoader would load twice.
    private static void DisableShadowedLooseDlls(string gameDir, string modId, string targetDir)
    {
        var names = Directory.GetFiles(targetDir)
            .Select(Path.GetFileName)
            .Where(n => n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal);
        foreach (var name in names)
        {
            var loose = Path.Combine(ModPaths.ModsBase(gameDir), name);
            if (!File.Exists(loose)) continue;
            if (File.Exists(loose + DisabledDllSuffix))
            {
                TavernLogger.Warn($"mod install: Mods/{name} looks like a loose copy of {modId}, but couldn't be "
                    + $"disabled ('{name + DisabledDllSuffix}' already exists; remove or rename it first.); MelonLoader may load it twice until it's removed.");
                continue;
            }
            try
            {
                File.Move(loose, loose + DisabledDllSuffix);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                TavernLogger.Warn($"mod install: Mods/{name} looks like a loose copy of {modId}, but couldn't be disabled ({e.Message}); MelonLoader may load it twice until it's removed.");
                continue;
            }
            TavernLogger.Warn($"mod install: Mods/{name} was a loose copy of a DLL that {modId} just installed; "
                + $"disabled it so MelonLoader doesn't load it twice. Rename it back to {name} to restore it.");
        }
    }

    // Twin of install.py's collect_library_dependencies and _check_folder_libraries: pins recorded anywhere in
    // the folder count too, since UserLibs/ is shared by every target.
    public static List<LibraryDependency> CollectLibraryDependencies(string gameDir, IReadOnlyList<ModManifest> mods, string target)
    {
        var byFilename = new Dictionary<string, (LibraryDependency Lib, string Owner)>();
        foreach (var mod in mods)
            foreach (var lib in mod.ArtifactFor(target).LibraryDependencies)
            {
                var key = ModPaths.SafeBasename(lib.Filename);
                if (byFilename.TryGetValue(key, out var existing)) CheckSameLibrary(existing.Lib, existing.Owner, lib, mod.Name);
                else byFilename[key] = (lib, mod.Name);
            }

        // A recorded build this install replaces can't conflict: its pins go with it.
        var versions = mods.ToDictionary(m => m.Id, m => m.Version);
        foreach (var rec in ScanMods(gameDir).Installed.Select(i => i.Record))
        {
            var replacing = versions.TryGetValue(rec.Id, out var version);
            foreach (var installedTarget in rec.Installed?.Keys ?? Enumerable.Empty<string>())
            {
                if (replacing && (installedTarget == target || version != rec.Version)) continue;
                foreach (var pin in rec.InstalledLibraries(installedTarget))
                    if (byFilename.TryGetValue(pin.Filename, out var wanted))
                        CheckSameLibrary(pin, $"{rec.Name} ({installedTarget}, installed)", wanted.Lib, wanted.Owner);
            }
        }
        return byFilename.Values.Select(v => v.Lib).ToList();
    }

    private static void CheckSameLibrary(LibraryDependency a, string aOwner, LibraryDependency b, string bOwner)
    {
        if (a.Sha256 != b.Sha256)
            throw new ModManagerException(
                $"Library conflict on '{b.Filename}': {aOwner} pins {a.DownloadUrl} (sha {a.Sha256.Substring(0, 12)}...) but {bOwner} pins {b.DownloadUrl} (sha {b.Sha256.Substring(0, 12)}...). They can't both be installed.");
    }

    public static void InstallLibraryDependency(string gameDir, NativeHttp http, LibraryDependency lib, DateTime deadlineUtc)
    {
        var name = ModPaths.SafeBasename(lib.Filename);
        DownloadVerifyMove(http, lib.DownloadUrl, lib.Sha256, Path.Combine(ModPaths.UserLibsDir(gameDir), name), deadlineUtc);
        ModRecord.WriteFile(ModPaths.LibrarySidecarPath(gameDir, name), new JObject
        {
            ["filename"] = name,
            ["sha256"] = lib.Sha256,
            ["download_url"] = lib.DownloadUrl,
        });
    }

    // With wantSha256, also false for a sidecar pinning another build, as install.py's _library_present.
    public static bool? VerifyLibrary(string gameDir, string filename, string wantSha256 = null)
    {
        var name = ModPaths.SafeBasename(filename);
        var sidecar = ModRecord.ParseObject(ModPaths.LibrarySidecarPath(gameDir, name));
        var expected = sidecar?["sha256"]?.Type == JTokenType.String ? (string)sidecar["sha256"] : null;
        if (string.IsNullOrEmpty(expected)) return wantSha256 == null ? null : false;
        if (wantSha256 != null && !string.Equals(expected, wantSha256, StringComparison.OrdinalIgnoreCase)) return false;

        var path = Path.Combine(ModPaths.UserLibsDir(gameDir), name);
        return File.Exists(path) && string.Equals(Sha256File(path), expected, StringComparison.OrdinalIgnoreCase);
    }

    public static bool SetEnabled(string gameDir, string modId, bool enabled)
    {
        var (on, off) = (ModPaths.ModRecordPath(gameDir, modId), ModPaths.DisabledRecordPath(gameDir, modId));
        var (src, dest) = enabled ? (off, on) : (on, off);
        if (!File.Exists(src)) return false;
        File.Move(src, dest);
        return true;
    }

    // Catches a record up on what can change without a version bump. Patched in place, so fields this side
    // doesn't model survive and key order stays install.py's.
    public static bool RefreshRecordMetadata(string gameDir, ModManifest manifest)
    {
        var record = ModRecord.ReadOwned(gameDir, manifest.Id, out var path);
        if ((string)record?["id"] != manifest.Id) return false;

        var scenes = manifest.Scenes == null ? null : new JArray(manifest.Scenes);
        var current = record["scenes"];
        var scenesSame = scenes == null ? current == null || current.Type == JTokenType.Null : JToken.DeepEquals(current, scenes);
        if ((bool)record["parity_required"] == manifest.ParityRequired && scenesSame) return false;

        record["parity_required"] = manifest.ParityRequired;
        if (scenes == null)
            record.Remove("scenes");
        else if (current != null)
            current.Replace(scenes);
        else // right after parity_required, as the record's key order has it
            record.Property("parity_required").AddAfterSelf(new JProperty("scenes", scenes));
        ModRecord.WriteFile(path, record);
        return true;
    }

    // Twin of install.py's _scan_mods. A folder that isn't ours is untracked on its record file's existence
    // alone, since MelonLoader loads it unread.
    public static (List<InstalledModInfo> Installed, List<UntrackedMod> Untracked) ScanMods(string gameDir)
    {
        var byId = new Dictionary<string, InstalledModInfo>();
        var untracked = new List<UntrackedMod>();
        var modsDir = ModPaths.ModsBase(gameDir);
        if (!Directory.Exists(modsDir)) return ([], untracked);
        static bool Skipped(string name) => name.StartsWith(".") || name.StartsWith("~");

        foreach (var name in Directory.GetFiles(modsDir).Select(Path.GetFileName).Where(n => !Skipped(n)))
        {
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                untracked.Add(new UntrackedMod { Name = name, Kind = UntrackedMod.KindFile, Enabled = true });
            else if (name.EndsWith(".dll" + DisabledDllSuffix, StringComparison.OrdinalIgnoreCase))
                untracked.Add(new UntrackedMod { Name = name.Substring(0, name.Length - DisabledDllSuffix.Length), Kind = UntrackedMod.KindFile });
        }

        foreach (var dir in Directory.GetDirectories(modsDir))
        {
            var name = Path.GetFileName(dir);
            if (Skipped(name)) continue;
            if (ModRecord.Read(gameDir, name, out var enabled) is { } rec)
                byId[rec.Id] = new InstalledModInfo { Record = rec, Enabled = enabled };
            else if (File.Exists(Path.Combine(dir, RecordName)))
                untracked.Add(new UntrackedMod { Name = name, Kind = UntrackedMod.KindFolder, Enabled = true });
            else if (File.Exists(Path.Combine(dir, DisabledRecordName)))
                untracked.Add(new UntrackedMod { Name = name, Kind = UntrackedMod.KindFolder });
        }
        return (byId.Values.ToList(), untracked);
    }
}

// Something MelonLoader loads that we didn't install: never resolved, updated or held against a client.
public class UntrackedMod
{
    public const string KindFile = "file"; // Name is the enabled filename either way
    public const string KindFolder = "folder";

    [JsonProperty("name")] public string Name { get; set; }
    [JsonProperty("kind")] public string Kind { get; set; }
    [JsonIgnore] public bool Enabled { get; set; }
}

public class InstalledModInfo
{
    public ModRecord Record { get; set; }
    public bool Enabled { get; set; }
}
