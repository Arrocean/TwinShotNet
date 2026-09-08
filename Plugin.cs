using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BepInEx;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TwinShotNet;

[BepInPlugin("local.twinshot.net", "Twin Shot Net (Experimental)", "0.3.0")]
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
    private bool host, started, visible = true, clientSceneReady;
    private bool oldBackground, backgroundOwned, quitting;
    internal bool PanelVisible => visible;
    private string address = "127.0.0.1", port = "27020", password = "", status = "Offline", fingerprint;
    private int assigned, sequence, players = 2;
    private float nextInput, nextSnapshot, lastFrame, connectedAt;
    private byte lastSent;
    private byte[] pendingFrame;
    private Rect window = new Rect(20, 40, 430, 360);
    private float snapshotHz;
    private readonly int[] skins = { 0, 1, 2, 3 };
    private readonly bool[] occupied = new bool[4];
    private readonly bool[] ready = new bool[4];
    internal bool InLobby => network != null && !started;

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
        Logger.LogInfo("TwinShotNet 0.3.0 loaded. F8 opens the experimental network panel.");
    }

    private void Open(bool asHost)
    {
        if (!int.TryParse(port, out int number) || number < 1024 || number > 65535) { status = "Port must be 1024-65535"; return; }
        if (password.Length < 12 || password.Length > 128) { status = "Room key must be 12-128 characters"; return; }
        if (Game.instance != null) { status = "Return to the main menu before connecting"; return; }
        if (MenuScene.instance == null) { status = "Wait for the main menu"; return; }
        host = asHost;
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
                int slot = Enumerable.Range(1, 3).FirstOrDefault(n => !peers.Values.Contains(n));
                if (slot == 0 || started) { peer.Disconnect(); return; }
                peers.Add(peer, slot);
                occupied[slot] = true;
                Array.Clear(ready, 0, 4);
                inputs[slot].Clear(); lastInput[slot] = Time.realtimeSinceStartup;
                var writer = new NetDataWriter(); writer.Put((byte)1); writer.Put((byte)(slot + 1)); peer.Send(writer, DeliveryMethod.ReliableOrdered);
                status = $"Hosting: {peers.Count + 1}/4 players";
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
                    Array.Clear(ready, 0, 4);
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
                visible = true; return;
            }
            if (type == 1)
            {
                assigned = reader.GetByte();
                if (assigned < 2 || assigned > 4) throw new InvalidDataException("Invalid player slot");
                status = $"Player {assigned}; waiting for host";
            }
            else if (type == 3)
            {
                int count = reader.GetByte();
                if (count < 2 || count > 4 || started) return;
                players = count; started = true;
                assigned = reader.GetByte();
                if (assigned < 2 || assigned > count) throw new InvalidDataException("Invalid match slot");
                for (int i = 0; i < count; i++)
                {
                    skins[i] = reader.GetByte();
                    if (skins[i] > 3) throw new InvalidDataException("Invalid match skin");
                }
                ConfigureGame(count);
                SceneManager.LoadScene("Game");
                lastFrame = Time.realtimeSinceStartup;
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

    private void ConfigureGame(int count)
    {
        Game.isUsingTouchControls = false;
        Game.playerSkins = skins.Take(count).Select(s => (Player.Skin)s).ToArray();
        Game.themeChoice = Theme.Acropolis;
        Game.levelId = new LevelId(Theme.Acropolis, 1);
    }

    private void StartMatch()
    {
        if (peers.Count == 0) { status = "At least one friend must connect first"; return; }
        if (Enumerable.Range(0, 4).Any(i => occupied[i] && !ready[i])) { status = "Waiting for everyone to ready"; return; }
        // Compact disconnected lobby gaps before spawning the game's contiguous player array.
        var oldSkins = (int[])skins.Clone();
        var ordered = peers.OrderBy(p => p.Value).ToArray();
        players = ordered.Length + 1;
        for (int i = 0; i < ordered.Length; i++) { skins[i + 1] = oldSkins[ordered[i].Value]; peers[ordered[i].Key] = i + 1; }
        started = true;
        foreach (var input in inputs) input.Clear();
        foreach (var entry in peers)
        {
            var writer = new NetDataWriter(); writer.Put((byte)3); writer.Put((byte)players); writer.Put((byte)(entry.Value + 1));
            for (int i = 0; i < players; i++) writer.Put((byte)skins[i]);
            entry.Key.Send(writer, DeliveryMethod.ReliableOrdered);
        }
        ConfigureGame(players);
        SceneManager.LoadScene("Game");
        visible = false;
        status = "Host / P1";
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
                status = $"Host / P1 | {peers.Count + 1}/4 | {bytes.Length * snapshotHz / 1024:0} KiB/s per peer";
            }
            else
            {
                if (!clientSceneReady)
                {
                    replica.InitializeClient(); clientSceneReady = true; visible = false;
                }
                if (pendingFrame != null) { replica.Apply(pendingFrame); pendingFrame = null; }
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
                GUILayout.Label("Room / Choose color and ready");
                string[] names = { "Pink", "Orange", "Purple", "Blue" };
                int own = host ? 0 : assigned - 1;
                for (int i = 0; i < 4; i++)
                {
                    GUILayout.Label($"P{i + 1}: " + (occupied[i] ? names[skins[i]] + (ready[i] ? " [READY]" : " [WAITING]") : "Empty"));
                    if (!occupied[i] || own != i) continue;
                    int selection = GUILayout.SelectionGrid(skins[i], names, 4);
                    if (selection != skins[i]) ChangeLobby(selection, false);
                    if (GUILayout.Button(ready[i] ? "Cancel ready" : "Ready")) ChangeLobby(skins[i], !ready[i]);
                }
                if (host)
                {
                    GUI.enabled = peers.Count > 0 && Enumerable.Range(0, 4).All(i => !occupied[i] || ready[i]);
                    if (GUILayout.Button("Start (Acropolis 1)")) StartMatch();
                    GUI.enabled = true;
                }
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
        started = false; host = false; assigned = 0; clientSceneReady = false; pendingFrame = null;
        assembler = new FrameAssembler(); frameCache = new FrameCache(); sequence = 0;
        nextInput = nextSnapshot = lastFrame = connectedAt = 0; lastSent = 0;
        foreach (var input in inputs) input.Clear();
        Array.Clear(applied, 0, applied.Length);
        replica.Clear();
        if (backgroundOwned) { Application.runInBackground = oldBackground; backgroundOwned = false; }
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
}

[HarmonyPatch(typeof(MenuScene), "FixedUpdate")]
internal static class LobbyMenuPatch
{
    private static bool Prefix() => Plugin.Instance == null || !Plugin.Instance.isActiveAndEnabled || !Plugin.Instance.InLobby;
}

[HarmonyPatch(typeof(GameInput), "AdvancePlayer")]
internal static class InputPatch
{
    private static bool Prefix(int number)
    {
        if (Plugin.Instance == null || !Plugin.Instance.isActiveAndEnabled || !Plugin.Instance.Hosting || Game.instance == null) return true;
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
