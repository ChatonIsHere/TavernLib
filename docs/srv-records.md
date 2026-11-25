# Ports and SRV records

A server uses four ports. Players only ever need one name.

| Port | Default | Protocol | Who uses it | Set by |
|---|---|---|---|---|
| Game | 1757 | UDP | players | `/start_server -1 false <port>`, and `server_port` in `tavern_server.json` |
| Console | 1760 | TCP (websocket) | admins, the server launcher, console bridges | `console_port` |
| Web | 1761 | TCP (HTTP) | players (the forest cache) | `web_port` |
| Auth | 1762 | TCP | players (logins, mod lists, tickets) | `auth_port` |

`tavern_server.json` is in `%APPDATA%\TheModdingTavern\`. TavernLib and the server launcher both read it.
A missing or unusable value means the default.

## How a player finds the rest

The auth port is the only one a player's launcher has to find. Its ping and join replies carry
`game_port` and `web_port`, and the launcher starts the game with:
- `/dev_server_ip`;
- `/dev_server_port`;
- `/dev_server_web_port`.

TavernLib hands the web port to the game's `ConnectionInfo.WebServerPort`.

The launcher finds the auth port like this:
1. **From an SRV record.** If the player typed a name, it looks up `_townshiptale._tcp.<name>`.
2. **Otherwise 1762.** This is the same as before.

A port typed with the address still means the game port, as before. When the auth reply says otherwise,
the reply wins.

## The record

Like Minecraft's `_minecraft._tcp`, one record per server:

```
_townshiptale._tcp.play.example.com.  3600  IN  SRV  0 5 27016 box.example.com.
```

The fields are priority 0, weight 5, port 27016 (the auth port), and target `box.example.com` (an A record).
Players type `play.example.com`.
- **Priority and weight:** they work as RFC 2782 says. The lowest priority wins, and weight shares out ties.
- **No service:** a target of `.` means there is no server.

The host still forwards all three player ports: game over UDP, and web and auth over TCP.

## Two servers on one address

Give each its own set of ports and its own record:

| Server | `server_port` | `auth_port` | `web_port` | `console_port` |
|---|---|---|---|---|
| A | 1757 | 1762 | 1761 | 1760 |
| B | 27015 | 27016 | 27017 | 27018 |

The launcher keys tokens, mod lists and declines on "IP:auth port" when the auth port isn't 1762,
so the two servers stay separate.
