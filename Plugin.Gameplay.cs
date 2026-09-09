using System;
using System.IO;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TwinShotNet;

public sealed partial class Plugin
{
    private readonly InputSlot[] _inputs = { new(), new(), new(), new() };
    private readonly float[] _lastInput = new float[4];
    private readonly byte[] _applied = new byte[4];
    private readonly Replica _replica = new();
    private bool _clientSceneReady;
    private float _nextInput, _nextSnapshot, _lastFrame;
    private int _lastBroadcastCoins = -1;
    private byte _lastSent;

    private void ConfigureGame(int count, Theme theme, int level)
    {
        Game.isUsingTouchControls = false;
        Game.playerSkins = _skins.Take(count).Select(s => (Player.Skin)s).ToArray();
        Game.themeChoice = theme;
        Game.levelId = new LevelId(theme, level);
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

    private void UpdateHost(float now)
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

    private void UpdateClient(float now)
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

    private void BroadcastSnapshot()
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

    private void UpdateClientScene()
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

    internal void ApplyInput(int number)
    {
        int index = number - 1;
        // P1 uses native input; this path only suppresses it while the panel is open or unfocused.
        byte value = _started && index != 0 ? _inputs[index].Advance() : (byte)0;
        // 先消费输入队列，再比较上一帧，保持短按边沿及原生 P1 输入顺序。
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
}