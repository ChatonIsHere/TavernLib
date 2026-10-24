using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Alta.Networking;
using MelonLoader;
using Newtonsoft.Json;
using TavernLib.Backend.Server.Configs;
using TavernLib.Patches;
using TavernLib.Utils;
using UnityEngine;

namespace TavernLib.ModdingApi;

public sealed class RolesChange(string username, IReadOnlyList<string> oldRoles, IReadOnlyList<string> newRoles)
{
    public string Username { get; } = username;
    public IReadOnlyList<string> OldRoles { get; } = oldRoles;
    public IReadOnlyList<string> NewRoles { get; } = newRoles;
}

public static class Roles
{
    public static readonly TavernEvents.TavernEvent<RolesChange> RolesChanged = new();

    private static readonly IReadOnlyList<string> NoRoles = new List<string>().AsReadOnly();
    private static readonly string UsersFile = TavernDirectories.Users;

    private static volatile Dictionary<string, IReadOnlyList<string>> _roles = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _loadedStamp;
    private static DateTime _seenStamp;
    private static DateTime _warnedStamp;
    private static float _nextPoll;
    

    public static bool HasRole(Connection connection, string role) =>
        GetRoles(connection).Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> GetRoles(Connection connection) =>
        GetRoles(connection?.UserInfo?.Username);

    public static IReadOnlyList<string> GetRoles(string username) =>
        username != null && _roles.TryGetValue(username, out var roles) ? roles : NoRoles;


    internal static void Start()
    {
        var stamp = File.GetLastWriteTimeUtc(UsersFile);
        _seenStamp = stamp;

        try
        {
            _roles = Read();
            _loadedStamp = stamp;
            TavernLogger.Msg($"Read roles for {_roles.Count} users");
        }
        catch (Exception e)
        {
            TavernLogger.Warn($"Could not read roles from users.json: {e.Message}");
        }

        MelonEvents.OnUpdate.Subscribe(Poll);
    }

    private static void Poll()
    {
        if (Time.realtimeSinceStartup < _nextPoll) return;
        _nextPoll = Time.realtimeSinceStartup + 1f;

        var stamp = File.GetLastWriteTimeUtc(UsersFile);
        var settled = stamp == _seenStamp;
        _seenStamp = stamp;

        if (settled && stamp != _loadedStamp) Reload(stamp);
    }

    private static void Reload(DateTime stamp)
    {
        Dictionary<string, IReadOnlyList<string>> fresh;
        try
        {
            fresh = Read();
        }
        catch (Exception e)
        {
            if (stamp != _warnedStamp) TavernLogger.Warn($"Could not read users.json, keeping the roles read before: {e.Message}");
            _warnedStamp = stamp;
            return;
        }

        var old = _roles;
        _roles = fresh;
        _loadedStamp = stamp;

        foreach (var name in old.Keys.Union(fresh.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var before = old.TryGetValue(name, out var o) ? o : NoRoles;
            var after = fresh.TryGetValue(name, out var n) ? n : NoRoles;
            if (new HashSet<string>(before, StringComparer.OrdinalIgnoreCase).SetEquals(after)) continue;

            TavernLogger.Msg($"Roles for {name}: [{string.Join(", ", before)}] -> [{string.Join(", ", after)}]");
            Announce(new RolesChange(name, before, after));
        }
    }

    private static Dictionary<string, IReadOnlyList<string>> Read()
    {
        var config = JsonConvert.DeserializeObject<UserConfig>(File.ReadAllText(UsersFile))
            ?? throw new InvalidDataException("users.json is empty");

        var roles = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in config.Users)
        {
            var list = pair.Value?.Roles?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
            if (list is { Count: > 0 }) roles[pair.Key] = list.AsReadOnly();
        }

        return roles;
    }

    private static void Announce(RolesChange change)
    {
        foreach (Player player in Player.AllPlayers)
        {
            var connection = player.ConnectionToRemotePlayer;
            if (!string.Equals(connection?.UserInfo?.Username, change.Username, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                ServerSettingsRolePatch.SendRoles(connection, change.NewRoles);
            }
            catch (Exception e)
            {
                TavernLogger.Warn($"Could not send roles to {change.Username}: {e.Message}");
            }
        }

        try
        {
            RolesChanged.Invoke(change);
        }
        catch (Exception e)
        {
            TavernLogger.Error($"A RolesChanged handler threw: {e}");
        }
    }
}