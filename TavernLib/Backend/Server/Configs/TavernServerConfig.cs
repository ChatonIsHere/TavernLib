﻿using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TavernLib.Backend.Server.Configs;

public class TavernServer
{
    [JsonProperty(PropertyName = "server_port")] public int ServerPort { get; private set; }

    // The game's other ports, for hosts that can't have the defaults (two servers on one address,
    // or a panel that hands out ports). Players never type these: the auth port's replies carry the
    // game and web ports, and an SRV record can carry the auth port (see docs/srv-records.md).
    [JsonProperty(PropertyName = "auth_port")] public int AuthPort { get; private set; } = DefaultAuthPort;
    [JsonProperty(PropertyName = "web_port")] public int WebPort { get; private set; } = DefaultWebPort;
    [JsonProperty(PropertyName = "console_port")] public int ConsolePort { get; private set; } = DefaultConsolePort;

    [JsonExtensionData] private IDictionary<string, JToken> Extra { get; set; }

    public const int DefaultGamePort = 1757;
    public const int DefaultConsolePort = 1760;
    public const int DefaultWebPort = 1761;
    public const int DefaultAuthPort = 1762;

    // A missing, zero or out-of-range port in the file means the default.
    public static int Valid(int port, int fallback) => port is > 0 and <= 65535 ? port : fallback;

    [JsonIgnore] public int Game => Valid(ServerPort, DefaultGamePort);
    [JsonIgnore] public int Auth => Valid(AuthPort, DefaultAuthPort);
    [JsonIgnore] public int Web => Valid(WebPort, DefaultWebPort);
    [JsonIgnore] public int Console => Valid(ConsolePort, DefaultConsolePort);
}

public class TavernServerConfig(string filePath) : ServerConfigFile<TavernServer>(filePath);
