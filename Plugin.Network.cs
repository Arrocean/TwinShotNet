using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TwinShotNet;

public sealed partial class Plugin
{
    internal bool Client => _network != null && !_host;
    internal bool Hosting => _network != null && _host;
    private NetManager _network;
    private NetPeer _server;
    private readonly Dictionary<NetPeer, int> _peers = new();
    private bool _host;
    private int _assigned, _sequence;
    private FrameAssembler _assembler = new();
    private FrameCache _frameCache = new();
    private byte[] _pendingFrame;

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
                // 远端槽位从本地主机人数之后分配，保留 0 作为无可用槽位的判定。
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
                ReceiveHostPacket(peer, reader, method, type);
                return;
            }

            ReceiveClientPacket(peer, reader, type);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Rejected network packet: " + ex.Message);
            peer.Disconnect();
        }
    }

    private void ReceiveHostPacket(NetPeer peer, NetPacketReader reader, DeliveryMethod method, byte type)
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
    }

    private void ReceiveClientPacket(NetPeer peer, NetPacketReader reader, byte type)
    {
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

    private void UpdateTransport()
    {
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
    }
}