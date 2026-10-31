using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TavernLib.Backend.Mods;

// Manifest v2 harness. Modes:
//   reconcile <gameDir> <repoBase> <id,id,...> [scene]   ModReconciler for the server target against repoBase only
//   snap <gameDir>                                       server snapshot as JSON {hash, count, mods, untracked}
//   select <gameDir> <target>                            MelonLoader's Mods scan, then TavernLib's selection; prints _modDirs
//   parity <gameDir> <claimJson|none>                    join verdict against the folder's server snapshot
//   verify <gameDir> <id> <target>                       VerifyTarget result
//   list <gameDir>                                       what `modmanager list` would print
//   manifest <file>                                      parse verdict, each target's URL and the dll install name
//   refresh <gameDir> <manifestFile>                     RefreshRecordMetadata
//   build <manifestFile>                                 the record ModRecord.Build writes, every target installed
//   resolve <dir> <rootId@version> <target>              ResolveDependencies over a local index (see the mode)
//   format <in.json> <out>                               ModRecord.Format of the parsed file
//   validaterepo <baseUrl>                               what `modmanager addrepo` checks before adding a repo
static class Program
{
    static readonly Assembly Lib = typeof(ModVersion).Assembly;

    static object _hook;
    static void LogReplacement(MelonLoader.Logging.ColorARGB color, string callerName, object msg, string prefix)
    {
        Console.Error.WriteLine($"    [log{(string.IsNullOrEmpty(prefix) ? "" : " " + prefix)}] {msg}");
    }

    static void PatchLogger()
    {
        var t = Lib.GetType("TavernLib.TavernLogger");
        var log = t.GetMethod("Log", BindingFlags.NonPublic | BindingFlags.Static);
        _hook = new MonoMod.RuntimeDetour.Hook(log, typeof(Program).GetMethod(nameof(LogReplacement), BindingFlags.NonPublic | BindingFlags.Static));
    }

    static int Main(string[] args)
    {
        PatchLogger();
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        switch (args.Length > 0 ? args[0] : "")
        {
            case "reconcile":
            {
                var modsList = new TavernLib.Backend.Server.Configs.ModsList { Mods = args[3].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList() };
                ModReconciler.Reconcile(args[1], modsList, [args[2]], args.Length > 4 ? args[4] : "normal");
                foreach (var m in ModInstaller.ScanMods(args[1]).Installed.OrderBy(m => m.Record.Id, StringComparer.Ordinal))
                    Console.WriteLine($"{m.Record.Id} {m.Record.Version} enabled={m.Enabled} installed=[{string.Join(",", (m.Record.Installed ?? new Dictionary<string, InstalledTarget>()).Keys.OrderBy(k => k, StringComparer.Ordinal))}]");
                return 0;
            }
            case "snap":
            {
                var (hash, mods, untracked) = ModHandshake.Snapshot(args[1]);
                var count = mods.Count;
                Console.WriteLine(JsonConvert.SerializeObject(new { hash, count, mods, untracked }, Formatting.Indented));
                return 0;
            }
            case "select":
                foreach (var dir in Select(args[1], args[2])) Console.WriteLine(dir);
                return 0;
            case "parity":
                Console.WriteLine(Parity(args[1], args[2] == "none" ? null : args[2]));
                return 0;
            case "verify":
            {
                var rec = ModRecord.Read(args[1], args[2], out _);
                Console.WriteLine(rec == null ? "not ours" : (ModInstaller.VerifyTarget(args[1], rec, args[3])?.ToString() ?? "null"));
                return 0;
            }
            case "list":
            {
                var listFor = Lib.GetType("TavernLib.ModulePorts.ModsCommandModule").GetMethod("ListFor", BindingFlags.NonPublic | BindingFlags.Static);
                Console.WriteLine(listFor.Invoke(null, new object[] { args[1] }));
                return 0;
            }
            case "manifest":
            {
                // manifest <file>: parse result
                try
                {
                    var m = ModManifest.FromJson(ModRecord_ParseFile(args[1]), "https://repo");
                    Console.WriteLine(m == null ? "skipped" : "ok server=" + (m.ArtifactFor("server")?.DownloadUrl ?? "-") + " client_windows=" + (m.ArtifactFor("client_windows")?.DownloadUrl ?? "-") + " client_android=" + (m.ArtifactFor("client_android")?.DownloadUrl ?? "-")
                                      + " name=" + (m.Artifacts.FirstOrDefault(a => a.Package == "dll")?.InstallName(m.Id) ?? "-"));
                }
                catch (ModManagerException e) { Console.WriteLine("refused: " + e.Message); }
                return 0;
            }
            case "refresh":
            {
                // refresh <gameDir> <manifestFile>
                var m = ModManifest.FromJson(ModRecord_ParseFile(args[2]), "https://repo");
                Console.WriteLine("changed=" + ModInstaller.RefreshRecordMetadata(args[1], m));
                return 0;
            }
            case "build":
            {
                // build <manifest.json>: the record ModRecord.Build writes for it, installed for every target it builds
                var m = ModManifest.FromJson(ModRecord_ParseFile(args[1]), "https://repo");
                var installed = new Dictionary<string, JToken>();
                foreach (var t in new[] { "server", "client_windows", "client_android" })
                    if (m.ArtifactFor(t) is { } a)
                        installed[t] = ModRecord.InstalledEntry(a, new Dictionary<string, string> { ["b/Z.dll"] = "ab", ["A.dll"] = "cd", ["a.dll"] = "ef" });
                var fmt = typeof(ModRecord).GetMethod("Format", BindingFlags.Static | BindingFlags.NonPublic);
                Console.WriteLine((string)fmt.Invoke(null, new object[] { ModRecord.Build(m, installed) }));
                return 0;
            }
            case "resolve":
            {
                // resolve <dir> <rootId@version> <target>: dir/index.json is {id: {major: entry}}, manifests at dir/<id>/<version>.json,
                // and an optional dir/replaced.json is a repository.json's "replaced" object
                var dir = args[1];
                var summaries = new List<ModSummary>();
                foreach (var mod in ModRecord_ParseFile(Path.Combine(dir, "index.json")).Properties())
                    foreach (var major in ((JObject)mod.Value).Properties())
                        if (ModSummary.FromJson((JObject)major.Value, mod.Name, "local") is { } s) summaries.Add(s);
                var replacedPath = Path.Combine(dir, "replaced.json");
                var index = new ModIndex(summaries, File.Exists(replacedPath)
                    ? ModRepoClient.ReplacementsFrom(new JObject { ["replaced"] = ModRecord_ParseFile(replacedPath) }, "local")
                    : new List<ModReplacement>());
                Func<string, string, string, ModManifest> fetch = (id, ver, src) =>
                {
                    Console.WriteLine($"  fetch {id} {ver} {src}");
                    return ModManifest.FromJson(ModRecord_ParseFile(Path.Combine(dir, id, ver + ".json")), "local");
                };
                var parts = args[2].Split('@');
                var root = fetch(parts[0], parts[1], "root");
                try
                {
                    var deps = ModDependencyResolver.ResolveDependencies(root, index, fetch, args[3]);
                    Console.WriteLine("ok " + string.Join(" ", deps.Select(d => d.Id + "@" + d.Version)));
                }
                catch (ModManagerException e) { Console.WriteLine($"{e.GetType().Name}: {e.Message}"); }
                return 0;
            }
            case "format":
            {
                // format <in.json> <out>: ModRecord.Format of the parsed file
                using var reader = new JsonTextReader(new StringReader(File.ReadAllText(args[1], new System.Text.UTF8Encoding(false)))) { DateParseHandling = DateParseHandling.None };
                var token = JToken.ReadFrom(reader);
                var fmt = typeof(ModRecord).GetMethod("Format", BindingFlags.Static | BindingFlags.NonPublic);
                File.WriteAllBytes(args[2], System.Text.Encoding.ASCII.GetBytes((string)fmt.Invoke(null, new object[] { token })));
                return 0;
            }
            case "validaterepo":
                try
                {
                    ModRepoClient.ValidateRepoIndex(args[1]);
                    Console.WriteLine("ok");
                }
                catch (ModManagerException e) { Console.WriteLine("refused: " + e.Message); }
                return 0;
            default:
                Console.Error.WriteLine("usage: reconcile|snap|select|parity|verify|list|manifest|refresh|build|resolve|format|validaterepo ...");
                return 2;
        }
    }

