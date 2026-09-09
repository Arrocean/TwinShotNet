using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TwinShotNet;

[BepInPlugin("local.twinshot.net", "Twin Shot Net (Experimental)", "0.5.0")]
public sealed class Plugin : BaseUnityPlugin
{
    internal static Plugin Instance;
    internal bool Client => _network != null && !_host;
    internal bool Hosting => _network != null && _host;
    private NetManager _network;
    private NetPeer _server;
    private readonly Dictionary<NetPeer, int> _peers = new();
    private readonly InputSlot[] _inputs = { new(), new(), new(), new() };
    private readonly float[] _lastInput = new float[4];
    private readonly byte[] _applied = new byte[4];
    private FrameAssembler _assembler = new();
    private FrameCache _frameCache = new();
    private readonly Replica _replica = new();
    private Harmony _harmony;
    private Harmony _startupHarmony;
    private bool _host, _started, _themePhase, _visible = true, _clientSceneReady;
    private bool _oldBackground, _backgroundOwned, _quitting;
    internal bool PanelVisible => _visible;
    private string _address = "127.0.0.1", _port = "27020", _password = "", _status = "Offline", _fingerprint;
    private int _assigned, _sequence, _players = 2, _hostLocalPlayers = 1;
    private float _nextInput, _nextSnapshot, _lastFrame;
    private int _lastBroadcastCoins = -1;
    private float _nextLobbySync;
    private byte _lastSent;
    private byte[] _pendingFrame;
    private Rect _window = new(20, 40, 430, 360);
    private float _snapshotHz;
    private readonly int[] _skins = { 0, 1, 2, 3 };
    private readonly bool[] _occupied = new bool[4];
    private readonly bool[] _ready = new bool[4];
    internal bool InLobby => _network != null && !_started;
    internal bool ThemePhase => _themePhase;

