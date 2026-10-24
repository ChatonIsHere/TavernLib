using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using TavernLib.Backend.Server.Configs;

namespace TavernLib.Backend.Server;

/// <summary>
/// Timed zip backups of the server's data folder (Servers\-1) for servers run
/// without TavernLauncher. TavernLauncher does the same job itself for the
/// servers it manages, so this stays off when it is in charge (see
/// Tavern.SetupServices). Keep the naming, the left-out folders and the
/// retention rule identical to the launcher's server_backup.py.
///
/// A backup is ServerBackup_yyyy-MM-dd_HH-mm-ss.zip in a Backups folder inside
/// the data folder. It holds a single top-level folder named after the data
/// folder ("-1"), so extracting it into the Servers folder restores the files.
/// </summary>
public static class ServerBackup
{
    private const string BackupFolderName = "Backups";
    private const string NamePrefix = "ServerBackup_";
    private const string StampFormat = "yyyy-MM-dd_HH-mm-ss";

    // Top-level folders that are not part of a backup: the backups themselves
    // (or every backup would contain all the earlier ones) and the logs, which
    // are diagnostics rather than server state and can be large.
    private static readonly string[] ExcludedFolders = { "Backups", "Logs" };

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

    private static System.Threading.Timer _timer;
    private static int _busy;
    private static string _lastSettingsError;

    public static string ServerDirectory => Path.Combine(TavernDirectories.ATTSave, "Servers", "-1");

    internal sealed class BackupResult
    {
        public string ZipPath;
        public int FilesAdded;
        public int FilesSkipped;
        public long SizeBytes;
    }

    /// <summary>
    /// Checks once a minute whether a backup is due. The settings are read
    /// from server_settings.json on every check, so editing them takes effect
    /// without restarting the server.
    /// </summary>
    internal static void Start()
    {
        if (_timer != null) return;

        float hours;
        int maxBackups;
        if (TryReadSettings(out hours, out maxBackups) && hours > 0)
        {
            var every = hours.ToString("0.##", CultureInfo.InvariantCulture);
            var keep = maxBackups > 0 ? $", keeping the newest {maxBackups}" : "";
            TavernLogger.Msg($"Automatic server backups every {every}h{keep} in {Path.Combine(ServerDirectory, BackupFolderName)}");
        }

        _timer = new System.Threading.Timer(_ => Tick(), null, CheckInterval, CheckInterval);
    }

