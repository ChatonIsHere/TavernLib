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
/// Downloads, verifies, and installs mods/libraries into Mods/&lt;id&gt;/ and
/// UserLibs/, and manages the enable/disable-in-place state - the native-C#
/// counterpart of modmanager.py's install/uninstall/disable/enable logic,
/// reusing the exact same on-disk record convention.
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

    /// <summary>
    /// Streams url to destPath, checking the shared reconcile deadline as it
    /// goes so one slow/stalled transfer can't blow past the whole pass's
    /// wall-clock cap. Throws on failure; the caller deletes the partial file.
    /// Goes through NativeHttp rather than HttpClient: this runs before Unity's
    /// TLS provider exists, when Mono can't complete an HTTPS handshake.
    /// </summary>
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

    /// <summary>
    /// Downloads url into a temp file inside workDir (never the system temp dir,
    /// so the follow-up move is a same-volume atomic rename), verifies its
    /// sha256, then hands the temp path to the caller to move or extract. Always
    /// cleaned up, so a failed verify/move leaves nothing behind.
    /// </summary>
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
            // Move, not copy: the temp already sits in destPath's own directory
            // (VerifiedDownload guarantees that), so this is a same-volume
            // rename and therefore atomic. A copy that's interrupted partway
            // leaves a truncated file at destPath that the sidecar written
            // afterwards would claim is hash-verified.
            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tmp, destPath);
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

        // A root-level manifest.json is a natural thing for a mod to ship (it's
        // MelonLoader's folder marker too), but the install record is written
        // under that name AFTER the tree is hashed, so extracting it would
        // record bytes the record then overwrites: a permanent "damaged"
        // verdict. Matched on the resolved path, case-insensitively, so
        // "Manifest.json" and "./manifest.json" are caught too (NTFS treats
        // them as the same file). install.py's _safe_extract_zip skips the same set.
        var recordPaths = new[] { ModManagerConstants.RecordName, ModManagerConstants.DisabledRecordName }
            .Select(n => Path.Combine(destRoot, n)).ToArray();

        long written = 0;
        var buffer = new byte[81920];
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (name.StartsWith("/") || name.StartsWith("\\"))
                throw new ModManagerException($"Unsafe archive entry '{name}': absolute path.");
            // A colon anywhere, not just a drive letter in position 1: on NTFS
            // "mod.dll:payload" writes an alternate data stream hanging off
            // mod.dll rather than a file, which the containment check below
            // reads as staying inside destRoot.
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

    /// <summary>
    /// {relative path (forward slashes): sha256} for every file under baseDir,
    /// at any depth. Run over the assembled staging dir at install time, before
    /// the record is written into it, so the record itself is never part of the
    /// map.
    ///
    /// The key convention is a cross-language invariant, not a local choice:
    /// modmanager.py's _hash_tree writes forward slashes and lowercase hex, and
    /// its verify_mod_files re-joins those keys against the mod folder. A record
    /// written here with backslash keys would read as damaged on the launcher
    /// side and vice versa, so the separator is normalised rather than left as
    /// whatever Path handed back.
    /// </summary>
    private static Dictionary<string, string> HashTree(string baseDir)
    {
        // Trimmed, so the Substring below can't leave a leading separator on
        // one platform and not the other.
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

    /// <summary>
    /// Whether an installed mod's files still match what <see cref="InstallMod"/>
    /// recorded putting there - the headless half of the launcher's damaged
    /// state, and the same check modmanager.py's verify_mod_files runs against
    /// the same map.
    ///
    /// true = every recorded file is present at its recorded hash. false =
    /// something is missing or altered (antivirus ate a DLL, a write was cut
    /// short). null = nothing to check: not installed, or the record carries no
    /// files map because it predates the field. null is deliberately not false -
    /// calling a mod damaged with no evidence would reinstall every pre-existing
    /// install the first time a server booted on this build.
    ///
    /// Re-hashes on every call; reconcile memoizes the verdict for its pass.
    /// </summary>
    public static bool? VerifyModFiles(string gameDir, ModRecord record)
    {
        if (record?.Files == null || record.Files.Count == 0) return null;

        var modDir = ModPaths.ModDirPath(gameDir, record.Id);
        foreach (var entry in record.Files)
        {
            var path = Path.Combine(modDir, entry.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return false;
            if (!string.Equals(Sha256File(path), entry.Value ?? "", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    /// <summary>ModRecord.Read for a folder name that came off the filesystem
    /// rather than from a manifest. SafeBasename throws on a name a mod could
    /// never legally have, but the scanners have to survive whatever is
    /// actually sitting in Mods/ - one oddly-named folder must not take down
    /// every ping, join, and reconcile.</summary>
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

    /// <summary>
    /// Frees Mods/&lt;id&gt;/ for an install. A previous install of ours is deleted -
    /// it's being replaced, and it's already cached if anything wanted it kept.
    /// A folder we DIDN'T install is moved into Mods/.displaced/ instead, never
    /// deleted: it's an operator's own files, and a headless install runs inside
    /// OnEarlyInitializeMelon with nobody to prompt, so the only safe automatic
    /// answer is one that's recoverable.
    ///
    /// Ours vs. theirs is decided by whether a readable record is present -
    /// exactly the test both listers use, so a folder can't count as untracked
    /// for listing and as ours for deletion.
    /// </summary>
    private static void ClearInstallPath(string gameDir, string modId)
    {
        var modDir = ModPaths.ModDirPath(gameDir, modId);
        if (!Directory.Exists(modDir)) return;

        if (ModRecord.Read(gameDir, modId) != null)
        {
            Directory.Delete(modDir, recursive: true);
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

    /// <summary>
    /// Installs one mod into its own folder, Mods/&lt;id&gt;/, and writes the record +
    /// MelonLoader marker inside it. Assembled in a throwaway staging folder and
    /// swapped into place only once complete, so a failed download/verify/extract
    /// never leaves a partial or half-upgraded mod folder.
    /// </summary>
    public static void InstallMod(string gameDir, NativeHttp http, ModManifest mod, DateTime deadlineUtc)
    {
        var modDir = ModPaths.ModDirPath(gameDir, mod.Id);
        var staging = ModPaths.StagingPath(gameDir, mod.Id);
        ResetDir(staging);
        try
        {
            if (mod.Package == "zip")
                VerifiedDownload(http, mod.DownloadUrl, mod.Sha256, staging, deadlineUtc, tmp => SafeExtractZip(tmp, staging));
            else
                DownloadVerifyMove(http, mod.DownloadUrl, mod.Sha256, Path.Combine(staging, mod.InstallName()), deadlineUtc);
            // Hash everything just assembled (the record isn't written yet, so
            // it never hashes itself) and put the map in the record, so
            // VerifyModFiles - and the launcher's Mod Manager, reading the same
            // field - can later tell "still exactly what was installed" from
            // "something ate or corrupted a file".
            var record = ModRecord.FromManifest(mod);
            record.Files = HashTree(staging);
            record.WriteTo(Path.Combine(staging, ModManagerConstants.RecordName));

            ClearInstallPath(gameDir, mod.Id);
            Directory.Move(staging, modDir);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        DisableShadowedLooseDlls(gameDir, mod.Id, modDir);
    }

    /// <summary>
    /// A loose root Mods/&lt;name&gt;.dll with the same filename as a DLL this
    /// install just placed is an earlier hand-installed copy of the same mod -
    /// upstream TavernLauncher 1.8.5 drops TavernEssentials.dll there, for one -
    /// and MelonLoader would load both. Disabled rather than deleted, by the same
    /// rename as <see cref="DisableUntrackedDll"/>, so it can be turned back on.
    ///
    /// Runs after the install has landed, and never fails it: a loose copy that
    /// can't be renamed is reported and left alone. Counterpart of
    /// install.py's _disable_shadowed_loose_dlls, so both sides report the
    /// same untracked set afterwards.
    /// </summary>
    private static void DisableShadowedLooseDlls(string gameDir, string modId, string modDir)
    {
        var names = Directory.GetFiles(modDir)
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
                    ModManagerConstants.NormalizeUrl(existing.Lib.DownloadUrl) != ModManagerConstants.NormalizeUrl(lib.DownloadUrl))
                    throw new ModManagerException(
                        $"Library conflict on '{lib.Filename}': {existing.Owner} pins {existing.Lib.DownloadUrl} (sha {ShortSha(existing.Lib.Sha256)}...) but {mod.Name} pins {lib.DownloadUrl} (sha {ShortSha(lib.Sha256)}...). They can't both be installed.");
            }
        }
        return byFilename.Values.Select(v => v.Lib).ToList();
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
        var sidecar = new
        {
            filename = name,
            sha256 = lib.Sha256,
            download_url = lib.DownloadUrl,
        };
        File.WriteAllText(ModPaths.LibrarySidecarPath(gameDir, name), JsonConvert.SerializeObject(sidecar, Formatting.Indented));
    }

    /// <summary>The UserLibs/ half of <see cref="VerifyModFiles"/>: whether a
    /// pinned library still matches the sha256 its sidecar recorded. Same
    /// three answers - null with no sidecar to check against (not ours, or
    /// predates sidecars), false when the file is missing or altered, true when
    /// intact.</summary>
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

    /// <summary>
    /// Installs a mod AND its full closure: fetches the root's manifest, walks
    /// dependencies, gathers pinned libraries, and only then installs anything -
    /// every cycle/depth/diamond/library conflict is raised before anything
    /// touches disk.
    ///
    /// The root goes in LAST. Each InstallMod swaps its folder in atomically,
    /// so if a later download fails partway the worst case is a new dependency
    /// sitting beside the OLD root - still a loadable set. Root first would
    /// leave a new root on disk without the dependencies it was resolved
    /// against, while the reconciler's fallback goes on describing the old one.
    /// </summary>
    public static void InstallModClosure(string gameDir, NativeHttp http, ModManifest root, List<ModManifest> deps, List<LibraryDependency> libs, DateTime deadlineUtc)
    {
        foreach (var m in deps)
            InstallMod(gameDir, http, m, deadlineUtc);
        foreach (var lib in libs)
            InstallLibraryDependency(gameDir, http, lib, deadlineUtc);
        InstallMod(gameDir, http, root, deadlineUtc);
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
    /// Takes a loose root Mods/&lt;filename&gt; out of rotation without deleting it,
    /// by renaming it out of MelonLoader's view - the file equivalent of
    /// DisableMod's manifest rename. Returns whether it was there and enabled.
    ///
    /// Counterpart of modmanager.py's disable_untracked_dll. Unlike a folder
    /// mod there's no marker to rename, so the file itself moves; the rename is
    /// refused rather than clobbering an existing .disabled, since that file is
    /// someone else's mod and overwriting it would destroy it.
    /// </summary>
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
        else if (record["libraries"]?.Parent is { } libraries)   // install.py's _mod_record order
            libraries.AddAfterSelf(new JProperty(ModScenesField.Name, scenes));
        else if (record["files"]?.Parent is { } files)
            files.AddBeforeSelf(new JProperty(ModScenesField.Name, scenes));
        else
            record.Add(ModScenesField.Name, scenes);
        File.WriteAllText(path, record.ToString(Formatting.Indented));
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

/// <summary>A mod sitting in Mods/ that MelonLoader will load but this installer
/// doesn't own - see <see cref="ModInstaller.ScanMods"/>. Carries only
/// what can be known without trusting a foreign manifest: what it's called, what
/// shape it is, and whether it's currently loading. No id, version, or side,
/// which is exactly why untracked mods are never planned, resolved, or
/// updated.</summary>
public class UntrackedMod
{
    /// <summary>A loose root Mods/&lt;name&gt;.dll. Name includes the extension, and
    /// for a disabled one is the ENABLED name (without the .disabled suffix), so
    /// the same string toggles it either way.</summary>
    public const string KindFile = "file";

    /// <summary>A Mods/&lt;name&gt;/ folder with a foreign manifest. Name is the
    /// folder name.</summary>
    public const string KindFolder = "folder";

    public string Name { get; set; }

    /// <summary><see cref="KindFile"/> or <see cref="KindFolder"/> - the two
    /// disable in different ways. Kept the same strings modmanager.py uses, for
    /// the same reason ModManagerConstants.SideClient/SideServer are strings:
    /// both implementations have to agree on the value, not just the concept.</summary>
    public string Kind { get; set; }

    public bool Enabled { get; set; }
}

public class InstalledModInfo
{
    public ModRecord Record { get; set; }
    public bool Enabled { get; set; }
}
