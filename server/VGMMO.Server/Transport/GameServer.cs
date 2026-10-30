using LiteNetLib;
using LiteNetLib.Utils;
using VGMMO.Protocol;

namespace VGMMO.Server.Transport;

/// <summary>
/// UDP game transport (LiteNetLib). Frames are [int length][UTF-8 JSON envelope].
/// Commands are ReliableOrdered; high-frequency states are Unreliable.
/// Events are delivered on the polling thread and handled inline (single-threaded core).
/// </summary>
public sealed class GameServer : IDisposable
{
    public const int MaxFrame = 64 * 1024;
    private const int HelloTimeoutMs = 5000;

    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _manager;
    private readonly Core.ServerCore _core;
    public string Address { get; private set; }

    public GameServer(Core.ServerCore core)
    {
        _core = core;
        _manager = new NetManager(_listener)
        {
            UpdateTime = 15,
        };

        _listener.ConnectionRequestEvent += request =>
        {
            if (_manager.ConnectedPeersCount >= _core.Config.MaxPeers) { request.Reject(); return; }
            request.Accept();
        };
        _listener.PeerConnectedEvent += peer => _core.OnPeerConnected(peer);
        _listener.PeerDisconnectedEvent += (peer, info) => _core.OnPeerDisconnected(peer);
        _listener.NetworkReceiveEvent += (peer, reader, channel, deliveryMethod) =>
        {
            {
                var frame = TryReadFrame(reader);
                if (frame == null) return;
                try
                {
                    _core.OnFrame(peer, Wire.Decode(frame));
                }
                catch (Exception e)
                {
                    _core.LogError($"malformed frame from {peer.EndPoint}: {e.Message}");
                }
            }
        };
    }

    private readonly Dictionary<int, NetPeer> _peersById = new();

    public void Start(int port)
    {
        if (!_manager.Start(port))
            throw new InvalidOperationException($"failed to bind UDP port {port}");
        Address = $"udp://0.0.0.0:{_manager.LocalPort}";
        _peersById.Clear();
    }

    public int Port => _manager.LocalPort;
    public void Poll() => _manager.PollEvents();
    public int ConnectedPeers => _manager.ConnectedPeersCount;
    public string? PeerEndPoint(int id) => _peersById.TryGetValue(id, out var p) ? p.EndPoint.ToString() : null;

    public void TrackPeer(NetPeer peer) => _peersById[peer.Id] = peer;
    public void UntrackPeer(NetPeer peer) => _peersById.Remove(peer.Id);

    public void SendPeer(int peerId, string type, object? msg, bool reliable = true)
    {
        if (_peersById.TryGetValue(peerId, out var peer)) Send(peer, type, msg, reliable);
    }

    public void DisconnectPeer(int peerId)
    {
        if (_peersById.TryGetValue(peerId, out var peer)) peer.Disconnect();
    }

    public void Send(NetPeer peer, string type, object? msg, bool reliable = true)
    {
        if (peer.ConnectionState != ConnectionState.Connected) return;
        var frame = Wire.Encode(type, msg);
        var writer = new NetDataWriter();
        writer.Put(frame.Length);
        writer.Put(frame);
        peer.Send(writer, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
    }

    public static byte[]? TryReadFrame(NetDataReader reader)
    {
        if (reader.AvailableBytes < 4) return null;
        int len = reader.GetInt();
        if (len <= 0 || len > MaxFrame) return null;
        if (reader.AvailableBytes < len) return null;
        return reader.GetRemainingBytes();
    }

    public void Dispose()
    {
        _manager.Stop();
    }
}
