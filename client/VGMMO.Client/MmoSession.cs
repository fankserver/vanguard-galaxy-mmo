using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using Source.Galaxy;
using Source.Player;
using UnityEngine;
using VGMMO.Protocol;

namespace VGMMO.Client;

/// <summary>
/// Client session: LiteNetLib transport + protocol handling. Runs on the main
/// thread (PollEvents from Update). Sends the local player's ship state and
/// renders remote player markers from relayed RemoteShipState messages.
/// </summary>
public sealed class MmoSession : MonoBehaviour
{
    private const string ConnectKey = "vgmmo";
    private const float SendInterval = 0.2f;
    private const float HeartbeatInterval = 2f;
    private const int MaxFrame = 64 * 1024;

    private EventBasedNetListener? _listener;
    private NetManager? _net;
    private NetPeer? _peer;

    private string _host = "";
    private int _port;
    private string _name = "";
    private string _buildHash = "";
    private string _sessionId = "";
    private bool _joined;
    private bool _inCluster;
    private string _clusterSystem = "";
    private readonly Dictionary<string, string> _hosted = new(StringComparer.Ordinal); // storyId -> systemId
    private float _sendTimer;
    private float _heartbeatTimer;
    private long _lastPingT;
    private float _reconnectTimer;
    private readonly bool _autoReconnect = true;

    public bool Connected => _peer != null && _peer.ConnectionState == ConnectionState.Connected;
    public string Status { get; private set; } = "disconnected";
    public long PingMs { get; private set; }
    public string SessionId => _sessionId;

    private readonly List<string> _log = new();
    private readonly Dictionary<string, GameObject> _markers = new();

    public IReadOnlyList<string> LogLines => _log;

