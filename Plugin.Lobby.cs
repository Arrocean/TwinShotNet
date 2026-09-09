using System;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;

namespace TwinShotNet;

public sealed partial class Plugin
{
    private bool _started, _themePhase;
    private int _players = 2;
    private float _nextLobbySync;
    private readonly int[] _skins = { 0, 1, 2, 3 };
    private readonly bool[] _occupied = new bool[4];
    private readonly bool[] _ready = new bool[4];
    // 槽位归属：本地原生箱子与远端 peer 互斥，是输入路由和槽位重排的唯一依据 (F2/F13)。
    private readonly bool[] _localOccupied = new bool[4];
    private readonly bool[] _remoteSlot = new bool[4];
    internal bool InLobby => _network != null && !_started;
    internal bool ThemePhase => _themePhase;

    internal bool IsRemoteSlot(int slot) => slot >= 0 && slot < 4 && _remoteSlot[slot];

    private int LocalPlayerCount() => Enumerable.Range(0, 4).Count(i => _localOccupied[i]);

    // 关卡只创建 1..N 号玩家（N = Game.playerSkins.Length），因此开局人数必须取最高占用槽位 + 1 (F2)。
    private int HighestOccupiedSlot() => SlotMap.HighestOccupied(_occupied);

    // 把远端 peer 重排到未被本地箱子占用的最低空槽，0 号槽位始终留给主机 P1。
    // 中间槽位掉线后不重排，就会出现 assigned > players 的 match 包或两个玩家共用一个槽位 (F2)。
    private void RepackRemoteSlots()
    {
        if (_peers.Count == 0) return;
        int[] targets = SlotMap.AssignRemote(_localOccupied, _peers.Count);
        if (targets == null)
        {
            // 连接阶段已限制 peer 数量，正常不可达；宁可断开也不要留下错位槽位。
            foreach (var entry in _peers.ToArray())
            {
                _remoteSlot[entry.Value] = false;
                _occupied[entry.Value] = false;
                _ready[entry.Value] = false;
                _inputs[entry.Value].Clear();
                entry.Key.Disconnect();
            }

            _peers.Clear();
            return;
        }

        int index = 0;
        foreach (var entry in _peers.OrderBy(pair => pair.Value).ToArray())
        {
            int target = targets[index++];
            if (target == entry.Value) continue;
            int from = entry.Value;
            _peers[entry.Key] = target;
            _remoteSlot[from] = false;
            _remoteSlot[target] = true;
            _inputs[from].Clear();
            _inputs[target].Clear();
            // 边沿基准必须跟着槽位一起搬，否则新占用者会丢掉一次 justPressed。
            _applied[from] = _applied[target] = 0;
            _lastInput[from] = _lastInput[target] = 0;
            _occupied[from] = false;
            _ready[from] = false;
            _occupied[target] = true;
            // 槽位变化后该 peer 必须重新确认就绪，避免用旧 ready 开局。
            _ready[target] = false;
            _skins[target] = _skins[from];
            SendSlotAssignment(entry.Key, target);
        }
    }

    private void SendSlotAssignment(NetPeer peer, int slot)
    {
        var writer = new NetDataWriter();
        writer.Put((byte)1);
        writer.Put((byte)(slot + 1));
        peer.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    private void StartMatch()
    {
        SyncHostLocalState();
        // Normalize ownership before validating readiness: repacking clears readiness on moved peers.
        // Publish this normalization before either returning unready or entering theme selection.
        RepackRemoteSlots();
        BroadcastLobby();
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

        _players = Math.Max(2, HighestOccupiedSlot());
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
        // 主机只回写远端槽位，本地槽位留给原生输入；客户端整屏回写以跟随主机 (F5)。
        if (_host) SyncHostRemoteBoxes();
        else SyncNativeLobby();
    }

    // 客户端的选择只能通过主机确认；主机状态来自原生箱子，不走这条路径 (F9)。
    private void SendLobbyChange(int skin, bool isReady)
    {
        if (_host || _server == null || _assigned == 0) return;
        var writer = new NetDataWriter();
        writer.Put((byte)5);
        writer.Put((byte)skin);
        writer.Put(isReady);
        _server.Send(writer, DeliveryMethod.ReliableOrdered);
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
