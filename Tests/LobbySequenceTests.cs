using System;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;

namespace TwinShotNet;

// Same partial type grants access to private production state without reflection or public test hooks.
public sealed partial class Plugin
{
    internal static void RunLobbySequenceTests()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("Lobby sequence: " + name);
            checks++;
        }
        Plugin Host()
        {
            var host = new Plugin();
            host.Open(true);
            host._ready[0] = true;
            return host;
        }
        void Local(Plugin host, int slot, bool occupied)
        {
            // Supply native-menu observations at the boundary; all subsequent transitions are production.
            host._localOccupied[slot] = host._occupied[slot] = occupied;
            host._ready[slot] = occupied;
        }
        NetPeer Join(Plugin host)
        {
            var peer = new NetPeer();
            host._network.Listener.Connect(peer);
            return peer;
        }
        void Ready(Plugin host, NetPeer peer, byte skin) =>
            host._network.Listener.Receive(peer, new byte[] { 5, skin, 1 });
        bool LobbyState(byte[] packet, int slot, bool occupied, bool ready) =>
            packet[0] == 6 && (packet[1 + slot * 3] != 0) == occupied &&
            (packet[3 + slot * 3] != 0) == ready;

        // Regression: a local P2 leaves immediately before StartMatch; remote P3 was ready.
        // Native sampling is deliberately inert, so StartMatch itself must repack, broadcast and validate.
        var host = Host();
        Local(host, 1, true);
        var peer = Join(host);
        Check(host._peers[peer] == 2 && host._localOccupied[1], "join skips local P2");
        Check(peer.Sent.Select(p => p[0]).SequenceEqual(new byte[] { 1, 6 }), "join assignment precedes lobby");
        Check(peer.Sent[0][1] == 3, "assignment is one-based");
        Ready(host, peer, 2);
        Check(host._ready[2], "real ready packet accepted");
        Local(host, 1, false);
        host._inputs[2].Receive(16);
        host._applied[2] = 16;
        host._lastInput[2] = 9;
        peer.Sent.Clear();
        host.StartMatch();
        Check(!host._themePhase && host.themeShows == 0, "repacked ready peer blocks theme transition");
        Check(host._peers[peer] == 1 && !host._ready[1] && !host._occupied[2], "repack clears moved readiness and old occupancy");
        Check(peer.Sent.Select(p => p[0]).SequenceEqual(new byte[] { 1, 6 }), "start repack assignment then broadcast even when blocked");
        Check(LobbyState(peer.Sent[1], 1, true, false) && LobbyState(peer.Sent[1], 2, false, false), "broadcast publishes cleared ready and vacated slot");
        Check(host._inputs[2].Advance() == 0 && host._inputs[1].Advance() == 0 &&
              host._applied[1] == 0 && host._applied[2] == 0 && host._lastInput[2] == 0, "repack releases queued input and edge baselines");
        Ready(host, peer, 2);
        Check(host._ready[1], "ready follows reassigned ownership");
        peer.Sent.Clear();
        host.StartMatch();
        Check(host._themePhase && host.themeShows == 1 && host._players == 2, "fresh ready permits theme with normalized count");
        Check(peer.Sent.Select(p => p[0]).SequenceEqual(new byte[] { 6, 9 }), "ready lobby published before theme packet");
        Check(peer.Methods.All(m => m == DeliveryMethod.ReliableOrdered), "assignments lobby and theme use reliable ordering");
        Check(!host.AllowNativeContinue() && host.themeShows == 1, "native continue cannot reenter theme");
        var request = new ConnectionRequest();
        host._network.Listener.Request(request);
        Check(request.Rejected, "theme rejects connection requests");
        var late = Join(host);
        Check(late.Disconnected && !host._peers.ContainsKey(late), "theme rejects already accepted late connection");

        // Disconnect real callback with two surviving remote peers and a freed middle slot.
        host = Host();
        var first = Join(host);
        var second = Join(host);
        var third = Join(host);
        Ready(host, second, 2);
        Ready(host, third, 3);
        second.Sent.Clear(); third.Sent.Clear();
        host._network.Listener.Disconnect(first);
        Check(host._peers[second] == 1 && host._peers[third] == 2 && host._peers.Count == 2, "disconnect compacts multiple survivors");
        Check(host._skins[1] == 2 && host._skins[2] == 3 && !host._occupied[3], "skins survive chained moves and tail clears");
        Check(!host._ready[1] && !host._ready[2] && host._ready[0], "disconnect resets only moved readiness");
        Check(second.Sent.Select(p => p[0]).SequenceEqual(new byte[] { 1, 6 }) &&
              third.Sent.Select(p => p[0]).SequenceEqual(new byte[] { 1, 6 }), "each survivor receives assignment before shared broadcast");
        Check(LobbyState(second.Sent[1], 1, true, false) && LobbyState(second.Sent[1], 2, true, false) &&
              LobbyState(second.Sent[1], 3, false, false), "disconnect broadcast contains final complete repack");
        Ready(host, second, 0);
        Check(host._skins[1] == 0 && !host._ready[1], "skin change resets readiness despite ready bit");
        Ready(host, second, 0);
        Check(host._ready[1], "second confirmation readies new skin");
        host.StartMatch();
        Check(!host._themePhase, "other unready survivor still blocks start");
        Ready(host, third, 3);
        host.StartMatch();
        Check(host._themePhase && host._players == 3, "all surviving peers ready starts three-player phase");

        host = Host();
        Local(host, 3, true);
        peer = Join(host);
        Ready(host, peer, 1);
        host.StartMatch();
        Check(host._players == 4 && host._localOccupied[3], "sparse local P4 determines match player count");
        host = Host();
        Local(host, 1, true); Local(host, 2, true); Local(host, 3, true);
        peer = Join(host);
        Check(peer.Disconnected && host._peers.Count == 0, "full local occupancy rejects remote join");
        host.StartMatch();
        Check(!host._themePhase && host._status.Contains("friend"), "no remote peer cannot start");
        host._started = true;
        request = new(); host._network.Listener.Request(request);
        Check(request.Rejected && Join(host).Disconnected, "running match rejects request and late connection");

        // Client consumes actual assignment/lobby/theme/match packet handlers via Receive validation.
        var client = new Plugin();
        client.Open(false);
        var server = new NetPeer();
        client._network.Listener.Connect(server);
        client._network.Listener.Receive(server, new byte[] { 1, 3 });
        Check(client._assigned == 3, "client accepts initial assignment");
        client._network.Listener.Receive(server, new byte[] { 1, 2 });
        Check(client._assigned == 2, "client accepts pre-match reassignment");
        var lobby = new NetDataWriter();
        lobby.Put((byte)6);
        for (int i = 0; i < 4; i++)
        {
            lobby.Put(i < 2); lobby.Put((byte)i); lobby.Put(i == 0);
        }
        client._network.Listener.Receive(server, lobby.Data);
        Check(client._occupied[1] && !client._ready[1] && !client._occupied[2] && client._skins[1] == 1,
              "client applies normalized lobby after assignment");
        client._network.Listener.Receive(server, new byte[] { 9 });
        Check(client._themePhase && client.themeShows == 1, "client enters theme from host packet");
        var match = new NetDataWriter();
        match.Put((byte)8); match.Put((byte)1); match.Put(1);
        match.Put((byte)2); match.Put((byte)2); match.Put((byte)0); match.Put((byte)1);
        client._network.Listener.Receive(server, match.Data);
        Check(client._started && !client._themePhase && client.configuredPlayers == 2 &&
              UnityEngine.SceneManagement.SceneManager.LoadedScene == "Game", "valid match configures game and exits theme");
        client._network.Listener.Receive(server, new byte[] { 1, 4 });
        client._network.Listener.Receive(server, new byte[] { 9 });
        client._network.Listener.Receive(server, match.Data);
        Check(client._assigned == 2 && !client._themePhase && client.themeShows == 1, "running client ignores reassignment repeated theme and match");
        client._network.Listener.Disconnect(server);
        Check(client._assigned == 0 && client._server == null, "client disconnect clears assignment and server");
        Console.WriteLine($"Production lobby sequences: {checks} checks passed");
    }
}