    private void Awake()
    {
        Instance = this;
        _address = Config.Bind("Network", "Address", "127.0.0.1", "Host public IP or DNS name").Value;
        _port = Config.Bind("Network", "Port", 27020, "UDP listen/destination port").Value.ToString();
        _password = Config.Bind("Network", "RoomKey", "", "Use a long unique room key; UDP transport is not encrypted")
            .Value;
        _snapshotHz =
            Mathf.Clamp(Config.Bind("Network", "SnapshotHz", 30, "Full visual snapshots per second (10-60)").Value, 10,
                60);
        using var hash = SHA256.Create();
        using var file = File.OpenRead(typeof(Game).Assembly.Location);
        _fingerprint = BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "");
        _harmony = new Harmony("local.twinshot.net");
        _harmony.PatchAll(typeof(Plugin).Assembly);
        _startupHarmony = new Harmony("local.twinshot.net.startup");
        if (Config.Bind("Startup", "DisableSteamInitialization", false,
                "Experimental: disable managed Steam initialization and restart requests. Steam features become unavailable. Restart required; does not affect native launch checks.")
            .Value)
        {
            try
            {
                _startupHarmony.Patch(AccessTools.Method(typeof(SteamManager), "Awake"),
                    transpiler: new HarmonyMethod(typeof(SteamRestartPatch),
                        nameof(SteamRestartPatch.TranspileOffline)));
                _startupHarmony.Patch(AccessTools.Method(typeof(LoadingScreen), "Start"),
                    transpiler: new HarmonyMethod(typeof(SteamRestartPatch),
                        nameof(SteamRestartPatch.TranspileOffline)));
                _startupHarmony.Patch(
                    AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(LoadingScreen), "Sequence")),
                    transpiler: new HarmonyMethod(typeof(SteamRestartPatch),
                        nameof(SteamRestartPatch.TranspileDeckQuery)));
                Logger.LogWarning(
                    "DisableSteamInitialization enabled: managed restart and Init calls disabled. Steam features unavailable.");
            }
            catch (Exception ex)
            {
                _startupHarmony.UnpatchSelf();
                Logger.LogError("Optional Steam initialization patches rolled back: " + ex);
            }
        }
        else if (Config.Bind("Startup", "SkipSteamRestart", false,
                     "Experimental: skip the game's Steam automatic restart request. Restart required. Steam initialization and other checks remain unchanged.")
                 .Value)
        {
            try
            {
                _startupHarmony.Patch(AccessTools.Method(typeof(SteamManager), "Awake"),
                    transpiler: new HarmonyMethod(typeof(SteamRestartPatch), nameof(SteamRestartPatch.Transpile)));
                _startupHarmony.Patch(AccessTools.Method(typeof(LoadingScreen), "Start"),
                    transpiler: new HarmonyMethod(typeof(SteamRestartPatch), nameof(SteamRestartPatch.Transpile)));
                Logger.LogWarning(
                    "SkipSteamRestart enabled. Steam initialization is unchanged; standalone startup is not guaranteed.");
            }
            catch (Exception ex)
            {
                _startupHarmony.UnpatchSelf();
                Logger.LogError("Optional Steam restart patches rolled back: " + ex);
            }
        }

        Logger.LogInfo("TwinShotNet 0.5.0 loaded. F8 opens the network panel.");
    }

    private void Open(bool asHost)
    {
        if (!int.TryParse(_port, out int number) || number < 1024 || number > 65535)
        {
            _status = "Port must be 1024-65535";
            return;
        }

        if (_password.Length < 12 || _password.Length > 128)
        {
            _status = "Room key must be 12-128 characters";
            return;
        }

        if (Game.instance != null)
        {
            _status = "Return to the main menu before connecting";
            return;
        }

        if (MenuScene.instance == null)
        {
            _status = "Wait for the main menu";
            return;
        }

        _host = asHost;
        _hostLocalPlayers = asHost ? HostLocalPlayerCount() : 1;
        Array.Clear(_ready, 0, 4);
        Array.Clear(_occupied, 0, 4);
        _occupied[0] = asHost;
        _visible = true;
        _oldBackground = Application.runInBackground;
        _backgroundOwned = true;
        Application.runInBackground = true;
        var listener = new EventBasedNetListener();
        _network = new NetManager(listener) { AutoRecycle = true, IPv6Enabled = false, DisconnectTimeout = 5000 };
        listener.ConnectionRequestEvent += request =>
        {
            if (!_host || _started || _peers.Count >= 3)
            {
                request.Reject();
                return;
            }

            request.AcceptIfKey(Wire.Version + ":" + _fingerprint + ":" + _password);
        };
        listener.PeerConnectedEvent += peer =>
        {
            if (_host)
            {
                _hostLocalPlayers = HostLocalPlayerCount();
                int slot = Enumerable.Range(_hostLocalPlayers, 4 - _hostLocalPlayers)
                    .FirstOrDefault(n => !_peers.Values.Contains(n));
                if (slot == 0 || _started)
                {
                    peer.Disconnect();
                    return;
                }

                _peers.Add(peer, slot);
                _occupied[slot] = true;
                _ready[slot] = false;
                _inputs[slot].Clear();
                _lastInput[slot] = Time.realtimeSinceStartup;
                var writer = new NetDataWriter();
                writer.Put((byte)1);
                writer.Put((byte)(slot + 1));
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
                _status = $"Hosting: {_hostLocalPlayers + _peers.Count}/4 players";
                BroadcastLobby();
            }
            else
            {
                _server = peer;
                _status = "Connected; waiting for host";
            }
        };
        listener.PeerDisconnectedEvent += (peer, info) =>
        {
            if (_host)
            {
                if (_peers.TryGetValue(peer, out int slot))
                {
                    _inputs[slot].Clear();
                    _peers.Remove(peer);
                    _occupied[slot] = false;
                    _ready[slot] = false;
                    if (!_started) BroadcastLobby();
                }

                _status = "Player disconnected; slot input released";
            }
            else
            {
                _status = "Disconnected: " + info.Reason + ". Stop to return to menu.";
                _server = null;
                _assigned = 0;
            }
        };
        listener.NetworkReceiveEvent += Receive;
        listener.NetworkErrorEvent += (_, error) =>
        {
            _status = "Network error: " + error;
            Logger.LogWarning(_status);
        };
        try
        {
            if (!_network.Start(_host ? number : 0)) throw new IOException("Unable to bind UDP socket");
            if (_host) _status = $"Listening UDP {number}. Waiting for friends.";
            else
            {
                _network.Connect(_address.Trim(), number, Wire.Version + ":" + _fingerprint + ":" + _password);
                _status = "Connecting...";
            }
        }
        catch (Exception ex)
        {
            Close(false);
            _status = ex.Message;
            Logger.LogError(ex);
        }
    }

    private void Receive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        try
        {
            if (_host ? !_peers.ContainsKey(peer) : !ReferenceEquals(peer, _server)) return;
            if (reader.AvailableBytes < 1) return;
            byte type = reader.GetByte();
            int bodyPosition = reader.Position;
            if (!PacketValidation.IsValid(type, reader.GetRemainingBytes())) return;
            reader.SetPosition(bodyPosition);
            if (_host)
            {
                if (!_peers.TryGetValue(peer, out int slot)) return;
                if (type == Wire.NackPacketType)
                {
                    if (!_started || method != DeliveryMethod.Unreliable || reader.AvailableBytes < 8 ||
                        reader.AvailableBytes > 6 + 2 * Wire.MaxRepairChunks) return;
                    if (!Wire.TryDecodeNack(reader.GetRemainingBytes(), out var request)) return;
                    foreach (var chunk in _frameCache.Serve(request, Wire.NowMilliseconds))
                    {
                        var writer = new NetDataWriter();
                        writer.Put((byte)4);
                        writer.Put(chunk.Sequence);
                        writer.Put((ushort)chunk.Index);
                        writer.Put((ushort)chunk.Total);
                        writer.Put(chunk.Payload);
                        peer.Send(writer, DeliveryMethod.Unreliable);
                    }

                    return;
                }

                if (type == 5 && !_started && reader.AvailableBytes == 2)
                {
                    int skin = reader.GetByte();
                    bool isReady = reader.GetBool();
                    if (skin > 3) return;
                    if (_skins[slot] != skin)
                    {
                        _skins[slot] = skin;
                        _ready[slot] = false;
                    }
                    else _ready[slot] = isReady;

                    BroadcastLobby();
                    return;
                }

                if (type != 2 || reader.AvailableBytes != 1) return;
                byte value = reader.GetByte();
                _inputs[slot].Receive(value);
                _lastInput[slot] = Time.realtimeSinceStartup;
                return;
            }

            if (!ReferenceEquals(peer, _server)) return;
            if (type == 6 && !_started)
            {
                for (int i = 0; i < 4; i++)
                {
                    _occupied[i] = reader.GetBool();
                    _skins[i] = reader.GetByte();
                    _ready[i] = reader.GetBool();
                    if (_skins[i] > 3) throw new InvalidDataException("Invalid lobby skin");
                }

                _visible = true;
                SyncNativeLobby();
                return;
            }

            if (type == 1)
            {
                _assigned = reader.GetByte();
                if (_assigned < 2 || _assigned > 4) throw new InvalidDataException("Invalid player slot");
                _status = $"Player {_assigned}; waiting for host";
            }
            else if (type == 3) throw new InvalidDataException("Obsolete match packet");
            else if (type == 8 && !_started)
            {
                if (reader.AvailableBytes < 7) throw new InvalidDataException("Truncated match packet");
                Theme theme = (Theme)reader.GetByte();
                int level = reader.GetInt();
                if (!Enum.IsDefined(typeof(Theme), theme)) throw new InvalidDataException("Invalid match theme");
                if (level < 1 || level > 100) throw new InvalidDataException("Invalid match level");
                var selectedLevel = new LevelId(theme, level);
                if (!selectedLevel.DoesExist()) throw new InvalidDataException("Host selected an unavailable level");
                _players = reader.GetByte();
                _assigned = reader.GetByte();
                if (_players < 2 || _players > 4 || _assigned < 2 || _assigned > _players)
                    throw new InvalidDataException("Invalid match players");
                if (reader.AvailableBytes != _players) throw new InvalidDataException("Invalid match packet size");
                for (int i = 0; i < _players; i++) _skins[i] = reader.GetByte();
                _started = true;
                _themePhase = false;
                ConfigureGame(_players, theme, level);
                SceneManager.LoadScene("Game");
                _lastFrame = Time.realtimeSinceStartup;
            }
            else if (type == 9 && !_started)
            {
                if (reader.AvailableBytes != 0) throw new InvalidDataException("Invalid theme phase packet");
                _themePhase = true;
                _lastSent = 0;
                ShowNativeThemeSelect();
                _visible = true;
                _status = "Waiting for host to choose a theme and level";
            }
            else if (type == 10 && _started)
            {
                if (reader.AvailableBytes != 4) throw new InvalidDataException("Invalid completion packet");
                int coins = reader.GetInt();
                if (coins < 0) throw new InvalidDataException("Invalid completion coins");
                ApplyCompletionCoins(coins);
            }
            else if (type == 4 && _started)
            {
                int id = reader.GetInt();
                int part = reader.GetUShort();
                int total = reader.GetUShort();
                var data = reader.GetRemainingBytes();
                byte[] frame = _assembler.Add(id, part, total, data);
                if (frame != null)
                {
                    _pendingFrame = Wire.Unpack(frame);
                    _lastFrame = Time.realtimeSinceStartup;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Rejected network packet: " + ex.Message);
            peer.Disconnect();
        }
    }

    private void ConfigureGame(int count, Theme theme, int level)
    {
        Game.isUsingTouchControls = false;
        Game.playerSkins = _skins.Take(count).Select(s => (Player.Skin)s).ToArray();
        Game.themeChoice = theme;
        Game.levelId = new LevelId(theme, level);
    }

    private void StartMatch()
    {
        SyncHostLocalState();
        if (_peers.Count == 0)
        {
            _status = "At least one friend must connect first";
            return;
        }

        if (Enumerable.Range(0, 4).Any(i => _occupied[i] && !_ready[i]))
        {
            _status = "Waiting for everyone to ready";
            return;
        }

        int localPlayers = _hostLocalPlayers;
        _players = localPlayers + _peers.Count;
        if (_players > 4)
        {
            _status = "Maximum of four total players reached";
            return;
        }

        foreach (var input in _inputs) input.Clear();
        ShowNativeThemeSelect();
        _themePhase = true;
        foreach (var peer in _peers.Keys)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)9);
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        _status = "Choose a theme and level";
    }

    private static bool TryReadLobbyValue(object value, out int result)
    {
        // Native state/skin fields may be integer-backed enums; null is not slot/state zero.
        if (value is int integer)
        {
            result = integer;
            return true;
        }

        if (value is Enum enumeration)
        {
            result = Convert.ToInt32(enumeration);
            return true;
        }

        result = 0;
        return false;
    }

    private void SyncHostLocalState()
    {
        if (!_host) return;
        // Readiness must be confirmed on every sync, never retained after a failed read.
        for (var i = 0; i < _hostLocalPlayers; i++) _ready[i] = false;
        try
        {
            var scene = MenuScene.instance;
            var menu = scene?.GetType().GetField("characterSelectMenu",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(scene);
            var boxes = menu?.GetType()
                .GetField("boxes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(menu) as Array;
            if (boxes != null)
                foreach (var box in boxes)
                {
                    if (box == null) continue;
                    var type = box.GetType();
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    var numberField = type.GetField("number", flags);
                    if (numberField == null) continue;
                    if (numberField.GetValue(box) is not int number) continue;
                    if (number < 1 || number > _hostLocalPlayers) continue;
                    var stateField = type.GetField("state", flags);
                    if (stateField == null) continue;
                    if (!TryReadLobbyValue(stateField.GetValue(box), out var state)) continue;
                    var skinField = type.GetField("selectedSkin", flags);
                    if (skinField == null) continue;
                    if (!TryReadLobbyValue(skinField.GetValue(box), out var skin)) continue;
                    if (state < 0 || state > 2 || skin < 0 || skin > 3) continue;
                    var slot = number - 1;
                    _occupied[slot] = state != 0;
                    _skins[slot] = skin;
                    _ready[slot] = state == 2;
                }
        }
        catch (Exception ex)
        {
            for (var i = 0; i < _hostLocalPlayers; i++) _ready[i] = false;
            Logger.LogDebug("Native host lobby state unavailable: " + ex.Message);
        }

        for (int i = 0; i < 4; i++)
            if (!_peers.Values.Contains(i))
            {
                if (!_occupied[i]) _ready[i] = false;
            }

        BroadcastLobby();
    }

    internal void StartSelectedMatch(Theme theme, LevelId level)
    {
        _started = true;
        _themePhase = false;
        foreach (var input in _inputs) input.Clear();
        foreach (var entry in _peers)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)8);
            writer.Put((byte)theme);
            writer.Put(level.LevelNumberWithinTheme);
            writer.Put((byte)_players);
            writer.Put((byte)(entry.Value + 1));
            for (int i = 0; i < _players; i++) writer.Put((byte)_skins[i]);
            entry.Key.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        ConfigureGame(_players, theme, level.LevelNumberWithinTheme);
        SceneManager.LoadScene("Game");
        _visible = false;
        _status = "Host / P1";
    }

    private void ApplyCompletionCoins(int coins)
    {
        try
        {
            var panel = Game.instance?.ui?.levelCompletePanel;
            var type = panel?.GetType();
            type?.GetField("lifetimeCoins", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.SetValue(panel, coins);
            var text = type?.GetField("textLifetimeCoins",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(panel);
            text?.GetType().GetProperty("text")?.SetValue(text, coins.ToString());
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Completion coin UI update unavailable: " + ex.Message);
        }
    }

    internal void BroadcastCompletionCoins(int coins)
    {
        if (!Hosting || coins < 0) return;
        if (coins == _lastBroadcastCoins) return;
        _lastBroadcastCoins = coins;
        var writer = new NetDataWriter();
        writer.Put((byte)10);
        writer.Put(coins);
        foreach (var peer in _peers.Keys) peer.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    private static int HostLocalPlayerCount()
    {
        try
        {
            var scene = MenuScene.instance;
            var menu = scene?.GetType().GetField("characterSelectMenu",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(scene);
            var boxes = menu?.GetType()
                .GetField("boxes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(menu) as Array;
            if (boxes == null) return 1;
            int count = 0;
            foreach (var box in boxes)
            {
                var state = box?.GetType()
                    .GetField("state", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(box);
                if (state != null && Convert.ToInt32(state) != 0) count++;
            }

            return Mathf.Clamp(count, 1, 4);
        }
        catch
        {
            return 1;
        }
    }

    private static void ShowNativeThemeSelect()
    {
        var menuScene = MenuScene.instance;
        var menu = menuScene?.GetType()
            .GetField("themeSelectMenu", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(menuScene);
        menu?.GetType().GetMethod("Show", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.Invoke(menu, null);
    }

    private static byte ReadKeys()
    {
        if (!Application.isFocused) return 0;
        byte value = 0;
        if (Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.A)) value |= 1;
        if (Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.D)) value |= 2;
        if (Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.W)) value |= 4;
        if (Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.S)) value |= 8;
        if (Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.Z)) value |= 16;
        if (Input.GetKey(KeyCode.X) || Input.GetKey(KeyCode.J)) value |= 32;
        return value;
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F8)) _visible = !_visible;
        if (Client && !_started && !_themePhase && _assigned > 0 && Input.GetKeyDown(KeyCode.Space))
            InvokeClientJoin();
        _network?.PollEvents();
        if (_network == null) return;
        float now = Time.realtimeSinceStartup;
        double transportNow = Wire.NowMilliseconds;
        if (_host) _frameCache.Expire(transportNow);
        else if (_server != null && _started)
        {
            var request = _assembler.GetMissingRequest(transportNow);
            if (request != null)
            {
                var writer = new NetDataWriter();
                writer.Put(Wire.NackPacketType);
                writer.Put(Wire.EncodeNack(request));
                _server.Send(writer, DeliveryMethod.Unreliable);
            }
        }

        if (_host)
        {
            if (!_started && now >= _nextLobbySync)
            {
                _nextLobbySync = now + 0.2f;
                SyncHostLocalState();
            }

            for (int i = 1; i < 4; i++)
                if (now - _lastInput[i] > 0.5f)
                    _inputs[i].Clear();
        }
        else if (_server != null && _assigned != 0)
        {
            byte value = _visible || !_started ? (byte)0 : ReadKeys();
            if (value != _lastSent || now >= _nextInput)
            {
                var writer = new NetDataWriter();
                writer.Put((byte)2);
                writer.Put(value);
                // Ordered transitions preserve short taps. Heartbeats release stale held keys on host.
                _server.Send(writer, DeliveryMethod.ReliableOrdered);
                _lastSent = value;
                _nextInput = now + 1f / 30;
            }

            if (_started && now - _lastFrame > 5) _status = "No complete snapshot for 5 seconds";
        }
    }

    private void InvokeClientJoin()
    {
        try
        {
            var scene = MenuScene.instance;
            var menu = scene?.GetType().GetField("characterSelectMenu",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(scene);
            var boxes = menu?.GetType()
                .GetField("boxes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(menu) as Array;
            if (boxes == null || _assigned > boxes.Length) return;
            var box = boxes.GetValue(_assigned - 1);
            var join = box?.GetType()
                .GetMethod("Join", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (join != null)
                join.Invoke(box, new[] { Activator.CreateInstance(join.GetParameters()[0].ParameterType) });
        }
        catch (Exception ex)
        {
            Logger.LogDebug("Client native join unavailable: " + ex.Message);
        }
    }

    private void LateUpdate()
    {
        if (_network == null || !_started || Game.instance == null || Game.instance.level == null) return;
        try
        {
            if (_host)
            {
                if (Time.realtimeSinceStartup < _nextSnapshot || _peers.Count == 0) return;
                _nextSnapshot = Time.realtimeSinceStartup + 1f / _snapshotHz;
                byte[] bytes = Wire.Pack(_replica.Capture());
                int count = (bytes.Length + Wire.ChunkSize - 1) / Wire.ChunkSize;
                _sequence++;
                _frameCache.Store(_sequence, bytes, Wire.NowMilliseconds);
                for (int i = 0; i < count; i++)
                {
                    var writer = new NetDataWriter();
                    writer.Put((byte)4);
                    writer.Put(_sequence);
                    writer.Put((ushort)i);
                    writer.Put((ushort)count);
                    writer.Put(bytes, i * Wire.ChunkSize, Math.Min(Wire.ChunkSize, bytes.Length - i * Wire.ChunkSize));
                    foreach (var peer in _peers.Keys) peer.Send(writer, DeliveryMethod.Unreliable);
                }

                _status = $"Host / P1 | {_players}/4 | {bytes.Length * _snapshotHz / 1024:0} KiB/s per peer";
            }
            else
            {
                Game.instance.ui?.Advance();
                if (!_clientSceneReady)
                {
                    _replica.InitializeClient();
                    _clientSceneReady = true;
                    _visible = false;
                }

                if (_pendingFrame != null)
                {
                    byte[] frame = _pendingFrame;
                    _pendingFrame = null;
                    try
                    {
                        _replica.Apply(frame);
                    }
                    catch (InvalidDataException ex)
                    {
                        Logger.LogWarning("Dropped invalid snapshot: " + ex.Message);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("Dropped snapshot: " + ex.Message);
                    }
                }

                _replica.Render();
                if (_server != null && Time.realtimeSinceStartup - _lastFrame < 5)
                    _status = $"P{_assigned} | Ping {_server.Ping} ms | missing sprites {_replica.MissingSprites}";
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
            Close(true);
            _status = "Session stopped: " + ex.Message;
        }
    }

    internal void ApplyInput(int number)
    {
        int index = number - 1;
        // P1 uses native input; this path only suppresses it while the panel is open or unfocused.
        byte value = _started && index != 0 ? _inputs[index].Advance() : (byte)0;
        byte pressed = (byte)(value & ~_applied[index]);
        _applied[index] = value;
        var c = GameInput.Player(number);
        c.left = (value & 1) != 0;
        c.right = (value & 2) != 0;
        c.up = (value & 4) != 0;
        c.down = (value & 8) != 0;
        c.jump = (value & 16) != 0;
        c.fire = (value & 32) != 0;
        c.justPressedLeft = (pressed & 1) != 0;
        c.justPressedRight = (pressed & 2) != 0;
        c.justPressedUp = (pressed & 4) != 0;
        c.justPressedDown = (pressed & 8) != 0;
        c.justPressedJump = (pressed & 16) != 0;
        c.justPressedFire = (pressed & 32) != 0;
        c.pause = c.menuForward = c.menuBack = c.menuNorth = false;
        c.justPressedPause = c.justPressedMenuForward = c.justPressedMenuBack = c.justPressedMenuNorth = false;
    }

    private void OnGUI()
    {
        if (Client && _clientSceneReady)
            GUI.Label(new Rect(12, Screen.height - 55, Screen.width - 24, 50), _replica.Hud);
        GUI.Label(new Rect(12, 8, Screen.width - 24, 25), "TwinShotNet EXPERIMENTAL | F8 | " + _status);
        if (!_visible) return;
        Cursor.visible = true;
        _window.width = Mathf.Min(430, Screen.width - 20);
        _window.x = Mathf.Clamp(_window.x, 0, Mathf.Max(0, Screen.width - _window.width));
        _window.y = Mathf.Clamp(_window.y, 0, Mathf.Max(0, Screen.height - _window.height));
        _window = GUILayout.Window(98241, _window, DrawWindow, "Twin Shot Net - Experimental");
    }

    private void DrawWindow(int id)
    {
        GUILayout.Label(_status);
        if (_network == null)
        {
            GUILayout.Label("Host IP / DNS");
            _address = GUILayout.TextField(_address, 253);
            GUILayout.Label("UDP port");
            _port = GUILayout.TextField(_port, 5);
            GUILayout.Label("Room key (12+ characters)");
            _password = GUILayout.PasswordField(_password, '*', 128);
            if (GUILayout.Button("Host")) Open(true);
            if (GUILayout.Button("Join")) Open(false);
        }
        else
        {
            if (!_started)
            {
                GUILayout.Label("Use the original character screen to join, change skin and ready.");
                GUILayout.Label(_status);
            }

            if (_host && _started && Game.instance != null && GUILayout.Button("Restart level"))
                Game.instance.LoadAndStartLevel(Game.levelId, Game.instance.nextLevelAfterBonusRound, false);
            if (GUILayout.Button("Stop / Disconnect")) Close(true);
        }

        if (GUILayout.Button("Close panel")) _visible = false;
        GUI.DragWindow(new Rect(0, 0, 10000, 24));
    }

    private void Close(bool returnToMenu)
    {
        bool wasStarted = _started;
        _network?.Stop();
        _network = null;
        _server = null;
        _peers.Clear();
        _started = false;
        _themePhase = false;
        _host = false;
        _assigned = 0;
        _clientSceneReady = false;
        _pendingFrame = null;
        _assembler = new FrameAssembler();
        _frameCache = new FrameCache();
        _sequence = 0;
        _nextInput = _nextSnapshot = _lastFrame = 0;
        _lastSent = 0;
        foreach (var input in _inputs) input.Clear();
        Array.Clear(_applied, 0, _applied.Length);
        _replica.Clear();
        if (_backgroundOwned)
        {
            Application.runInBackground = _oldBackground;
            _backgroundOwned = false;
        }

        _lastBroadcastCoins = -1;
        _status = "Offline";
        _visible = true;
        if (wasStarted && returnToMenu) SceneManager.LoadScene("Menus");
    }

    private void OnApplicationQuit()
    {
        _quitting = true;
        Close(false);
    }

    private void OnDisable()
    {
        Close(!_quitting);
    }

    private void OnDestroy()
    {
        try
        {
            Close(!_quitting);
        }
        finally
        {
            _startupHarmony?.UnpatchSelf();
            _harmony?.UnpatchSelf();
            Instance = null;
        }
    }

    private void BroadcastLobby()
    {
        var writer = new NetDataWriter();
        writer.Put((byte)6);
        for (int i = 0; i < 4; i++)
        {
            writer.Put(_occupied[i]);
            writer.Put((byte)_skins[i]);
            writer.Put(_ready[i]);
        }

        foreach (var peer in _peers.Keys) peer.Send(writer, DeliveryMethod.ReliableOrdered);
        if (!_host) SyncNativeLobby();
    }

    private void SyncNativeLobby()
    {
        try
        {
            var flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            object menuScene = typeof(MenuScene).GetField("instance", flags)?.GetValue(null);
            object menu = menuScene?.GetType().GetField("characterSelectMenu", flags)?.GetValue(menuScene);
            if (menu == null) return;
            var boxes = menu.GetType().GetField("boxes", flags)?.GetValue(menu) as Array;
            if (boxes == null) return;
            for (int i = 0; i < Math.Min(4, boxes.Length); i++)
            {
                object box = boxes.GetValue(i);
                if (box == null) continue;
                var type = box.GetType();
                type.GetField("selectedSkin", flags)?.SetValue(box, _skins[i]);
                type.GetField("state", flags)?.SetValue(box, _occupied[i] ? (_ready[i] ? 2 : 1) : 0);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug("Native lobby sync unavailable: " + ex.Message);
        }
    }

    private void ChangeLobby(int skin, bool isReady)
    {
        if (_host)
        {
            _skins[0] = skin;
            _ready[0] = isReady;
            BroadcastLobby();
        }
        else if (_server != null && _assigned != 0)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)5);
            writer.Put((byte)skin);
            writer.Put(isReady);
            _server.Send(writer, DeliveryMethod.ReliableOrdered);
        }
    }

    internal bool AllowNativeContinue()
    {
        if (!Hosting || _started) return true;
        if (_themePhase) return false;
        if (_peers.Count == 0 || Enumerable.Range(0, 4).Any(i => _occupied[i] && !_ready[i]))
        {
            _status = "Waiting for every player to be ready";
            return false;
        }

        StartMatch();
        return false;
    }

    internal bool HandleNativeJoin(object box, bool back, int direction)
    {
        // The host/offline game uses native input; do not reflect when this plugin is inapplicable.
        if (_started || !isActiveAndEnabled || !Client || _assigned <= 0) return true;
        // Fail closed for an active client: neither run native input nor send a lobby change
        // unless the join box is known to belong to our valid assigned slot.
        if (box == null || _assigned > _skins.Length) return false;
        var numberField = box.GetType().GetField("number",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (numberField == null) return false;
        if (numberField.GetValue(box) is not int number) return false;
        if (number != _assigned) return false;
        int slot = _assigned - 1;
        if (back) ChangeLobby(_skins[slot], false);
        else if (direction != 0) ChangeLobby(Mathf.Clamp(_skins[slot] + direction, 0, 3), false);
        else ChangeLobby(_skins[slot], !_ready[slot]);
        return false;
    }
}

[HarmonyPatch(typeof(GameInput), "AdvancePlayer")]
internal static class InputPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    private static bool Prefix(int number)
    {
        if (Plugin.Instance == null || !Plugin.Instance.isActiveAndEnabled || !Plugin.Instance.Hosting ||
            Plugin.Instance.InLobby || Game.instance == null) return true;
        if (number == 1 && !Plugin.Instance.PanelVisible && Application.isFocused) return true;
        Plugin.Instance.ApplyInput(number);
        return false;
    }
}

[HarmonyPatch(typeof(Game), "Update")]
internal static class ClientGamePatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    private static bool Prefix() =>
        Plugin.Instance == null || !Plugin.Instance.isActiveAndEnabled || !Plugin.Instance.Client;
}

[HarmonyPatch(typeof(LevelCompletePanel), "Advance")]
internal static class CompletionCoinsPatch
{
    [HarmonyPostfix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    [SuppressMessage("ReSharper", "InconsistentNaming",
        Justification = "Harmony requires the __instance parameter name for instance binding.")]
    private static void Postfix(LevelCompletePanel __instance)
    {
        var plugin = Plugin.Instance;
        if (plugin == null || !plugin.isActiveAndEnabled || !plugin.Hosting) return;
        var field = __instance.GetType().GetField("lifetimeCoins",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field?.GetValue(__instance) is int coins) plugin.BroadcastCompletionCoins(coins);
    }
}

[HarmonyPatch(typeof(CharacterSelectMenu), "ContinueToNextScreen")]
internal static class CharacterSelectContinuePatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    [SuppressMessage("ReSharper", "InconsistentNaming",
        Justification = "Harmony requires the __instance parameter name for instance binding.")]
    private static bool Prefix(CharacterSelectMenu __instance)
    {
        Plugin plugin = Plugin.Instance;
        if (plugin == null || !plugin.isActiveAndEnabled || !plugin.InLobby) return true;
        if (plugin.Client) return false;
        if (!plugin.Hosting) return true;
        var method = __instance.GetType().GetMethod("IsReadyToContinue",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (method == null) return false;
        if (method.Invoke(__instance, null) is not bool ready) return false;
        if (!ready) return true;
        return plugin.AllowNativeContinue();
    }
}

[HarmonyPatch(typeof(CharacterSelectMenu), "GoBackToPreviousMenu")]
internal static class ClientCharacterSelectBackPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    private static bool Prefix() =>
        Plugin.Instance == null || !Plugin.Instance.isActiveAndEnabled || !Plugin.Instance.Client;
}

[HarmonyPatch(typeof(ThemeSelectMenu), "StartGame")]
internal static class ThemeSelectStartPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    private static bool Prefix(Theme themeChoice, LevelId levelToStartOn)
    {
        var plugin = Plugin.Instance;
        if (plugin == null || !plugin.isActiveAndEnabled || !plugin.Hosting) return true;
        plugin.StartSelectedMatch(themeChoice, levelToStartOn);
        return false;
    }
}

