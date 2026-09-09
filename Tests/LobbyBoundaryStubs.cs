// Only external boundaries are simulated. Lobby/network decisions come from linked production partials.
using System;
using System.Collections.Generic;
using System.IO;

namespace LiteNetLib.Utils
{
    public sealed class NetDataWriter
    {
        private readonly MemoryStream stream = new();
        private BinaryWriter Writer => new(stream);
        public void Put(byte value) => Writer.Write(value);
        public void Put(bool value) => Writer.Write(value);
        public void Put(int value) => Writer.Write(value);
        public void Put(ushort value) => Writer.Write(value);
        public void Put(byte[] value) => Writer.Write(value);
        public byte[] Data => stream.ToArray();
    }
}
namespace LiteNetLib
{
    public enum DeliveryMethod { ReliableOrdered, Unreliable }
    public sealed class NetPeer
    {
        public readonly List<byte[]> Sent = new();
        public readonly List<DeliveryMethod> Methods = new();
        public bool Disconnected;
        public void Send(Utils.NetDataWriter writer, DeliveryMethod method)
        { Sent.Add(writer.Data); Methods.Add(method); }
        public void Disconnect() => Disconnected = true;
    }
    public sealed class NetPacketReader
    {
        private readonly MemoryStream stream;
        private readonly BinaryReader reader;
        public NetPacketReader(byte[] data) { stream = new(data); reader = new(stream); }
        public int AvailableBytes => (int)(stream.Length - stream.Position);
        public int Position => (int)stream.Position;
        public void SetPosition(int position) => stream.Position = position;
        public byte GetByte() => reader.ReadByte();
        public bool GetBool() => reader.ReadBoolean();
        public int GetInt() => reader.ReadInt32();
        public ushort GetUShort() => reader.ReadUInt16();
        public byte[] GetRemainingBytes() => reader.ReadBytes(AvailableBytes);
    }
    public sealed class ConnectionRequest
    {
        public bool Rejected;
        public string AcceptedKey;
        public void Reject() => Rejected = true;
        public void AcceptIfKey(string key) => AcceptedKey = key;
    }
    public sealed class DisconnectInfo { public string Reason = "test disconnect"; }
    public sealed class EventBasedNetListener
    {
        public event Action<ConnectionRequest> ConnectionRequestEvent;
        public event Action<NetPeer> PeerConnectedEvent;
        public event Action<NetPeer, DisconnectInfo> PeerDisconnectedEvent;
        public event Action<NetPeer, NetPacketReader, byte, DeliveryMethod> NetworkReceiveEvent;
        public event Action<object, object> NetworkErrorEvent;
        public void Request(ConnectionRequest request) => ConnectionRequestEvent?.Invoke(request);
        public void Connect(NetPeer peer) => PeerConnectedEvent?.Invoke(peer);
        public void Disconnect(NetPeer peer) => PeerDisconnectedEvent?.Invoke(peer, new());
        public void Receive(NetPeer peer, byte[] bytes) => NetworkReceiveEvent?.Invoke(peer, new(bytes), 0, DeliveryMethod.ReliableOrdered);
        public void Error(object error) => NetworkErrorEvent?.Invoke(null, error);
    }
    public sealed class NetManager
    {
        public readonly EventBasedNetListener Listener;
        public NetManager(EventBasedNetListener listener) => Listener = listener;
        public bool AutoRecycle, IPv6Enabled;
        public int DisconnectTimeout;
        public bool Start(int port) => true;
        public void Connect(string address, int port, string key) { }
    }
}
namespace UnityEngine
{
    public static class Application { public static bool runInBackground; }
    public static class Time { public static float realtimeSinceStartup; }
}
namespace UnityEngine.SceneManagement
{
    public static class SceneManager
    {
        public static string LoadedScene;
        public static void LoadScene(string scene) => LoadedScene = scene;
    }
}
public sealed class Game { public static Game instance; }
public sealed class MenuScene { public static MenuScene instance = new(); }
public enum Theme { Forest = 1 }
public sealed class LevelId
{
    public LevelId(Theme theme, int level) { }
    public bool DoesExist() => true;
}
namespace TwinShotNet
{
    public sealed partial class Plugin
    {
        private readonly InputSlot[] _inputs = { new(), new(), new(), new() };
        private readonly byte[] _applied = new byte[4];
        private readonly float[] _lastInput = new float[4];
        private string _port = "9050", _password = "test-room-key", _fingerprint = "test", _address = "localhost", _status;
        private bool _visible, _oldBackground, _backgroundOwned;
        private float _lastFrame, _lastSent;
        private readonly TestLogger Logger = new();
        private void Close(bool unused) { }
        // Native menu sampling is a boundary. Intentionally does NOT normalize or broadcast:
        // StartMatch must enforce its own ordering, independent of side effects of sampling.
        private void SyncHostLocalState() { }
        private void SyncHostRemoteBoxes() { }
        private void SyncNativeLobby() { }
        private int themeShows, configuredPlayers;
        private void ShowNativeThemeSelect() => themeShows++;
        private void ConfigureGame(int players, Theme theme, LevelId level) => configuredPlayers = players;
        private void ApplyCompletionCoins(int coins) { }
        private sealed class TestLogger
        {
            public void LogWarning(object message) { }
            public void LogError(object message) { }
        }
    }
}