    internal static void Tick()
    {
        // A backup can take longer than the check interval; never run two at once.
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            float hours;
            int maxBackups;
            if (!TryReadSettings(out hours, out maxBackups) || hours <= 0) return;

            var serverDir = ServerDirectory;
            if (!IsDue(serverDir, hours, DateTime.UtcNow)) return;

            var result = CreateBackup(serverDir, DateTime.Now);
            TavernLogger.Msg($"Server backup saved: {Path.GetFileName(result.ZipPath)} ({FormatSize(result.SizeBytes)}, {result.FilesAdded} files)");
            if (result.FilesSkipped > 0)
                TavernLogger.Warn($"{result.FilesSkipped} file(s) were in use and left out of the backup.");

            var removed = Prune(serverDir, maxBackups);
            if (removed > 0)
                TavernLogger.Msg($"Removed {removed} old server backup(s) (keeping {maxBackups}).");
        }
        catch (Exception e)
        {
            TavernLogger.Error($"Server backup failed: {e.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static bool TryReadSettings(out float hours, out int maxBackups)
    {
        hours = 0;
        maxBackups = 0;
        try
        {
            var settings = JsonConvert.DeserializeObject<ServerSettings>(File.ReadAllText(TavernDirectories.ServerSettings));
            if (settings == null) return false;

            hours = settings.ServerBackupTime;
            maxBackups = settings.ServerBackupMaxBackups;
            if (float.IsNaN(hours) || float.IsInfinity(hours) || hours < 0) hours = 0;
            if (maxBackups < 0) maxBackups = 0;
            _lastSettingsError = null;
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception e)
        {
            // A locked file or a typo in server_settings.json: say so once, not every minute.
            if (e.Message != _lastSettingsError)
            {
                _lastSettingsError = e.Message;
                TavernLogger.Warn($"Server backups: could not read server_settings.json ({e.Message})");
            }
            return false;
        }
    }

    /// <summary>The finished backups, oldest first.</summary>
    internal static string[] ListBackups(string serverDir)
    {
        var folder = Path.Combine(serverDir, BackupFolderName);
        if (!Directory.Exists(folder)) return new string[0];

        return Directory.GetFiles(folder, NamePrefix + "*.zip")
            .Where(p => p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => File.GetLastWriteTimeUtc(p))
            .ThenBy(p => p, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// True if backups are on (hours greater than 0) and the newest one is at
    /// least that old, or there is none yet.
    /// </summary>
    internal static bool IsDue(string serverDir, float hours, DateTime nowUtc)
    {
        if (hours <= 0) return false;

        var backups = ListBackups(serverDir);
        if (backups.Length == 0) return true;

        var newest = File.GetLastWriteTimeUtc(backups[backups.Length - 1]);
        return nowUtc - newest >= TimeSpan.FromHours(hours);
    }

    /// <summary>
    /// Zips the server folder into its Backups folder. Files that are open in
    /// another program are left out; any other problem is thrown and leaves no
    /// partial zip behind.
    /// </summary>
    internal static BackupResult CreateBackup(string serverDir, DateTime now)
    {
        if (!Directory.Exists(serverDir))
            throw new DirectoryNotFoundException("Server folder not found: " + serverDir);

        var root = serverDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootName = Path.GetFileName(root);
        var folder = Path.Combine(root, BackupFolderName);
        Directory.CreateDirectory(folder);

        // What an earlier backup left behind if it was interrupted mid-zip.
        foreach (var stale in Directory.GetFiles(folder, NamePrefix + "*.zip.tmp"))
        {
            try { File.Delete(stale); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var stamp = now.ToString(StampFormat, CultureInfo.InvariantCulture);
        var zipPath = Path.Combine(folder, NamePrefix + stamp + ".zip");
        for (var n = 2; File.Exists(zipPath); n++)
            zipPath = Path.Combine(folder, NamePrefix + stamp + "_" + n + ".zip");
        var tempPath = zipPath + ".tmp";

        var result = new BackupResult { ZipPath = zipPath };
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            // UTF-8 names explicitly: older .NET Framework versions would otherwise store non-ASCII names in the system code page
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, false, Encoding.UTF8))
            {
                foreach (var file in EnumerateBackupFiles(root))
                {
                    FileStream source;
                    try
                    {
                        // The game keeps some files open while it runs; allow them to be read.
                        source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    }
                    catch (IOException)
                    {
                        result.FilesSkipped++;  // in use by another program, or gone since it was listed
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        result.FilesSkipped++;
                        continue;
                    }

                    using (source)
                    {
                        var entryName = rootName + "/" + file.Substring(root.Length + 1).Replace('\\', '/');
                        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                        entry.LastWriteTime = ZipTime(File.GetLastWriteTime(file));
                        using (var target = entry.Open())
                            source.CopyTo(target);
                    }
                    result.FilesAdded++;
                }
            }

            File.Move(tempPath, zipPath);
        }
        catch
        {
            try { File.Delete(tempPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }

        result.SizeBytes = new FileInfo(zipPath).Length;
        return result;
    }

    /// <summary>
    /// Deletes the oldest backups so that at most maxBackups remain (0 means
    /// keep everything). Only files matching our own naming are ever touched.
    /// Returns how many were removed.
    /// </summary>
    internal static int Prune(string serverDir, int maxBackups)
    {
        if (maxBackups <= 0) return 0;

        var backups = ListBackups(serverDir);
        var removed = 0;
        for (var i = 0; i < backups.Length - maxBackups; i++)
        {
            try
            {
                File.Delete(backups[i]);
                removed++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    private static IEnumerable<string> EnumerateBackupFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            var isRoot = string.Equals(dir, root, StringComparison.OrdinalIgnoreCase);

            string[] files;
            string[] subDirectories;
            try
            {
                files = Directory.GetFiles(dir);
                subDirectories = Directory.GetDirectories(dir);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var file in files) yield return file;

            foreach (var sub in subDirectories)
            {
                var name = Path.GetFileName(sub);
                if (isRoot && ExcludedFolders.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) continue;
                pending.Push(sub);
            }
        }
    }

    // Zip cannot store dates outside 1980-2107.
    private static DateTimeOffset ZipTime(DateTime time)
    {
        if (time.Year < 1980) time = new DateTime(1980, 1, 1);
        else if (time.Year > 2107) time = new DateTime(2107, 12, 31);
        return new DateTimeOffset(time);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024 * 1024) return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        return Math.Max(1, bytes / 1024) + " KB";
    }
}