    private void Awake()
    {
        _listener = new EventBasedNetListener();
        _net = new NetManager(_listener) { UpdateTime = 15 };
        _listener.PeerConnectedEvent += OnPeerConnected;
        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            if (info.Reason == DisconnectReason.Timeout) Log("server unreachable/timeout");
            else Log("disconnected: " + info.Reason);
            Status = "disconnected";
        };
        _listener.NetworkReceiveEvent += (peer, reader, channel, delivery) =>
        {
            var frame = TryReadFrame(reader);
            if (frame != null) Handle(Wire.Decode(frame));
        };
    }

    private void Update()
    {
        _net?.PollEvents();

        // Auto-reconnect (dev/test convenience): survive server restarts.
        if (!Connected && _autoReconnect && _host.Length > 0)
        {
            _reconnectTimer += Time.deltaTime;
            if (_reconnectTimer >= 5f)
            {
                _reconnectTimer = 0f;
                try { _net.Start(); _net.Connect(_host, _port, ConnectKey); }
                catch { /* retry next tick */ }
            }
        }

        if (!Connected) return;

        _heartbeatTimer += Time.deltaTime;
        if (_heartbeatTimer >= HeartbeatInterval)
        {
            _heartbeatTimer = 0f;
            Send(MsgTypes.Ping, new PingMessage { T = NowMs() });
        }

        // Presence: we are only "in the MMO cluster" when the current in-game system has
        // a storyId the server hosts. Everywhere else (vanilla systems) we stay silent.
        var current = GamePlayer.current?.currentSystem;
        string? targetId = null;
        var inCluster = current != null && !string.IsNullOrEmpty(current.storyId)
            && _hosted.TryGetValue(current.storyId, out targetId);
        if (inCluster != _inCluster)
        {
            _inCluster = inCluster;
            if (inCluster)
            {
                _clusterSystem = targetId!;
                Send(MsgTypes.JoinWorld, new JoinWorldRequest { SystemId = _clusterSystem });
                Log($"entered MMO cluster: {current!.name} ({current.storyId}) -> join {_clusterSystem}");
            }
            else
            {
                Send(MsgTypes.LeaveWorld, new object());
                Log("left MMO cluster");
            }
        }

        _sendTimer += Time.deltaTime;
        if (_sendTimer >= SendInterval && _inCluster)
        {
            _sendTimer = 0f;
            var rb = LocalShip();
            if (rb != null)
            {
                var pos = rb.position;
                var vel = rb.linearVelocity;
                Send(MsgTypes.ShipState, new ShipStateMessage
                {
                    SystemId = _clusterSystem,
                    X = pos.x, Y = pos.y,
                    Rot = rb.rotation,
                    Vx = vel.x, Vy = vel.y,
                    Seq = ++_sendSeq,
                }, reliable: false);
            }
        }
    }

    private uint _sendSeq;

    private static Rigidbody2D? LocalShip()
    {
        var gm = GameplayManager.Instance;
        if (gm == null || gm.spaceShip == null) return null;
        return gm.spaceShip.GetComponent<Rigidbody2D>();
    }

    public void Connect(string host, int port, string name, string buildHash)
    {
        if (Connected || _net == null) return;
        _host = host; _port = port; _name = name; _buildHash = buildHash;
        try
        {
            _net.Start();
            _net.Connect(host, port, ConnectKey);
            Status = "connecting...";
            Log($"connecting to {host}:{port}");
        }
        catch (System.Exception e)
        {
            Log("connect failed: " + e.Message);
            Status = "connect failed";
        }
    }

    public void Disconnect()
    {
        _peer?.Disconnect();
        _net?.Stop();
        Status = "disconnected";
        Log("disconnected");
    }

    private void OnPeerConnected(NetPeer peer)
    {
        _peer = peer;
        Status = "hello...";
        Send(MsgTypes.Hello, new HelloRequest
        {
            Protocol = ProtocolInfo.Version,
            GameVersion = Application.version,
            ClientBuildHash = _buildHash,
            Name = _name,
        });
    }

    private void Handle(Envelope env)
    {
        switch (env.Type)
        {
            case MsgTypes.Welcome:
                var w = Wire.Read<WelcomeResponse>(env);
                _sessionId = w?.SessionId ?? "";
                Status = "joined world " + (w?.WorldId ?? "?");
                _hosted.Clear();
                foreach (var s in w?.Systems ?? new List<WelcomeSystem>())
                    if (!string.IsNullOrEmpty(s.StoryId)) _hosted[s.StoryId] = s.Id;
                Log($"welcome: session {_sessionId} world {w?.WorldId} (layout {w?.LayoutHash?[..12]}) hosted {_hosted.Count} systems");
                break;
            case MsgTypes.SystemEntered:
                var se = Wire.Read<SystemEnteredMessage>(env);
                _joined = true;
                Log("entered system " + (se?.SystemId ?? "?"));
                break;
            case MsgTypes.Refused:
                var r = Wire.Read<RefusedResponse>(env);
                Log("refused: " + (r?.Reason ?? "?"));
                Status = "refused";
                Disconnect();
                break;
            case MsgTypes.Pong:
                PingMs = NowMs() - _lastPingT;
                Status = $"connected · {PingMs} ms";
                break;
            case MsgTypes.RemoteShipState:
                var rs = Wire.Read<RemoteShipState>(env);
                if (rs != null) UpdateMarker(rs);
                break;
            case MsgTypes.Event:
                var ev = Wire.Read<EventMessage>(env);
                Log($"EVENT [{ev?.SystemId}] {ev?.Command} " + (ev?.Args == null ? "" : ev.Args.ToString()));
                break;
            case MsgTypes.LootSpawn:
                var ls = Wire.Read<LootSpawnMessage>(env);
                Log($"loot spawn: {ls?.Amount}x {ls?.ItemId} @ ({ls?.X:F1},{ls?.Y:F1}) in {ls?.SystemId}");
                break;
            case MsgTypes.LootGone:
                var lg = Wire.Read<LootGoneMessage>(env);
                Log($"loot gone: {lg?.InstanceId?[..6]} taken by {lg?.Taker}");
                break;
            case MsgTypes.TradeOffer:
                var to = Wire.Read<TradeOfferMessage>(env);
                Log($"trade offer {to?.TradeId}: {to?.From} -> {to?.To} (accept/decline not implemented yet)");
                break;
            case MsgTypes.TradeResult:
                var tr = Wire.Read<TradeResultMessage>(env);
                Log($"trade {tr?.TradeId}: {(tr?.Ok == true ? "ok" : "failed " + tr?.Reason)}");
                break;
            default:
                Log("msg " + env.Type);
                break;
        }
    }

    private void UpdateMarker(RemoteShipState rs)
    {
        if (!_markers.TryGetValue(rs.SessionId, out var marker) || marker == null)
        {
            Log($"remote player {rs.Name} appeared @ ({rs.X:F0},{rs.Y:F0})");
            try
            {
                marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                var col = marker.GetComponent<Collider>();
                if (col != null) Destroy(col);
                marker.transform.localScale = new Vector3(3f, 3f, 1f);
                marker.transform.SetParent(transform, worldPositionStays: true);
                var name = new GameObject("label").AddComponent<TextMesh>();
                name.transform.SetParent(marker.transform, false);
                name.transform.localPosition = new Vector3(0f, 2.2f, 0f);
                name.text = rs.Name;
                name.characterSize = 0.18f;
                _markers[rs.SessionId] = marker;
            }
            catch (System.Exception e) { Log("marker: " + e.Message); return; }
        }
        var renderer = marker.GetComponent<Renderer>();
        if (renderer != null) renderer.material.color = ColorFor(rs.Name);
        marker.transform.position = new Vector3(rs.X, rs.Y, 0f);
    }

    private static Color ColorFor(string name)
    {
        var hash = 0u;
        foreach (var c in name) hash = hash * 31u + c;
        return Color.HSVToRGB((hash % 360) / 360f, 0.55f, 1f);
    }

    private void Send(string type, object msg, bool reliable = true)
    {
        var peer = _peer;
        if (peer == null || peer.ConnectionState != ConnectionState.Connected) return;
        var frame = Wire.Encode(type, msg);
        var w = new NetDataWriter();
        w.Put(frame.Length);
        w.Put(frame);
        peer.Send(w, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
    }

    private static byte[]? TryReadFrame(NetDataReader reader)
    {
        if (reader.AvailableBytes < 4) return null;
        var len = reader.GetInt();
        if (len <= 0 || len > MaxFrame) return null;
        if (reader.AvailableBytes < len) return null;
        return reader.GetRemainingBytes();
    }

    private static long NowMs() => System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void OnDestroy()
    {
        _net?.Stop();
    }

    public void Log(string line)
    {
        lock (_log)
        {
            _log.Add($"[{System.DateTime.Now:HH:mm:ss}] {line}");
            if (_log.Count > 80) _log.RemoveAt(0);
        }
        MmoPlugin.Instance?.LogSource.LogInfo(line);
    }
}
