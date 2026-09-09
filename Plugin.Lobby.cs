using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;

namespace TwinShotNet;

public sealed partial class Plugin
{
    private bool _started, _themePhase;
    private int _players = 2, _hostLocalPlayers = 1;
    private float _nextLobbySync;
    private readonly int[] _skins = { 0, 1, 2, 3 };
    private readonly bool[] _occupied = new bool[4];
    private readonly bool[] _ready = new bool[4];
    internal bool InLobby => _network != null && !_started;
    internal bool ThemePhase => _themePhase;

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
}