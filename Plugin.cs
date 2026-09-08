using System;
using System.Collections.Generic;
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

[BepInPlugin("local.twinshot.net", "Twin Shot Net (Experimental)", "0.4.0")]
public sealed class Plugin : BaseUnityPlugin
{
    internal static Plugin Instance;
    internal bool Client => network != null && !host;
    internal bool Hosting => network != null && host;
    private NetManager network;
    private NetPeer server;
    private readonly Dictionary<NetPeer, int> peers = new Dictionary<NetPeer, int>();
    private readonly InputSlot[] inputs = { new InputSlot(), new InputSlot(), new InputSlot(), new InputSlot() };
    private readonly float[] lastInput = new float[4];
    private readonly byte[] applied = new byte[4];
    private FrameAssembler assembler = new FrameAssembler();
    private FrameCache frameCache = new FrameCache();
    private readonly Replica replica = new Replica();
    private Harmony harmony;
    private Harmony startupHarmony;
    private bool host, started, themePhase, visible = true, clientSceneReady;
    private bool oldBackground, backgroundOwned, quitting;
    internal bool PanelVisible => visible;
    private string address = "127.0.0.1", port = "27020", password = "", status = "Offline", fingerprint;
    private int assigned, sequence, players = 2, hostLocalPlayers = 1;
    private float nextInput, nextSnapshot, lastFrame, connectedAt;
    private int lastBroadcastCoins = -1;
    private float nextLobbySync;
    private byte lastSent;
    private byte[] pendingFrame;
    private Rect window = new Rect(20, 40, 430, 360);
    private float snapshotHz;
    private readonly int[] skins = { 0, 1, 2, 3 };
    private readonly bool[] occupied = new bool[4];
    private readonly bool[] ready = new bool[4];
    internal bool InLobby => network != null && !started;
    internal bool ThemePhase => themePhase;