[HarmonyPatch(typeof(ThemeSelectMenu), "OnPressContinue")]
internal static class ClientThemeContinuePatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    private static bool Prefix() => Plugin.Instance == null || !Plugin.Instance.Client || !Plugin.Instance.ThemePhase;
}

[HarmonyPatch(typeof(ThemeSelectMenu), "OnPressLevelSelect")]
internal static class ClientThemeLevelSelectPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    private static bool Prefix() => Plugin.Instance == null || !Plugin.Instance.Client || !Plugin.Instance.ThemePhase;
}

[HarmonyPatch(typeof(ThemeSelectMenu), "OnPressThemeLeft")]
internal static class ClientThemeLeftPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    private static bool Prefix() => Plugin.Instance == null || !Plugin.Instance.Client || !Plugin.Instance.ThemePhase;
}

[HarmonyPatch(typeof(ThemeSelectMenu), "OnPressThemeRight")]
internal static class ClientThemeRightPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    private static bool Prefix() => Plugin.Instance == null || !Plugin.Instance.Client || !Plugin.Instance.ThemePhase;
}

[HarmonyPatch(typeof(ThemeSelectMenu), "OnPressLevelButton")]
internal static class ClientThemeLevelButtonPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    private static bool Prefix() => Plugin.Instance == null || !Plugin.Instance.Client || !Plugin.Instance.ThemePhase;
}

