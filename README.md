# TwinShotNet (experimental)

## 0.2.0 update

All peers must update; protocol 0.1.0 is rejected. Hosting now opens a four-slot plugin lobby. Each player selects Pink/Orange/Purple/Blue and marks Ready; the host can start only after everyone is ready. Joining or leaving clears readiness. Empty slots are compacted at start. This is a plugin lobby, not the original character-selection UI; unlockable skins are not offered yet.

Sprite identifiers now derive from serialized asset-reference paths rather than potentially duplicated texture/sprite names. Ambiguous fallback names are omitted instead of displaying another asset. Missing sprites are counted in the status bar. Original local renderers are hidden every frame to prevent animation scripts making duplicate local art reappear. Received score/hit/alive/powerup values update client player display data, and the original score widget is refreshed.

These changes require two-machine regression testing, especially animated enemies, pickups, score, reconnect-before-start and scene transitions. Do not treat a successful build as validation of the reported visual fix.

Startup fix revision 2: both `LoadingScreen.Start` and `SteamManager.Awake` are patched. Initialization-disabled mode also suppresses the loading coroutine's Steam Deck query. Earlier startup test packages only patched SteamManager and missed the actual loading-screen startup calls. The execution marker is now `Startup reached; Steam initialization intentionally disabled.`

An experimental BepInEx 5 plugin for host-authoritative online play in Twin Shot Deluxe.

## Current model

- The host runs the original 60 Hz game simulation.
- Each peer owns exactly one player slot and sends only that slot's input.
- The client does not simulate combat. It displays compressed visual snapshots from the host.
- The room rejects mismatched game assemblies and sessions use a room key.

This is an early technical prototype. It does not yet reproduce every particle, mesh effect, sound, menu, or controller binding. Public UDP traffic is not encrypted. Do not reuse a password from another service.

## Build and deploy

```powershell
dotnet build -c Release -p:Deploy=true
```

Copy the resulting `TwinShotNet.dll` and `LiteNetLib.dll` to every player's `BepInEx/plugins/TwinShotNet` directory. The deploy command does this for the local default game installation.

## Play

1. All players start the game and remain at the main menu.
2. Press `F8`.
3. The host sets UDP port `27020`, enters a unique room key of at least 12 characters, and selects **Host**.
4. The router maps the same public UDP port to the host PC's LAN address and UDP port.
5. Friends enter the public IP/DNS, port, and identical room key, then select **Join**.
6. The host selects **Start** after all players connect.

The host uses the original game's P1 controls (Rewired keyboard/controller bindings). Clients currently use arrows or WASD, Space/Z to jump, X/J to shoot. `F8` releases gameplay input and opens the panel. Client controller support is not implemented.

## Verification and limits

### Optional Steam restart suppression

After running the updated plugin once, edit `BepInEx/config/local.twinshot.net.cfg`:

```ini
[Startup]
SkipSteamRestart = true
```

Default is `false`. Restart the game after changing it. This only suppresses the `SteamAPI.RestartAppIfNecessary` call inside `SteamManager.Awake`; Steam initialization remains intact. It does not guarantee operation without Steam or change other launch/ownership checks. Set it back to `false` to restore normal behavior. No original game DLL is modified. This optional path has not been validated with Steam fully stopped.

For a separate diagnostic that also suppresses the managed `SteamAPI.Init()` call, set `DisableSteamInitialization = true` under `[Startup]`. Default is false; it takes precedence over `SkipSteamRestart`. Initialization stays false and Steam-dependent features are unavailable. The game's `SteamAPI_Init() failed` message is expected in this mode. The log marker `SteamManager reached; Steam initialization intentionally disabled` confirms the patched method actually executed. This does not affect native launch checks before plugin loading and is not verified to provide standalone gameplay. To restore all original startup behavior, set both options to false.

Build and 11 automated input/protocol checks pass (`dotnet run --project Tests/Tests.csproj -c Release`). This does not establish successful two-instance gameplay.

All SpriteRenderer objects, including terrain, are sent as full snapshots. This avoids unsynchronized destructible terrain but uses more bandwidth than entity deltas. Missing sprites, sorting groups, special materials, mesh water, sound, local HUD hiding, scene lifecycle and final-level transitions need gameplay validation. There is no prediction or rollback: remote response includes network round-trip time and interpolation delay, and cannot currently match local latency.

Only pre-game joining is supported. Start uses Acropolis 1 and fixed player colors. Disconnected slots remain inactive until a new session. Host progress uses the original save behavior; back up your saves before testing.

Windows Firewall must permit `Twin Shot Deluxe.exe` on the selected UDP port. CGNAT connections require a VPN overlay or a UDP-capable relay/VPS; ordinary router forwarding cannot bypass CGNAT.