    /// <summary>Runs MelonLoader 0.7's own private FindSubFolders over
    /// gameDir/Mods (ScanForFolders' Mods half: base dir first, then the
    /// recursive scan with its exclusions), installs the result as _modDirs,
    /// then runs TavernLib's selection for target.</summary>
    static List<string> Select(string gameDir, string target)
    {
        var mlAsm = typeof(MelonLoader.MelonBase).Assembly;
        var configType = mlAsm.GetType("MelonLoader.LoaderConfig");
        var currentProp = configType.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (currentProp.GetValue(null) == null)
        {
            var config = Activator.CreateInstance(configType, nonPublic: true);
            if (currentProp.GetSetMethod(true) is { } setter) setter.Invoke(null, new[] { config });
            else configType.GetField("<Current>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, config);
        }

        var handler = mlAsm.GetType("MelonLoader.Melons.MelonFolderHandler");
        var scanType = handler.GetNestedType("ScanType", BindingFlags.NonPublic);
        var find = handler.GetMethod("FindSubFolders", BindingFlags.Static | BindingFlags.NonPublic);
        var modsDir = Path.Combine(Path.GetFullPath(gameDir), "Mods");
        var userLibs = new List<string>();
        var plugins = new List<string>();
        var mods = new List<string> { modsDir };
        var callArgs = new object[] { Enum.ToObject(scanType, 2), modsDir, true, userLibs, plugins, mods };
        find.Invoke(null, callArgs);
        var modDirsField = handler.GetField("_modDirs", BindingFlags.Static | BindingFlags.NonPublic);
        var modDirs = (List<string>)modDirsField.GetValue(null);
        modDirs.Clear();
        modDirs.AddRange((List<string>)callArgs[5]);

        var sync = Lib.GetType("TavernLib.Backend.Mods.MelonModFolders").GetMethod("Sync", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        sync.Invoke(null, new object[] { gameDir, target });
        return modDirs.ToList();
    }

    static JObject ModRecord_ParseFile(string path)
    {
        using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    static string Parity(string gameDir, string claim)
    {
        var (_, mods, _) = ModHandshake.Snapshot(gameDir);
        var mismatches = TavernLib.Backend.Auth.ModParity.CheckJoin(mods, claim);
        return mismatches.Count == 0 ? "admit" : "refuse " + JsonConvert.SerializeObject(mismatches);
    }
}