    private void Awake()
    {
        Instance = this;
        address = Config.Bind("Network", "Address", "127.0.0.1", "Host public IP or DNS name").Value;
        port = Config.Bind("Network", "Port", 27020, "UDP listen/destination port").Value.ToString();
        password = Config.Bind("Network", "RoomKey", "", "Use a long unique room key; UDP transport is not encrypted").Value;
        snapshotHz = Mathf.Clamp(Config.Bind("Network", "SnapshotHz", 30, "Full visual snapshots per second (10-60)").Value, 10, 60);
        using var hash = SHA256.Create();
        using var file = File.OpenRead(typeof(Game).Assembly.Location);
        fingerprint = BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "");
        harmony = new Harmony("local.twinshot.net");
        harmony.PatchAll(typeof(Plugin).Assembly);
        startupHarmony = new Harmony("local.twinshot.net.startup");
        if (Config.Bind("Startup", "DisableSteamInitialization", false,
            "Experimental: disable managed Steam initialization and restart requests. Steam features become unavailable. Restart required; does not affect native launch checks.").Value)
        {
            try
            {
                startupHarmony.Patch(AccessTools.Method(typeof(SteamManager), "Awake"),
                    transpiler: new HarmonyMethod(typeof(SteamRestartPatch), nameof(SteamRestartPatch.TranspileOffline)));
                startupHarmony.Patch(AccessTools.Method(typeof(LoadingScreen), "Start"),
                    transpiler: new HarmonyMethod(typeof(SteamRestartPatch), nameof(SteamRestartPatch.TranspileOffline)));
                startupHarmony.Patch(AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(LoadingScreen), "Sequence")),
                    transpiler: new HarmonyMethod(typeof(SteamRestartPatch), nameof(SteamRestartPatch.TranspileDeckQuery)));
                Logger.LogWarning("DisableSteamInitialization enabled: managed restart and Init calls disabled. Steam features unavailable.");
            }
            catch (Exception ex) { startupHarmony.UnpatchSelf(); Logger.LogError("Optional Steam initialization patches rolled back: " + ex); }
        }
        else
        if (Config.Bind("Startup", "SkipSteamRestart", false,
            "Experimental: skip the game's Steam automatic restart request. Restart required. Steam initialization and other checks remain unchanged.").Value)
        {
            try
            {
                startupHarmony.Patch(AccessTools.Method(typeof(SteamManager), "Awake"),
                    transpiler: new HarmonyMethod(typeof(SteamRestartPatch), nameof(SteamRestartPatch.Transpile)));
                startupHarmony.Patch(AccessTools.Method(typeof(LoadingScreen), "Start"),
                    transpiler: new HarmonyMethod(typeof(SteamRestartPatch), nameof(SteamRestartPatch.Transpile)));
                Logger.LogWarning("SkipSteamRestart enabled. Steam initialization is unchanged; standalone startup is not guaranteed.");
            }
            catch (Exception ex) { startupHarmony.UnpatchSelf(); Logger.LogError("Optional Steam restart patches rolled back: " + ex); }
        }
        Logger.LogInfo("TwinShotNet 0.4.0 loaded. F8 opens the network panel.");
    }

    private void Open(bool asHost)
    {
        if (!int.TryParse(port, out int number) || number < 1024 || number > 65535) { status = "Port must be 1024-65535"; return; }
        if (password.Length < 12 || password.Length > 128) { status = "Room key must be 12-128 characters"; return; }
        if (Game.instance != null) { status = "Return to the main menu before connecting"; return; }
        if (MenuScene.instance == null) { status = "Wait for the main menu"; return; }
        host = asHost;
        hostLocalPlayers = asHost ? HostLocalPlayerCount() : 1;
        Array.Clear(ready, 0, 4); Array.Clear(occupied, 0, 4);
        occupied[0] = asHost;
        visible = true;
        oldBackground = Application.runInBackground;
        backgroundOwned = true;
        Application.runInBackground = true;
        var listener = new EventBasedNetListener();
        network = new NetManager(listener) { AutoRecycle = true, IPv6Enabled = false, DisconnectTimeout = 5000 };
        listener.ConnectionRequestEvent += request =>
        {
            if (!host || started || peers.Count >= 3) { request.Reject(); return; }
            request.AcceptIfKey(Wire.Version + ":" + fingerprint + ":" + password);
        };
        listener.PeerConnectedEvent += peer =>
        {
            if (host)
            {
                hostLocalPlayers = HostLocalPlayerCount();
                int slot = Enumerable.Range(hostLocalPlayers, 4 - hostLocalPlayers).FirstOrDefault(n => !peers.Values.Contains(n));
                if (slot == 0 || started) { peer.Disconnect(); return; }
                peers.Add(peer, slot);
                occupied[slot] = true;
                ready[slot] = false;
                inputs[slot].Clear(); lastInput[slot] = Time.realtimeSinceStartup;
                var writer = new NetDataWriter(); writer.Put((byte)1); writer.Put((byte)(slot + 1)); peer.Send(writer, DeliveryMethod.ReliableOrdered);
                status = $"Hosting: {hostLocalPlayers + peers.Count}/4 players";
                BroadcastLobby();
            }
            else { server = peer; connectedAt = Time.realtimeSinceStartup; status = "Connected; waiting for host"; }
        };
        listener.PeerDisconnectedEvent += (peer, info) =>
        {
            if (host)
            {
                if (peers.TryGetValue(peer, out int slot))
                {
                    inputs[slot].Clear(); peers.Remove(peer); occupied[slot] = false;
                    ready[slot] = false;
                    if (!started) BroadcastLobby();
                }
                status = "Player disconnected; slot input released";
            }
            else { status = "Disconnected: " + info.Reason + ". Stop to return to menu."; server = null; assigned = 0; }
        };
        listener.NetworkReceiveEvent += Receive;
        listener.NetworkErrorEvent += (endpoint, error) => { status = "Network error: " + error; Logger.LogWarning(status); };
        try
        {
            if (!network.Start(host ? number : 0)) throw new IOException("Unable to bind UDP socket");
            if (host) status = $"Listening UDP {number}. Waiting for friends.";
            else { network.Connect(address.Trim(), number, Wire.Version + ":" + fingerprint + ":" + password); status = "Connecting..."; }
        }
        catch (Exception ex) { Close(false); status = ex.Message; Logger.LogError(ex); }
    }

    private void Receive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        try
        {
            byte type = reader.GetByte();
            if (host)
            {
                if (!peers.TryGetValue(peer, out int slot)) return;
                if (type == Wire.NackPacketType)
                {
                    if (!started || method != DeliveryMethod.Unreliable || reader.AvailableBytes < 8 ||
                        reader.AvailableBytes > 6 + 2 * Wire.MaxRepairChunks) return;
                    if (!Wire.TryDecodeNack(reader.GetRemainingBytes(), out var request)) return;
                    foreach (var chunk in frameCache.Serve(request, Wire.NowMilliseconds))
                    {
                        var writer = new NetDataWriter();
                        writer.Put((byte)4); writer.Put(chunk.Sequence);
                        writer.Put((ushort)chunk.Index); writer.Put((ushort)chunk.Total); writer.Put(chunk.Payload);
                        peer.Send(writer, DeliveryMethod.Unreliable);
                    }
                    return;
                }
                if (type == 5 && !started && reader.AvailableBytes == 2)
                {
                    int skin = reader.GetByte(); bool isReady = reader.GetBool();
                    if (skin > 3) return;
                    if (skins[slot] != skin) { skins[slot] = skin; ready[slot] = false; }
                    else ready[slot] = isReady;
                    BroadcastLobby(); return;
                }
                if (type != 2 || reader.AvailableBytes != 1) return;
                byte value = reader.GetByte();
                inputs[slot].Receive(value);
                lastInput[slot] = Time.realtimeSinceStartup;
                return;
            }
            if (peer != server) return;
            if (type == 6 && !started)
            {
                for (int i = 0; i < 4; i++)
                {
                    occupied[i] = reader.GetBool(); skins[i] = reader.GetByte(); ready[i] = reader.GetBool();
                    if (skins[i] > 3) throw new InvalidDataException("Invalid lobby skin");
                }
                visible = true; SyncNativeLobby(); return;
            }
            if (type == 1)
            {
                assigned = reader.GetByte();
                if (assigned < 2 || assigned > 4) throw new InvalidDataException("Invalid player slot");
                status = $"Player {assigned}; waiting for host";
            }
            else if (type == 3) throw new InvalidDataException("Obsolete match packet");
            else if (type == 8 && !started)
            {
                if (reader.AvailableBytes < 7) throw new InvalidDataException("Truncated match packet");
                Theme theme = (Theme)reader.GetByte();
                int level = reader.GetInt();
                if (!Enum.IsDefined(typeof(Theme), theme)) throw new InvalidDataException("Invalid match theme");
                if (level < 1 || level > 100) throw new InvalidDataException("Invalid match level");
                var selectedLevel = new LevelId(theme, level);
                if (!selectedLevel.DoesExist()) throw new InvalidDataException("Host selected an unavailable level");
                players = reader.GetByte(); assigned = reader.GetByte();
                if (players < 2 || players > 4 || assigned < 2 || assigned > players) throw new InvalidDataException("Invalid match players");
                if (reader.AvailableBytes != players) throw new InvalidDataException("Invalid match packet size");
                for (int i = 0; i < players; i++) skins[i] = reader.GetByte();
                started = true; themePhase = false; ConfigureGame(players, theme, level); SceneManager.LoadScene("Game"); lastFrame = Time.realtimeSinceStartup;
            }
            else if (type == 9 && !started)
            {
                if (reader.AvailableBytes != 0) throw new InvalidDataException("Invalid theme phase packet");
                themePhase = true;
                lastSent = 0;
                ShowNativeThemeSelect();
                visible = true;
                status = "Waiting for host to choose a theme and level";
            }
            else if (type == 10 && started)
            {
                if (reader.AvailableBytes != 4) throw new InvalidDataException("Invalid completion packet");
                int coins = reader.GetInt();
                if (coins < 0) throw new InvalidDataException("Invalid completion coins");
                ApplyCompletionCoins(coins);
            }
            else if (type == 4 && started)
            {
                int id = reader.GetInt(); int part = reader.GetUShort(); int total = reader.GetUShort();
                var data = reader.GetRemainingBytes();
                byte[] frame = assembler.Add(id, part, total, data);
                if (frame != null) { pendingFrame = Wire.Unpack(frame); lastFrame = Time.realtimeSinceStartup; }
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
        Game.playerSkins = skins.Take(count).Select(s => (Player.Skin)s).ToArray();
        Game.themeChoice = theme;
        Game.levelId = new LevelId(theme, level);
    }

    private void StartMatch()
    {
        SyncHostLocalState();
        if (peers.Count == 0) { status = "At least one friend must connect first"; return; }
        if (Enumerable.Range(0, 4).Any(i => occupied[i] && !ready[i])) { status = "Waiting for everyone to ready"; return; }
        int localPlayers = hostLocalPlayers;
        players = localPlayers + peers.Count;
        if (players > 4) { status = "Maximum of four total players reached"; return; }
        foreach (var input in inputs) input.Clear();
        ShowNativeThemeSelect();
        themePhase = true;
        foreach (var peer in peers.Keys) { var writer = new NetDataWriter(); writer.Put((byte)9); peer.Send(writer, DeliveryMethod.ReliableOrdered); }
        status = "Choose a theme and level";
    }

    private void SyncHostLocalState()
    {
        if (!host) return;
        try
        {
            var scene = MenuScene.instance;
            var menu = scene?.GetType().GetField("characterSelectMenu", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(scene);
            var boxes = menu?.GetType().GetField("boxes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(menu) as Array;
            if (boxes != null)
                foreach (var box in boxes)
                {
                    var type = box.GetType();
                    int number = (int)type.GetField("number", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(box);
                    if (number < 1 || number > hostLocalPlayers) continue;
                    int slot = number - 1;
                    var state = type.GetField("state", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(box);
                    var skin = type.GetField("selectedSkin", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(box);
                    occupied[slot] = Convert.ToInt32(state) != 0;
                    skins[slot] = Convert.ToInt32(skin);
                    ready[slot] = Convert.ToInt32(state) == 2;
                }
        }
        catch (Exception ex) { Logger.LogDebug("Native host lobby state unavailable: " + ex.Message); }
        for (int i = 0; i < 4; i++) if (!peers.Values.Contains(i)) { if (!occupied[i]) ready[i] = false; }
        BroadcastLobby();
    }

    internal void StartSelectedMatch(Theme theme, LevelId level)
    {
        started = true;
        themePhase = false;
        foreach (var input in inputs) input.Clear();
        foreach (var entry in peers)
        {
            var writer = new NetDataWriter(); writer.Put((byte)8); writer.Put((byte)theme); writer.Put(level.LevelNumberWithinTheme); writer.Put((byte)players); writer.Put((byte)(entry.Value + 1));
            for (int i = 0; i < players; i++) writer.Put((byte)skins[i]);
            entry.Key.Send(writer, DeliveryMethod.ReliableOrdered);
        }
        ConfigureGame(players, theme, level.LevelNumberWithinTheme);
        SceneManager.LoadScene("Game");
        visible = false;
        status = "Host / P1";
    }

    private void ApplyCompletionCoins(int coins)
    {
        try
        {
            var panel = Game.instance?.ui?.levelCompletePanel;
            var type = panel?.GetType();
            type?.GetField("lifetimeCoins", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(panel, coins);
            var text = type?.GetField("textLifetimeCoins", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(panel);
            text?.GetType().GetProperty("text")?.SetValue(text, coins.ToString());
        }
        catch (Exception ex) { Logger.LogWarning("Completion coin UI update unavailable: " + ex.Message); }
    }

    internal void BroadcastCompletionCoins(int coins)
    {
        if (!Hosting || coins < 0) return;
        if (coins == lastBroadcastCoins) return;
        lastBroadcastCoins = coins;
        var writer = new NetDataWriter(); writer.Put((byte)10); writer.Put(coins);
        foreach (var peer in peers.Keys) peer.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    private int HostLocalPlayerCount()
    {
        try
        {
            var scene = MenuScene.instance;
            var menu = scene?.GetType().GetField("characterSelectMenu", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(scene);
            var boxes = menu?.GetType().GetField("boxes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(menu) as Array;
            if (boxes == null) return 1;
            int count = 0;
            foreach (var box in boxes)
            {
                var state = box?.GetType().GetField("state", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(box);
                if (state != null && Convert.ToInt32(state) != 0) count++;
            }
            return Mathf.Clamp(count, 1, 4);
        }
        catch { return 1; }
    }

    private void ShowNativeThemeSelect()
    {
        var menuScene = MenuScene.instance;
        var menu = menuScene?.GetType().GetField("themeSelectMenu", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(menuScene);
        menu?.GetType().GetMethod("Show", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(menu, null);
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
        if (Input.GetKeyDown(KeyCode.F8)) visible = !visible;
        if (Client && !started && !themePhase && assigned > 0 && Input.GetKeyDown(KeyCode.Space))
            InvokeClientJoin();
        network?.PollEvents();
        if (network == null) return;
        float now = Time.realtimeSinceStartup;
        double transportNow = Wire.NowMilliseconds;
        if (host) frameCache.Expire(transportNow);
        else if (server != null && started)
        {
            var request = assembler.GetMissingRequest(transportNow);
            if (request != null)
            {
                var writer = new NetDataWriter(); writer.Put(Wire.NackPacketType); writer.Put(Wire.EncodeNack(request));
                server.Send(writer, DeliveryMethod.Unreliable);
            }
        }
        if (host)
        {
            if (!started && now >= nextLobbySync) { nextLobbySync = now + 0.2f; SyncHostLocalState(); }
            for (int i = 1; i < 4; i++) if (now - lastInput[i] > 0.5f) inputs[i].Clear();
        }
        else if (server != null && assigned != 0)
        {
            byte value = visible || !started ? (byte)0 : ReadKeys();
            if (value != lastSent || now >= nextInput)
            {
                var writer = new NetDataWriter(); writer.Put((byte)2); writer.Put(value);
                // Ordered transitions preserve short taps. Heartbeats release stale held keys on host.
                server.Send(writer, DeliveryMethod.ReliableOrdered);
                lastSent = value; nextInput = now + 1f / 30;
            }
            if (started && now - lastFrame > 5) status = "No complete snapshot for 5 seconds";
        }
    }

    private void InvokeClientJoin()
    {
        try
        {
            var scene = MenuScene.instance;
            var menu = scene?.GetType().GetField("characterSelectMenu", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(scene);
            var boxes = menu?.GetType().GetField("boxes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(menu) as Array;
            if (boxes == null || assigned > boxes.Length) return;
            var box = boxes.GetValue(assigned - 1);
            var join = box?.GetType().GetMethod("Join", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (join != null) join.Invoke(box, new[] { Activator.CreateInstance(join.GetParameters()[0].ParameterType) });
        }
        catch (Exception ex) { Logger.LogDebug("Client native join unavailable: " + ex.Message); }
    }

    private void LateUpdate()
    {
        if (network == null || !started || Game.instance == null || Game.instance.level == null) return;
        try
        {
            if (host)
            {
                if (Time.realtimeSinceStartup < nextSnapshot || peers.Count == 0) return;
                nextSnapshot = Time.realtimeSinceStartup + 1f / snapshotHz;
                byte[] bytes = Wire.Pack(replica.Capture());
                int count = (bytes.Length + Wire.ChunkSize - 1) / Wire.ChunkSize;
                sequence++;
                frameCache.Store(sequence, bytes, Wire.NowMilliseconds);
                for (int i = 0; i < count; i++)
                {
                    var writer = new NetDataWriter(); writer.Put((byte)4); writer.Put(sequence); writer.Put((ushort)i); writer.Put((ushort)count);
                    writer.Put(bytes, i * Wire.ChunkSize, Math.Min(Wire.ChunkSize, bytes.Length - i * Wire.ChunkSize));
                    foreach (var peer in peers.Keys) peer.Send(writer, DeliveryMethod.Unreliable);
                }
                status = $"Host / P1 | {players}/4 | {bytes.Length * snapshotHz / 1024:0} KiB/s per peer";
            }
            else
            {
                Game.instance.ui?.Advance();
                if (!clientSceneReady)
                {
                    replica.InitializeClient(); clientSceneReady = true; visible = false;
                }
                if (pendingFrame != null)
                {
                    byte[] frame = pendingFrame; pendingFrame = null;
                    try { replica.Apply(frame); }
                    catch (InvalidDataException ex) { Logger.LogWarning("Dropped invalid snapshot: " + ex.Message); }
                    catch (Exception ex) { Logger.LogWarning("Dropped snapshot: " + ex.Message); }
                }
                replica.Render();
                if (server != null && Time.realtimeSinceStartup - lastFrame < 5)
                status = $"P{assigned} | Ping {server.Ping} ms | missing sprites {replica.MissingSprites}";
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex); Close(true); status = "Session stopped: " + ex.Message;
        }
    }

    internal void ApplyInput(int number)
    {
        int index = number - 1;
        // P1 uses native input; this path only suppresses it while the panel is open or unfocused.
        byte value = started && index != 0 ? inputs[index].Advance() : (byte)0;
        byte pressed = (byte)(value & ~applied[index]); applied[index] = value;
        var c = GameInput.Player(number);
        c.left = (value & 1) != 0; c.right = (value & 2) != 0; c.up = (value & 4) != 0; c.down = (value & 8) != 0;
        c.jump = (value & 16) != 0; c.fire = (value & 32) != 0;
        c.justPressedLeft = (pressed & 1) != 0; c.justPressedRight = (pressed & 2) != 0;
        c.justPressedUp = (pressed & 4) != 0; c.justPressedDown = (pressed & 8) != 0;
        c.justPressedJump = (pressed & 16) != 0; c.justPressedFire = (pressed & 32) != 0;
        c.pause = c.menuForward = c.menuBack = c.menuNorth = false;
        c.justPressedPause = c.justPressedMenuForward = c.justPressedMenuBack = c.justPressedMenuNorth = false;
    }

    private void OnGUI()
    {
        if (Client && clientSceneReady) GUI.Label(new Rect(12, Screen.height - 55, Screen.width - 24, 50), replica.Hud);
        GUI.Label(new Rect(12, 8, Screen.width - 24, 25), "TwinShotNet EXPERIMENTAL | F8 | " + status);
        if (!visible) return;
        Cursor.visible = true;
        window.width = Mathf.Min(430, Screen.width - 20);
        window.x = Mathf.Clamp(window.x, 0, Mathf.Max(0, Screen.width - window.width));
        window.y = Mathf.Clamp(window.y, 0, Mathf.Max(0, Screen.height - window.height));
        window = GUILayout.Window(98241, window, DrawWindow, "Twin Shot Net - Experimental");
    }

    private void DrawWindow(int id)
    {
        GUILayout.Label(status);
        if (network == null)
        {
            GUILayout.Label("Host IP / DNS"); address = GUILayout.TextField(address, 253);
            GUILayout.Label("UDP port"); port = GUILayout.TextField(port, 5);
            GUILayout.Label("Room key (12+ characters)"); password = GUILayout.PasswordField(password, '*', 128);
            if (GUILayout.Button("Host")) Open(true);
            if (GUILayout.Button("Join")) Open(false);
        }
        else
        {
            if (!started)
            {
                GUILayout.Label("Use the original character screen to join, change skin and ready.");
                GUILayout.Label(status);
            }
            if (host && started && Game.instance != null && GUILayout.Button("Restart level"))
                Game.instance.LoadAndStartLevel(Game.levelId, Game.instance.nextLevelAfterBonusRound, false);
            if (GUILayout.Button("Stop / Disconnect")) Close(true);
        }
        if (GUILayout.Button("Close panel")) visible = false;
        GUI.DragWindow(new Rect(0, 0, 10000, 24));
    }

    private void Close(bool returnToMenu)
    {
        bool wasStarted = started;
        network?.Stop(); network = null; server = null; peers.Clear();
        started = false; themePhase = false; host = false; assigned = 0; clientSceneReady = false; pendingFrame = null;
        assembler = new FrameAssembler(); frameCache = new FrameCache(); sequence = 0;
        nextInput = nextSnapshot = lastFrame = connectedAt = 0; lastSent = 0;
        foreach (var input in inputs) input.Clear();
        Array.Clear(applied, 0, applied.Length);
        replica.Clear();
        if (backgroundOwned) { Application.runInBackground = oldBackground; backgroundOwned = false; }
        lastBroadcastCoins = -1;
        status = "Offline"; visible = true;
        if (wasStarted && returnToMenu) SceneManager.LoadScene("Menus");
    }

    private void OnApplicationQuit() { quitting = true; Close(false); }
    private void OnDisable() { Close(!quitting); }
    private void OnDestroy()
    {
        try { Close(!quitting); }
        finally { startupHarmony?.UnpatchSelf(); harmony?.UnpatchSelf(); Instance = null; }
    }

    private void BroadcastLobby()
    {
        var writer = new NetDataWriter(); writer.Put((byte)6);
        for (int i = 0; i < 4; i++) { writer.Put(occupied[i]); writer.Put((byte)skins[i]); writer.Put(ready[i]); }
        foreach (var peer in peers.Keys) peer.Send(writer, DeliveryMethod.ReliableOrdered);
        if (!host) SyncNativeLobby();
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
                object box = boxes.GetValue(i); if (box == null) continue;
                var type = box.GetType();
                type.GetField("selectedSkin", flags)?.SetValue(box, skins[i]);
                type.GetField("state", flags)?.SetValue(box, occupied[i] ? (ready[i] ? 2 : 1) : 0);
            }
        }
        catch (Exception ex) { Logger.LogDebug("Native lobby sync unavailable: " + ex.Message); }
    }

    private void ShowNativeCharacterSelect()
    {
        try
        {
            object menuScene = typeof(MenuScene).GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
            object menu = menuScene?.GetType().GetField("characterSelectMenu", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(menuScene);
            menu?.GetType().GetMethod("Appear", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(menu, null);
            SyncNativeLobby();
        }
        catch (Exception ex) { Logger.LogDebug("Native character select unavailable: " + ex.Message); }
    }

    private void ChangeLobby(int skin, bool isReady)
    {
        if (host) { skins[0] = skin; ready[0] = isReady; BroadcastLobby(); }
        else if (server != null && assigned != 0)
        {
            var writer = new NetDataWriter(); writer.Put((byte)5); writer.Put((byte)skin); writer.Put(isReady);
            server.Send(writer, DeliveryMethod.ReliableOrdered);
        }
    }

    internal bool AllowNativeContinue()
    {
        if (!Hosting || started) return true;
        if (themePhase) return false;
        if (peers.Count == 0 || Enumerable.Range(0, 4).Any(i => occupied[i] && !ready[i]))
        {
            status = "Waiting for every player to be ready";
            return false;
        }
        StartMatch();
        return false;
    }

    internal bool HandleNativeJoin(object box, bool back, int direction)
    {
        if (started || box == null) return true;
        // The host uses the game's native join-box flow; only clients need remote input forwarding.
        if (host) return true;
        int number = (int)box.GetType().GetField("number", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(box);
        if (!Client || assigned <= 0) return true;
        if (number != assigned) return false;
        int slot = assigned - 1;
        if (back) ChangeLobby(skins[slot], false);
        else if (direction != 0) ChangeLobby(Mathf.Clamp(skins[slot] + direction, 0, 3), false);
        else ChangeLobby(skins[slot], !ready[slot]);
        return false;
    }
}

[HarmonyPatch(typeof(GameInput), "AdvancePlayer")]
internal static class InputPatch
{
    private static bool Prefix(int number)
    {
        if (Plugin.Instance == null || !Plugin.Instance.isActiveAndEnabled || !Plugin.Instance.Hosting || Plugin.Instance.InLobby || Game.instance == null) return true;
        if (number == 1 && !Plugin.Instance.PanelVisible && Application.isFocused) return true;
        Plugin.Instance.ApplyInput(number);
        return false;
    }
}

[HarmonyPatch(typeof(Game), "Update")]
internal static class ClientGamePatch
{
    private static bool Prefix() => Plugin.Instance == null || !Plugin.Instance.isActiveAndEnabled || !Plugin.Instance.Client;
}

[HarmonyPatch(typeof(LevelCompletePanel), "Advance")]
internal static class CompletionCoinsPatch
{
    private static void Postfix(LevelCompletePanel __instance)
    {
        var plugin = Plugin.Instance;
        if (plugin == null || !plugin.isActiveAndEnabled || !plugin.Hosting) return;
        var field = __instance.GetType().GetField("lifetimeCoins", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field?.GetValue(__instance) is int coins) plugin.BroadcastCompletionCoins(coins);
    }
}

[HarmonyPatch(typeof(CharacterSelectMenu), "ContinueToNextScreen")]
internal static class CharacterSelectContinuePatch
{
    private static bool Prefix(CharacterSelectMenu __instance)
    {
        Plugin plugin = Plugin.Instance;
        if (plugin == null || !plugin.isActiveAndEnabled || !plugin.InLobby) return true;
        if (plugin.Client) return false;
        if (!plugin.Hosting) return true;
        var method = __instance.GetType().GetMethod("IsReadyToContinue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (method != null && !(bool)method.Invoke(__instance, null)) return true;
        return plugin.AllowNativeContinue();
    }
}

[HarmonyPatch(typeof(CharacterSelectMenu), "GoBackToPreviousMenu")]
internal static class ClientCharacterSelectBackPatch
{
    private static bool Prefix() => Plugin.Instance == null || !Plugin.Instance.isActiveAndEnabled || !Plugin.Instance.Client;
}

[HarmonyPatch(typeof(ThemeSelectMenu), "StartGame")]
internal static class ThemeSelectStartPatch
{
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
    private static bool Prefix() => Plugin.Instance == null || !Plugin.Instance.Client || !Plugin.Instance.ThemePhase;
}

[HarmonyPatch(typeof(ThemeSelectMenu), "OnPressLevelSelect")]
internal static class ClientThemeLevelSelectPatch
{
    private static bool Prefix() => Plugin.Instance == null || !Plugin.Instance.Client || !Plugin.Instance.ThemePhase;
}

[HarmonyPatch(typeof(ThemeSelectMenu), "OnPressThemeLeft")]
internal static class ClientThemeLeftPatch
{
    private static bool Prefix() => Plugin.Instance == null || !Plugin.Instance.Client || !Plugin.Instance.ThemePhase;
}

[HarmonyPatch(typeof(ThemeSelectMenu), "OnPressThemeRight")]
internal static class ClientThemeRightPatch
{
    private static bool Prefix() => Plugin.Instance == null || !Plugin.Instance.Client || !Plugin.Instance.ThemePhase;
}

[HarmonyPatch(typeof(ThemeSelectMenu), "OnPressLevelButton")]
internal static class ClientThemeLevelButtonPatch
{
    private static bool Prefix() => Plugin.Instance == null || !Plugin.Instance.Client || !Plugin.Instance.ThemePhase;
}

[HarmonyPatch(typeof(ThemeSelectMenu), "OnPressBack")]
internal static class ClientThemeBackPatch
{
    private static bool Prefix() => Plugin.Instance == null || !Plugin.Instance.Client || !Plugin.Instance.ThemePhase;
}

[HarmonyPatch(typeof(PlayerJoinBox), "OnPressLeft")]
internal static class CharacterSelectLeftPatch
{
    private static bool Prefix(PlayerJoinBox __instance) => Plugin.Instance == null || !Plugin.Instance.isActiveAndEnabled || Plugin.Instance.HandleNativeJoin(__instance, false, -1);
}

[HarmonyPatch(typeof(PlayerJoinBox), "OnPressRight")]
internal static class CharacterSelectRightPatch
{
    private static bool Prefix(PlayerJoinBox __instance) => Plugin.Instance == null || !Plugin.Instance.isActiveAndEnabled || Plugin.Instance.HandleNativeJoin(__instance, false, 1);
}

[HarmonyPatch(typeof(PlayerJoinBox), "Back")]
internal static class CharacterSelectBackPatch
{
    private static bool Prefix(PlayerJoinBox __instance) => Plugin.Instance == null || !Plugin.Instance.isActiveAndEnabled || Plugin.Instance.HandleNativeJoin(__instance, true, 0);
}

[HarmonyPatch(typeof(PlayerJoinBox), "Join")]
internal static class CharacterSelectJoinPatch
{
    private static bool Prefix(PlayerJoinBox __instance)
    {
        Plugin plugin = Plugin.Instance;
        if (plugin == null || !plugin.isActiveAndEnabled) return true;
        return plugin.HandleNativeJoin(__instance, false, 0);
    }
}
