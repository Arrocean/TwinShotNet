# TwinShotNet

[EN](README.md) | [中文](README.zh_CN.md)

Experimental BepInEx 5 plugin that adds host-authoritative UDP multiplayer to Twin Shot Deluxe. The current plugin and protocol version is **0.5.1**. All players must use the same plugin and game build; mismatched builds are rejected during connection.

## Architecture

The codebase layout, lifecycle, protocol constraints, and test coverage are documented in [docs/architecture.md](docs/architecture.md).

## Features

- Host runs the original game simulation at its native 60 Hz.
- Up to four players, with one isolated network input slot per player.
- Host and peers use the game's native character-selection/ready UI after the room is opened. Remote players are assigned to the next available player slot after the host's local players.
- Players can occupy slots, choose Pink, Orange, Purple, or Blue, and ready up before the host starts.
- Joining or leaving before the match clears readiness only for the affected slot; the host cannot start until every occupied slot is ready.
- The host sees remote players' joined/ready state in the native character-select screen, and the remaining remote players are repacked into the lowest free slot when someone leaves before the match starts.
- The host continues through the native theme and level-selection flow. Peers enter a synchronized, input-locked view and load the exact theme and level confirmed by the host. Random theme choices are resolved to a concrete theme before the match packet is sent.
- Host local players keep the original Rewired keyboard/controller bindings. Clients use arrows or WASD, Space/Z to jump, and X/J to shoot.
- Host state is sent as compressed visual snapshots. Clients do not independently simulate combat, collision, enemy AI, or random events.
- Snapshot packets are chunked over UDP. Clients request missing chunks with bounded NACK repair; the host keeps a short, aggregate repair cache.
- Host score, hit points, alive state, and powerup state are sent to clients and applied to the in-game player HUD. Completion-panel lifetime coins are synchronized during the native result sequence.
- Room authentication uses a shared room key and an `Assembly-CSharp.dll` fingerprint.
- The plugin can optionally suppress the game's managed Steam restart/initialization calls for standalone startup testing.

## Important limitations

This remains an experimental prototype, not a finished release. The client renders the host's visual state instead of running a local copy of the game. There is no client-side prediction or rollback, so remote input necessarily includes network delay and interpolation delay.

The snapshot currently includes eligible dynamic `SpriteRenderer` objects and player HUD state. Static map tiles and UI-layer renderers are kept local to the client's matching level and are not copied as dynamic objects. Mesh water, special materials, particle effects, sound timing, some UI, sorting groups, final-level transitions, reconnect-after-start, and every scene lifecycle have not been fully regression-tested. Ambiguous sprite fallback matches are deliberately omitted rather than replaced with a potentially incorrect asset; the client status line reports missing sprites.

The host selects the theme and level through the native game menus, using the host's save data for the native Continue/level-selection behavior. Skin selection currently exposes four base colors, not unlockable skins; a host local player who picks an unlockable skin falls back to that player's last base color. Public UDP traffic is not encrypted, so use a unique room key and do not reuse an account password.

## Installation

Install BepInEx 5.4.23.5 Windows x64 separately. Then copy this folder to every player's game directory:

```text
Twin Shot Deluxe/
└─ BepInEx/
   └─ plugins/
      └─ TwinShotNet/
         ├─ TwinShotNet.dll
         └─ LiteNetLib.dll
```

Do not copy `Assembly-CSharp.dll`, Unity DLLs, project files, PDB files, or the BepInEx distribution as part of the plugin package. All participants need the same two plugin DLLs.

## Build and deploy

From `D:\RiderProject\TwinShotNet`:

```powershell
dotnet build -c Release -p:Deploy=true
dotnet run --project Tests/Tests.csproj -c Release
```

The deploy target copies the plugin DLL and `LiteNetLib.dll` to the default local game installation. For validation without deploying, use `dotnet build TwinShotNet.csproj -c Release -p:Deploy=false`. The automated suite currently contains 417 checks: 232 snapshot, 110 packet-boundary, 15 slot-assignment, and 60 input/transport checks.

## Start a session

1. Start the game and remain at the main menu on every machine.
2. Press `F8` to open the TwinShotNet panel.
3. The host enters a UDP port, a unique room key of 12-128 characters, and clicks **Host**.
4. The host forwards that port as **UDP**, not HTTP, HTTPS, or TCP. Windows Firewall must allow the game executable to receive UDP.
5. Each friend enters the host's public IP or DNS name without `https://`, enters the public UDP port and identical room key, and clicks **Join**.
6. The native character-selection screen opens. The host's local players use the game's original keyboard/controller bindings; each remote peer operates only its assigned player slot, selects a base color, and marks **Ready**.
7. The host starts only after all occupied slots show ready. The host then chooses a theme and either continues into the native uncompleted level or selects a specific level. Peers follow the host's confirmed selection.

For an HTTPS-looking endpoint such as `https://example.invalid:13478`, the client fields must be `example.invalid` and `13478`, and the provider must explicitly offer UDP forwarding. An HTTPS tunnel cannot carry this protocol. CGNAT cannot be bypassed by ordinary router forwarding; use a UDP-capable relay or a VPN overlay.

## Steam startup options

The plugin creates `BepInEx/config/local.twinshot.net.cfg` on first launch. These options are disabled by default and require a restart:

```ini
[Startup]
SkipSteamRestart = false
DisableSteamInitialization = false
```

`SkipSteamRestart` suppresses the game's managed `SteamAPI.RestartAppIfNecessary` calls but leaves Steam initialization enabled. `DisableSteamInitialization` additionally replaces the managed Steam initialization path with a deliberately false result and takes precedence over the first option. Steam-dependent features are unavailable in that mode.

This only patches managed startup code after BepInEx loads. It does not bypass native launcher checks, ownership checks, or licensing. If standalone testing is enabled, the log should contain `DisableSteamInitialization enabled` and `Startup reached; Steam initialization intentionally disabled.` Restore both values to `false` for normal Steam behavior.

## Troubleshooting

- Check `BepInEx/LogOutput.log` for `TwinShotNet 0.5.1 loaded`.
- `Unknown Host` usually means the address contains `https://` or the endpoint is not a DNS hostname. Use hostname and port in separate fields.
- A timeout usually means the endpoint is not UDP, the port mapping is wrong, Windows Firewall is blocking the game, or the host is behind CGNAT.
- All participants must update together when the protocol version changes. Version `0.5.1` is incompatible with older protocol builds.
- `missing sprites` in the client status line indicates assets that were not safely matched, not a gameplay simulation failure.
- Stop the session from the panel before changing rooms. Joining after the match has started is not supported.

## Project status

The repository contains the plugin source, protocol implementation, asset snapshot renderer, automated protocol/input tests, and experimental startup patch. Successful compilation and unit tests do not establish complete two-machine gameplay. Regression testing should cover movement, arrows, enemies, pickups and score, player death, level completion, disconnects, and public-network latency.
