using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TavernLib.Backend.Mods;

/// <summary>
/// Verified installs into Mods/&lt;id&gt;/ and UserLibs/, and reading back what is
/// there. Twin of tavern_shared/mods/install.py: records, files maps and the
/// installed/untracked sets must stay identical between the two.
/// </summary>
public static class ModInstaller
{
    private static string Sha256File(string path)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        var hash = sha.ComputeHash(stream);
        var hex = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) hex.Append(b.ToString("x2"));
        return hex.ToString();
    }

    /// <summary>Streams url to destPath within the reconcile deadline. Throws on
    /// failure; the caller deletes the partial file.</summary>
    private static void DownloadFile(NativeHttp http, string url, string destPath, DateTime deadlineUtc)
    {
        try
        {
            using var output = File.Create(destPath);
            // Nothing legitimate is bigger than an archive is allowed to extract to.
            http.Download(url, output, deadlineUtc, TimeSpan.FromSeconds(ModManagerConstants.DownloadTimeoutSeconds),
                ModManagerConstants.ZipMaxTotalUncompressed);
        }
        catch (TimeoutException)
        {
            throw new ModManagerException($"Download of {url} exceeded the reconcile pass's time budget; giving up.");
        }
        catch (Exception e) when (e is not ModManagerException)
        {
            throw new ModManagerException($"Couldn't download {url}: {ModManagerException.Describe(e)}");
        }
    }

    /// <summary>Downloads into a temp file inside workDir (same volume, so the
    /// caller's move is an atomic rename), verifies its sha256 and hands it to the
    /// caller. The temp is always removed.</summary>
    private static void VerifiedDownload(NativeHttp http, string url, string sha256, string workDir, DateTime deadlineUtc, Action<string> withTempFile)
    {
        Directory.CreateDirectory(workDir);
        var tmp = Path.Combine(workDir, $".tavern_dl_{Guid.NewGuid():N}.part");
        try
        {
            DownloadFile(http, url, tmp, deadlineUtc);
            var actual = Sha256File(tmp);
            if (!string.Equals(actual, sha256 ?? "", StringComparison.OrdinalIgnoreCase))
                throw new ModManagerException(
                    $"Downloaded file failed its checksum: expected {sha256}, got {actual}. The file may be corrupted or tampered with; nothing was installed.");
            withTempFile(tmp);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static void DownloadVerifyMove(NativeHttp http, string url, string sha256, string destPath, DateTime deadlineUtc)
    {
        var destDir = Path.GetDirectoryName(destPath);
        VerifiedDownload(http, url, sha256, destDir, deadlineUtc, tmp =>
        {
            // Move, not copy: a same-volume rename is atomic, so an interrupted write
            // can't leave a truncated file the sidecar then calls verified.
            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tmp, destPath);
        });
    }

    /// <summary>Twin of install.py's _safe_extract_zip: refuses traversal, colons
    /// and symlinks, and caps entry count and extracted size.</summary>
    private static void SafeExtractZip(string zipPath, string destDir)
    {
        var destRoot = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > ModManagerConstants.ZipMaxEntries)
            throw new ModManagerException($"This mod archive has {zip.Entries.Count} entries, over the {ModManagerConstants.ZipMaxEntries} limit; refusing to extract it.");

        // A shipped root manifest.json is skipped (by resolved path, ignoring case):
        // the record overwrites it after hashing, which would read as damage forever.
        // install.py skips the same set.
        var recordPaths = new[] { ModManagerConstants.RecordName, ModManagerConstants.DisabledRecordName }
            .Select(n => Path.Combine(destRoot, n)).ToArray();

        long written = 0;
        var buffer = new byte[81920];
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (name.StartsWith("/") || name.StartsWith("\\"))
                throw new ModManagerException($"Unsafe archive entry '{name}': absolute path.");
            // Anywhere, not just a drive letter: "mod.dll:x" is an NTFS stream, which the
            // containment check below passes.
            if (name.Contains(":"))
                throw new ModManagerException(
                    $"Unsafe archive entry '{name}': contains a colon (drive letter or NTFS alternate data stream).");

            // Symlinks are stored in the high 16 bits of ExternalAttributes as a unix
            // file-mode; 0xA000 is S_IFLNK. A symlink could point outside destDir.
            var mode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            if (mode != 0 && (mode & 0xF000) == 0xA000)
                throw new ModManagerException($"Unsafe archive entry '{name}': symlink.");

            var rel = name.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
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
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using var src = entry.Open();
            using var dst = File.Create(target);
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

    /// <summary>{relative path: sha256} for every file under baseDir. Twin of
    /// install.py's _hash_tree; forward-slash keys and lowercase hex are a
    /// cross-language invariant, since each side verifies the other's records.</summary>
    private static Dictionary<string, string> HashTree(string baseDir)
    {
        var root = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var map = new Dictionary<string, string>();
        foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetFullPath(path).Substring(root.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace('\\', '/');
            map[rel] = Sha256File(path);
        }
        return map;
    }

    /// <summary>Twin of install.py's verify_mod_files for one target: true if
    /// every file in installed.&lt;target&gt;.files is present under
    /// .&lt;target&gt;/ at its hash, false (damaged) if not, null when there is
    /// nothing to check. Null is deliberately not false: no evidence is not
    /// damage. Re-hashes on every call; reconcile memoizes per pass.</summary>
    public static bool? VerifyTarget(string gameDir, ModRecord record, string target)
    {
        var files = record?.InstalledFor(target)?.Files;
        if (files == null || files.Count == 0) return null;

        var targetDir = ModPaths.TargetDirPath(gameDir, record.Id, target);
        foreach (var entry in files)
        {
            var path = Path.Combine(targetDir, entry.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return false;
            if (!string.Equals(Sha256File(path), entry.Value ?? "", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    /// <summary>ModRecord.Read for a name off the filesystem: one oddly named folder
    /// must not take down every ping, join and reconcile.</summary>
    private static ModRecord ReadRecordSkippingUnsafe(string gameDir, string name, out bool enabled)
    {
        try
        {
            return ModRecord.Read(gameDir, name, out enabled);
        }
        catch (ModManagerException)
        {
            enabled = false;
            return null;
        }
    }

    private static void ResetDir(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
    }

    /// <summary>Twin of install.py's _clear_install_path, for replacing a whole
    /// mod: a folder of ours is emptied best effort, anything else moved to
    /// Mods/.displaced/ (a headless boot has nobody to ask, so only a recoverable
    /// answer will do).</summary>
    private static void ClearInstallPath(string gameDir, string modId)
    {
        var modDir = ModPaths.ModDirPath(gameDir, modId);
        if (!Directory.Exists(modDir)) return;

        if (ModRecord.Read(gameDir, modId) != null)
        {
            // The record stays until the new one overwrites it. Anything a
            // running game holds open is left; no longer under `installed`,
            // a dot folder of it is never loaded.
            foreach (var entry in Directory.GetFileSystemEntries(modDir))
            {
                var child = Path.GetFileName(entry);
                if (IsRecordName(child)) continue;
                try
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                    else File.Delete(entry);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    TavernLogger.Warn($"mod install: couldn't remove Mods/{modId}/{child} ({e.Message}); left it behind.");
                }
            }
            return;
        }

        var baseDir = ModPaths.DisplacedBase(gameDir);
        Directory.CreateDirectory(baseDir);
        var name = ModPaths.SafeBasename(modId);
        var dest = Path.Combine(baseDir, name);
        // Counter rather than a timestamp: displacing the same name twice has to
        // keep both, and a deterministic suffix is one a person can predict.
        for (var n = 2; Directory.Exists(dest) || File.Exists(dest); n++)
            dest = Path.Combine(baseDir, $"{name}.{n}");
        Directory.Move(modDir, dest);

        TavernLogger.Warn(
            $"mod install: Mods/{name}/ already existed and wasn't installed by this manager. "
            + $"Moved it to Mods/.displaced/{Path.GetFileName(dest)}/ rather than deleting it; "
            + "nothing loads from there, so remove it by hand once you've checked it.");
    }

    private static bool IsRecordName(string name) =>
        string.Equals(name, ModManagerConstants.RecordName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, ModManagerConstants.DisabledRecordName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Twin of install.py's install_mod: installs mod's build for target into
    /// Mods/&lt;id&gt;/.&lt;target&gt;/, assembled in staging so a failure never
    /// leaves a partial folder, then writes the record last. At the record's own
    /// version only that target is replaced; at any other version, or over a
    /// record with no `installed`, the whole mod is.
    /// </summary>
    public static void InstallMod(string gameDir, NativeHttp http, ModManifest mod, string target, DateTime deadlineUtc)
    {
        var artifact = mod.ArtifactFor(target)
            ?? throw new ModManagerException($"'{mod.Id}' {mod.Version} has no {target} build.");
        var modDir = ModPaths.ModDirPath(gameDir, mod.Id);
        var targetDir = ModPaths.TargetDirPath(gameDir, mod.Id, target);
        var staging = ModPaths.StagingPath(gameDir, mod.Id);
        ResetDir(staging);
        try
        {
            if (artifact.Package == ModManagerConstants.PackageZip)
                VerifiedDownload(http, artifact.DownloadUrl, artifact.Sha256, staging, deadlineUtc, tmp => SafeExtractZip(tmp, staging));
            else
                DownloadVerifyMove(http, artifact.DownloadUrl, artifact.Sha256, Path.Combine(staging, artifact.InstallName(mod.Id)), deadlineUtc);
            var entry = ModRecord.InstalledEntry(artifact, HashTree(staging));

            var existing = ModRecord.ReadOwned(gameDir, mod.Id, out _);
            var installed = new Dictionary<string, JToken>();
            if (existing?["installed"] is JObject kept && (string)existing["version"] == mod.Version)
            {
                // Other targets' entries are kept as written, whichever installer wrote them.
                foreach (var prop in kept.Properties().Where(p => p.Name != target))
                    installed[prop.Name] = prop.Value;
                if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
            }
            else
            {
                ClearInstallPath(gameDir, mod.Id);
            }
            Directory.CreateDirectory(modDir);
            Directory.Move(staging, targetDir);

            installed[target] = entry;
            // An install always leaves the mod enabled, as install.py's does.
            ModRecord.WriteFile(ModPaths.ModRecordPath(gameDir, mod.Id), ModRecord.Build(mod, installed));
            if (File.Exists(ModPaths.DisabledRecordPath(gameDir, mod.Id)))
                File.Delete(ModPaths.DisabledRecordPath(gameDir, mod.Id));
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        DisableShadowedLooseDlls(gameDir, mod.Id, targetDir);
    }

    /// <summary>Twin of install.py's _disable_shadowed_loose_dlls: a loose root
    /// Mods/&lt;name&gt;.dll matching a DLL this install placed in the target
    /// folder is an older hand install MelonLoader would load twice, so it's
    /// renamed aside. Never fails the install.</summary>
    private static void DisableShadowedLooseDlls(string gameDir, string modId, string targetDir)
    {
        var names = Directory.GetFiles(targetDir)
            .Select(Path.GetFileName)
            .Where(n => n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal);
        foreach (var name in names)
        {
            try
            {
                if (DisableUntrackedDll(gameDir, name))
                    TavernLogger.Warn($"mod install: Mods/{name} was a loose copy of a DLL that {modId} just installed; "
                        + "disabled it so MelonLoader doesn't load it twice. Rename it back to {name} to restore it.");
            }
            catch (ModManagerException e)
            {
                TavernLogger.Warn($"mod install: Mods/{name} looks like a loose copy of {modId}, but couldn't be "
                    + $"disabled ({e.Message}); MelonLoader may load it twice until it's removed.");
            }
        }
    }

    /// <summary>
    /// Twin of install.py's collect_library_dependencies: the libraries the
    /// closure's target builds pin, deduplicated by filename. The same filename
    /// at another sha256 is a conflict naming both pinners, whether the other is
    /// in the closure or a build recorded anywhere in this game folder that the
    /// install leaves in place, since UserLibs/ is shared by every target.
    /// </summary>
    public static List<LibraryDependency> CollectLibraryDependencies(string gameDir, IReadOnlyList<ModManifest> mods, string target)
    {
        var byFilename = new Dictionary<string, (LibraryDependency Lib, string Owner)>();
        foreach (var mod in mods)
        {
            foreach (var lib in mod.ArtifactFor(target)?.LibraryDependencies ?? new List<LibraryDependency>())
            {
                var key = ModPaths.SafeBasename(lib.Filename);
                if (!byFilename.TryGetValue(key, out var existing))
                    byFilename[key] = (lib, mod.Name);
                else
                    CheckSameLibrary(existing.Lib, existing.Owner, lib, mod.Name);
            }
        }

        // A recorded build this install replaces can't conflict: its pins go with it.
        var versions = mods.ToDictionary(m => m.Id, m => m.Version);
        foreach (var info in ScanMods(gameDir).Installed)
        {
            var rec = info.Record;
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
        if (!string.Equals(a.Sha256, b.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new ModManagerException(
                $"Library conflict on '{b.Filename}': {aOwner} pins {a.DownloadUrl} (sha {ShortSha(a.Sha256)}...) but {bOwner} pins {b.DownloadUrl} (sha {ShortSha(b.Sha256)}...). They can't both be installed.");
    }

    // install.py slices sha[:12], which never throws on a short value.
    private static string ShortSha(string sha) => sha.Length > 12 ? sha.Substring(0, 12) : sha;

    /// <summary>Same shape as InstallMod: downloads, verifies, writes
    /// UserLibs/&lt;safe filename&gt; plus its .meta.json sidecar.</summary>
    public static void InstallLibraryDependency(string gameDir, NativeHttp http, LibraryDependency lib, DateTime deadlineUtc)
    {
        var name = ModPaths.SafeBasename(lib.Filename);
        var dest = Path.Combine(ModPaths.UserLibsDir(gameDir), name);
        DownloadVerifyMove(http, lib.DownloadUrl, lib.Sha256, dest, deadlineUtc);
        ModRecord.WriteFile(ModPaths.LibrarySidecarPath(gameDir, name), new JObject
        {
            ["filename"] = name,
            ["sha256"] = lib.Sha256,
            ["download_url"] = lib.DownloadUrl,
        });
    }

    /// <summary>VerifyModFiles for a UserLibs/ library, against its sidecar's
    /// sha256; null without a sidecar.</summary>
    public static bool? VerifyLibrary(string gameDir, string filename)
    {
        var name = ModPaths.SafeBasename(filename);
        var sidecarPath = ModPaths.LibrarySidecarPath(gameDir, name);
        if (!File.Exists(sidecarPath)) return null;

        string expected;
        try
        {
            expected = JObject.Parse(File.ReadAllText(sidecarPath))["sha256"]?.ToString();
        }
        catch (Exception)
        {
            return null;
        }
        if (string.IsNullOrEmpty(expected)) return null;

        var path = Path.Combine(ModPaths.UserLibsDir(gameDir), name);
        return File.Exists(path) && string.Equals(Sha256File(path), expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Installs a resolved closure's target builds: dependencies,
    /// libraries, then the root. Root last, so a failure partway leaves new
    /// dependencies beside the old root, which still loads.</summary>
    public static void InstallModClosure(string gameDir, NativeHttp http, ModManifest root, List<ModManifest> deps, List<LibraryDependency> libs, string target, DateTime deadlineUtc)
    {
        foreach (var m in deps)
            InstallMod(gameDir, http, m, target, deadlineUtc);
        foreach (var lib in libs)
            InstallLibraryDependency(gameDir, http, lib, deadlineUtc);
        InstallMod(gameDir, http, root, target, deadlineUtc);
    }

    /// <summary>Renames the record to manifest.disabled.json so MelonLoader skips
    /// the folder. Returns whether it was enabled.</summary>
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

    /// <summary>Twin of install.py's disable_untracked_dll: renames a loose root DLL
    /// to .disabled, refusing to clobber an existing one. Returns whether it was
    /// there and enabled.</summary>
    public static bool DisableUntrackedDll(string gameDir, string filename)
    {
        var enabled = Path.Combine(ModPaths.ModsBase(gameDir), ModPaths.SafeBasename(filename));
        return MoveRefusingClobber(enabled, enabled + ModManagerConstants.DisabledDllSuffix);
    }

    private static bool MoveRefusingClobber(string src, string dest)
    {
        if (!File.Exists(src)) return false;
        if (File.Exists(dest))
            throw new ModManagerException($"'{Path.GetFileName(dest)}' already exists; remove or rename it first.");
        File.Move(src, dest);
        return true;
    }

    /// <summary>
    /// Catches an installed record up on the metadata that can change without a
    /// version bump (parity_required, scenes), writing to whichever record file
    /// the mod has so a disabled mod stays disabled. Patched in place, so fields
    /// this side doesn't model survive and key order stays install.py's.
    /// Returns whether anything changed.
    /// </summary>
    public static bool RefreshRecordMetadata(string gameDir, ModManifest manifest)
    {
        if (ModRecord.Read(gameDir, manifest.Id, out var enabled)?.Id != manifest.Id) return false;
        var path = enabled ? ModPaths.ModRecordPath(gameDir, manifest.Id) : ModPaths.DisabledRecordPath(gameDir, manifest.Id);
        var record = ModRecord.ParseObject(path);
        if (record == null) return false;

        var scenes = manifest.Scenes == null ? null : new JArray(manifest.Scenes);
        var current = record[ModScenesField.Name];
        var scenesSame = scenes == null ? current == null || current.Type == JTokenType.Null : JToken.DeepEquals(current, scenes);
        if ((bool)record[ModParityField.Name] == manifest.ParityRequired && scenesSame) return false;

        record[ModParityField.Name] = manifest.ParityRequired;
        if (scenes == null)
            record.Remove(ModScenesField.Name);
        else if (current != null)
            current.Replace(scenes);
        else   // right after parity_required, as the record's key order has it
            record.Property(ModParityField.Name).AddAfterSelf(new JProperty(ModScenesField.Name, scenes));
        ModRecord.WriteFile(path, record);
        return true;
    }

    /// <summary>
    /// One pass over Mods/: twin of install.py's list_installed_mods (Installed)
    /// and list_untracked_mods (Untracked), which must report the same sets.
    /// Dot/tilde-prefixed entries are skipped (staging dirs, which MelonLoader
    /// ignores too). Installed is keyed by record id, so two folders carrying
    /// one id collapse to one entry. A folder that isn't ours by
    /// <see cref="ModRecord.Read(string, string, out bool)"/> is untracked on
    /// the EXISTENCE of a record file, since MelonLoader loads it unread.
    /// </summary>
    public static (List<InstalledModInfo> Installed, List<UntrackedMod> Untracked) ScanMods(string gameDir)
    {
        var byId = new Dictionary<string, InstalledModInfo>();
        var untracked = new List<UntrackedMod>();
        var modsDir = ModPaths.ModsBase(gameDir);
        if (!Directory.Exists(modsDir)) return (new List<InstalledModInfo>(), untracked);

        foreach (var path in Directory.GetFiles(modsDir))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith(".") || name.StartsWith("~")) continue;
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                untracked.Add(new UntrackedMod { Name = name, Kind = UntrackedMod.KindFile, Enabled = true });
            else if (name.EndsWith(".dll" + ModManagerConstants.DisabledDllSuffix, StringComparison.OrdinalIgnoreCase))
                untracked.Add(new UntrackedMod
                {
                    Name = name.Substring(0, name.Length - ModManagerConstants.DisabledDllSuffix.Length),
                    Kind = UntrackedMod.KindFile,
                    Enabled = false,
                });
        }

        foreach (var dir in Directory.GetDirectories(modsDir))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith(".") || name.StartsWith("~")) continue;
            var rec = ReadRecordSkippingUnsafe(gameDir, name, out var enabled);
            if (rec != null)
                byId[rec.Id] = new InstalledModInfo { Record = rec, Enabled = enabled };
            else if (File.Exists(Path.Combine(dir, ModManagerConstants.RecordName)))
                untracked.Add(new UntrackedMod { Name = name, Kind = UntrackedMod.KindFolder, Enabled = true });
            else if (File.Exists(Path.Combine(dir, ModManagerConstants.DisabledRecordName)))
                untracked.Add(new UntrackedMod { Name = name, Kind = UntrackedMod.KindFolder, Enabled = false });
        }
        return (byId.Values.ToList(), untracked);
    }
}

/// <summary>Something MelonLoader loads that this manager didn't install: a name and shape only, so it's
/// never resolved, updated or held against a client.</summary>
public class UntrackedMod
{
    public const string KindFile = "file";     // a loose Mods/<name>.dll; Name is the enabled filename either way
    public const string KindFolder = "folder"; // a Mods/<name>/ folder with a foreign record

    [JsonProperty("name")] public string Name { get; set; }
    [JsonProperty("kind")] public string Kind { get; set; }
    [JsonIgnore] public bool Enabled { get; set; }
}

public class InstalledModInfo
{
    public ModRecord Record { get; set; }
    public bool Enabled { get; set; }
}
