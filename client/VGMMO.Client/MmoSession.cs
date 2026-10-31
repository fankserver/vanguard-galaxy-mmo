using System;
using System.Collections.Generic;
using System.Linq;
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
    private string _name = "";            // nickname (MMO handle)
    private string _fullName = "";        // captain GetFullName() from the save
    private string _title = "";           // selected title display name
    private string _titleColor = "";      // hex color of the title
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
    private readonly Dictionary<string, string> _markerShip = new();   // session -> cloned ship id
    private readonly Dictionary<string, GameObject> _markerLabels = new();

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
            // Presence resets so the next reconnect re-sends JoinWorld (the server
            // forgets our system when the session ends).
            _inCluster = false; _clusterSystem = "";
            MmoPlugin.Instance?.World.RequestTeardown();
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
                RefreshIdentity();   // commander is loaded now — authoritative identity
                Send(MsgTypes.JoinWorld, new JoinWorldRequest { SystemId = _clusterSystem, Name = _name, FullName = _fullName, Title = _title, TitleColor = _titleColor });
                Log($"entered MMO cluster: {current!.name} ({current.storyId}) -> join {_clusterSystem}");
            }
            else
            {
                Send(MsgTypes.LeaveWorld, new object());
                Log("left MMO cluster");
            }
        }
        else if (inCluster && targetId != null && targetId != _clusterSystem)
        {
            // Cluster-internal travel (hub -> salvage, via gate): the server still
            // thinks we are in the previous system — position was rendered against
            // the wrong origin and presence groups were stale. Re-join the new one.
            _clusterSystem = targetId;
            Send(MsgTypes.JoinWorld, new JoinWorldRequest { SystemId = _clusterSystem, Name = _name, FullName = _fullName, Title = _title, TitleColor = _titleColor });
            Log($"resystem: {current!.name} ({current.storyId}) -> {_clusterSystem}");
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
                // Server frame = layout/POI-local coords (the game's own mapping:
                // world = local * mapToLocalConversion). Authoritative on the server.
                var scale = MapPointOfInterest.mapToLocalConversion;
                Send(MsgTypes.ShipState, new ShipStateMessage
                {
                    SystemId = _clusterSystem,
                    ShipId = CurrentShipId(),
                    X = pos.x / scale, Y = pos.y / scale,
                    Rot = rb.rotation,
                    Vx = vel.x / scale, Vy = vel.y / scale,
                    Seq = ++_sendSeq,
                }, reliable: false);
            }
        }
    }

    private uint _sendSeq;

    /// <summary>Ship class identity (unitData.name = definition display name). Sent so
    /// remote mirrors can pick matching visuals — and re-clone when the ship is
    /// swapped, which is authoritative state, not animation.</summary>
    private string CurrentShipId()
    {
        var ship = GameplayManager.Instance?.spaceShip;
        return ship?.unitData?.name ?? "";
    }

    private static Rigidbody2D? LocalShip()
    {
        var gm = GameplayManager.Instance;
        if (gm == null || gm.spaceShip == null) return null;
        return gm.spaceShip.GetComponent<Rigidbody2D>();
    }

    /// <summary>Reads the captain identity from the loaded save: the NICKNAME is the
    /// callsign (that's what the web UI shows), the full name is GetFullName()
    /// (firstName "callsign" lastName) + the selected title. The hello happens at
    /// connect (often before a save exists); the cluster-entry JoinWorld carries the
    /// authoritative identity.</summary>
    private void RefreshIdentity()
    {
        var gp = GamePlayer.current;
        if (gp?.commander == null) return;
        try
        {
            // Nickname = callsign (save identity), fallback firstName, then config name.
            _name = gp.commander.callsign.Length > 0 ? gp.commander.callsign
                  : gp.commander.firstName.Length > 0 ? gp.commander.firstName
                  : _name;
            _fullName = gp.commander.GetFullName();
            var id = gp.commander.selectedTitle;
            if (string.IsNullOrEmpty(id))
            {
                _title = ""; _titleColor = "";
            }
            else
            {
                _title = Titles.Get(id)?.GetDisplayName() ?? id;
                _titleColor = "#" + ColorUtility.ToHtmlStringRGB(gp.commander.selectedTitleColor);
            }
        }
        catch (System.Exception e) { Log("identity: " + e.Message); }
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
        _inCluster = false; _clusterSystem = "";
        MmoPlugin.Instance?.World.RequestTeardown();
    }

    private void OnPeerConnected(NetPeer peer)
    {
        _peer = peer;
        Status = "hello...";
        RefreshIdentity();
        Send(MsgTypes.Hello, new HelloRequest
        {
            Protocol = ProtocolInfo.Version,
            GameVersion = Application.version,
            ClientBuildHash = _buildHash,
            Name = _name,
            FullName = _fullName,
            Title = _title,
            TitleColor = _titleColor,
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
                MmoPlugin.Instance?.World.SetLayout(w?.World);
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
        if (!_markers.TryGetValue(rs.SessionId, out var marker) || marker == null
            || _markerShip.GetValueOrDefault(rs.SessionId) != rs.ShipId)
        {
            if (marker != null)
            {
                Destroy(marker);
                _markers.Remove(rs.SessionId);
                _markerShip.Remove(rs.SessionId);
                if (_markerLabels.Remove(rs.SessionId, out var old)) Destroy(old.gameObject);
            }
            CreateRemoteMarker(rs);
        }
        marker = _markers.GetValueOrDefault(rs.SessionId);
        if (marker == null) return;
        var renderer = marker.GetComponent<Renderer>();
        if (renderer != null) renderer.material.color = ColorFor(rs.Name);
        // rs position is in server layout frame; game world = local * mapToLocalConversion.
        var scale = MapPointOfInterest.mapToLocalConversion;
        marker.transform.position = new Vector3(rs.X * scale, rs.Y * scale, 0f);
        // Direction: rot is Rigidbody2D degrees (0 = +X forward).
        marker.transform.rotation = Quaternion.Euler(0f, 0f, rs.Rot);
        // Keep the label upright (it would otherwise spin with the ship).
        if (_markerLabels.TryGetValue(rs.SessionId, out var label))
            label.transform.rotation = Quaternion.identity;
        // Animations are DERIVED from the transferred doing (velocity), never
        // replicated as data: the protocol carries authoritative state only.
        var speed = Mathf.Sqrt(rs.Vx * rs.Vx + rs.Vy * rs.Vy);
        var flameChild = marker.transform.Find("flame");
        if (flameChild != null)
        {
            var on = speed > 0.15f;
            flameChild.gameObject.SetActive(on);
            if (on)
            {
                var k = Mathf.Clamp(speed / 8f, 0.35f, 1.6f);
                flameChild.localScale = new Vector3(scale * 0.12f * k, scale * 0.07f * k, scale * 0.07f * k);
            }
        }
    }

    private void CreateRemoteMarker(RemoteShipState rs)
    {
        Log($"remote player {rs.Name} appeared @ ({rs.X:F0},{rs.Y:F0}) ship '{rs.ShipId}'");
        // Facts dump: verify the mirror frame against the game's own numbers.
        var cur = GamePlayer.current?.currentSystem;
        var poiLine = cur == null ? "no system" : string.Join(", ",
            cur.pointsOfInterest.Select(p => $"{p.name}({p.position.x * MapPointOfInterest.mapToLocalConversion:F0},{p.position.y * MapPointOfInterest.mapToLocalConversion:F0})"));
        Log($"mirror facts: conversion={MapPointOfInterest.mapToLocalConversion} rs=({rs.X:F2},{rs.Y:F2}) markerWorld=({rs.X * MapPointOfInterest.mapToLocalConversion:F0},{rs.Y * MapPointOfInterest.mapToLocalConversion:F0}) ship={LocalShip()?.position} pois=[{poiLine}]");
        try
        {
            var u = MapPointOfInterest.mapToLocalConversion;   // 250
            var marker = BuildShipVisual(rs.ShipId);
            if (marker == null)
            {
                // No ship visual available — primitive fallback with direction + flame.
                marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                var col = marker.GetComponent<Collider>();
                if (col != null) Destroy(col);
                marker.transform.localScale = new Vector3(u * 0.3f, u * 0.14f, u * 0.14f);
                var flame = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                var fc = flame.GetComponent<Collider>();
                if (fc != null) Destroy(fc);
                flame.name = "flame";
                flame.transform.SetParent(marker.transform, false);
                flame.transform.localPosition = new Vector3(-u * 0.24f, 0f, 0f);
                flame.transform.localScale = new Vector3(u * 0.12f, u * 0.07f, u * 0.07f);
                flame.GetComponent<Renderer>().material.color = new Color(1f, 0.55f, 0.1f);
                flame.SetActive(false);
            }
            marker.transform.SetParent(transform, false);
            var name = new GameObject("label").AddComponent<TextMesh>();
            name.transform.position = Vector3.zero;
            name.transform.SetParent(transform, false);
            name.text = (rs.Title.Length > 0 ? rs.Title + " " : "") + (rs.FullName.Length > 0 ? rs.FullName : rs.Name);
            name.characterSize = u * 0.035f;
            name.fontSize = 32;
            name.color = Color.white;
            _markers[rs.SessionId] = marker;
            _markerShip[rs.SessionId] = rs.ShipId;
            _markerLabels[rs.SessionId] = name.gameObject;
        }
        catch (System.Exception e) { Log("marker: " + e.Message); }
        return;
    }

    /// <summary>Builds a 1:1 visual copy of the ship identified by `shipId` — same sprites,
    /// same relative layout — so the mirror looks like the actual ship the pilot flies
    /// (including after a ship swap). Sources: the local ship when the class matches,
    /// else the fleet's ship data definition; null when neither matches.</summary>
    private GameObject? BuildShipVisual(string shipId)
    {
        var root = new GameObject("mirror-ship");
        var local = LocalShip();
        var localId = CurrentShipId();
        Transform? src = null;
        if (shipId.Length == 0 || shipId == localId) src = local != null ? local.transform : null;
        if (src == null && shipId.Length > 0)
        {
            var def = GamePlayer.current?.spaceShips?.FirstOrDefault(d => d.name == shipId)?.unitDefinition;
            if (def != null) src = def.transform;
        }
        if (src == null) { Destroy(root); return null; }
        root.transform.localScale = src.lossyScale;
        var n = CloneRenderers(root.transform, src, src);
        Log($"mirror: ship visual '{shipId}' — {n} sprites from '{src.name}'");
        return root;
    }

    private static int CloneRenderers(Transform dstRoot, Transform srcRoot, Transform relativeTo)
    {
        var count = 0;
        var relScale = relativeTo.lossyScale;
        var invRelRot = Quaternion.Inverse(relativeTo.rotation);
        foreach (var sr in srcRoot.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (sr.sprite == null) continue;
            var go = new GameObject(sr.name);
            go.transform.SetParent(dstRoot, false);
            go.transform.localPosition = relativeTo.InverseTransformPoint(sr.transform.position);
            go.transform.localRotation = invRelRot * sr.transform.rotation;
            var ls = sr.transform.lossyScale;
            go.transform.localScale = new Vector3(ls.x / relScale.x, ls.y / relScale.y, ls.z / relScale.z);
            var r = go.AddComponent<SpriteRenderer>();
            r.sprite = sr.sprite;
            r.color = sr.color;
            r.flipX = sr.flipX; r.flipY = sr.flipY;
            r.sortingLayerID = sr.sortingLayerID;
            r.sortingOrder = sr.sortingOrder;
            r.material = sr.material;
            r.drawMode = sr.drawMode;
            r.size = sr.size;
            count++;
        }
        return count;
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