[HarmonyPatch(typeof(ThemeSelectMenu), "OnPressBack")]
internal static class ClientThemeBackPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    private static bool Prefix() => Plugin.Instance == null || !Plugin.Instance.Client || !Plugin.Instance.ThemePhase;
}

[HarmonyPatch(typeof(PlayerJoinBox), "OnPressLeft")]
internal static class CharacterSelectLeftPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    [SuppressMessage("ReSharper", "InconsistentNaming",
        Justification = "Harmony requires the __instance parameter name for instance binding.")]
    private static bool Prefix(PlayerJoinBox __instance) => Plugin.Instance == null ||
                                                            !Plugin.Instance.isActiveAndEnabled ||
                                                            Plugin.Instance.HandleNativeJoin(__instance, false, -1);
}

[HarmonyPatch(typeof(PlayerJoinBox), "OnPressRight")]
internal static class CharacterSelectRightPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    [SuppressMessage("ReSharper", "InconsistentNaming",
        Justification = "Harmony requires the __instance parameter name for instance binding.")]
    private static bool Prefix(PlayerJoinBox __instance) => Plugin.Instance == null ||
                                                            !Plugin.Instance.isActiveAndEnabled ||
                                                            Plugin.Instance.HandleNativeJoin(__instance, false, 1);
}

[HarmonyPatch(typeof(PlayerJoinBox), "Back")]
internal static class CharacterSelectBackPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    [SuppressMessage("ReSharper", "InconsistentNaming",
        Justification = "Harmony requires the __instance parameter name for instance binding.")]
    private static bool Prefix(PlayerJoinBox __instance) => Plugin.Instance == null ||
                                                            !Plugin.Instance.isActiveAndEnabled ||
                                                            Plugin.Instance.HandleNativeJoin(__instance, true, 0);
}

[HarmonyPatch(typeof(PlayerJoinBox), "Join")]
internal static class CharacterSelectJoinPatch
{
    [HarmonyPrefix]
    [SuppressMessage("ReSharper", "UnusedMember.Local",
        Justification = "Harmony discovers and invokes this patch method by reflection.")]
    [SuppressMessage("ReSharper", "InconsistentNaming",
        Justification = "Harmony requires the __instance parameter name for instance binding.")]
    private static bool Prefix(PlayerJoinBox __instance)
    {
        Plugin plugin = Plugin.Instance;
        if (plugin == null || !plugin.isActiveAndEnabled) return true;
        return plugin.HandleNativeJoin(__instance, false, 0);
    }
}