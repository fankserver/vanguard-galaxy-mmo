using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;
using Behaviour.Equipment.Module;
using Behaviour.Unit;
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
    private const float SendInterval = 0.1f;   // 10 Hz movement frames (trajectory-follow needs denser sampling)
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
    private readonly Dictionary<string, Vector3> _markerLabelOff = new();
    // INVARIANT (audit R1 follow-up): every _remoteUnits key must have a matching
    // _remoteState entry — TickStaleReplicas treats a missing pose as insta-stale
    // (despawn without grace). True today because only player keys enter (TrySpawn
    // requires name+fullName; companions pass null -> markers only). If real units
    // are ever spawned under companion keys (session#guid), key _remoteState by the
    // same handle key at packet time.
    private readonly Dictionary<string, RemoteUnitSpawner.Handle> _remoteUnits = new();

    private void RemoveLabel(string key)
    {
        if (_markerLabels.Remove(key, out var old)) Destroy(old.gameObject);
        _markerLabelOff.Remove(key);
    }

    private void CounterLabel(string key, Transform host)
    {
        if (!_markerLabels.TryGetValue(key, out var label)) return;
        var inv = Quaternion.Inverse(host.rotation);
        label.transform.localRotation = inv;
        label.transform.localPosition = inv * (_markerLabelOff.TryGetValue(key, out var off) ? off : Vector3.zero);
    }
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
                var comps = new List<CompanionState>();
                foreach (var f in DeployedFighters())
                {
                    var frb = f.GetComponent<Rigidbody2D>();
                    if (frb == null) continue;
                    comps.Add(new CompanionState
                    {
                        Id = f.unitData?.guid ?? f.name,
                        ShipId = f.unitData?.name ?? "",
                        X = frb.position.x / scale, Y = frb.position.y / scale,
                        Rot = frb.rotation,
                        Vx = frb.linearVelocity.x / scale, Vy = frb.linearVelocity.y / scale,
                    });
                }
                Send(MsgTypes.ShipState, new ShipStateMessage
                {
                    SystemId = _clusterSystem,
                    ShipId = CurrentShipId(),
                    X = pos.x / scale, Y = pos.y / scale,
                    Rot = rb.rotation,
                    Vx = vel.x / scale, Vy = vel.y / scale,
                    Seq = ++_sendSeq,
                    Companions = comps,
                }, reliable: false);
                if (comps.Count != _lastCompanionCount)
                {
                    _lastCompanionCount = comps.Count;
                    Log($"shipstate: {comps.Count} deployed fighters");
                }
            }
        }
    }

    private uint _sendSeq;
    private int _lastCompanionCount = -1;
    private string _lastFaction = "";

    /// <summary>Ship class identity (unitData.name = definition display name). Sent so
    /// remote mirrors can pick matching visuals — and re-clone when the ship is
    /// swapped, which is authoritative state, not animation.</summary>
    private string CurrentShipId()
    {
        var ship = GameplayManager.Instance?.spaceShip;
        return ship?.unitData?.name ?? "";
    }

    /// <summary>Deployed hangar fighters of the local carrier (if any).</summary>
    private static List<SpaceShip> DeployedFighters()
    {
        var list = new List<SpaceShip>();
        var ship = GameplayManager.Instance?.spaceShip;
        if (ship == null) return list;
        foreach (var bay in ship.GetComponentsInChildren<HangarBayModule>(true))
            foreach (var f in bay.GetDeployedFighters()) list.Add(f);
        return list;
    }

    /// <summary>Live fighter with the given unit guid (source for companion mirrors).</summary>
    private static SpaceShip? FindLiveFighter(string guid)
        => DeployedFighters().FirstOrDefault(f => f.unitData?.guid == guid);

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
                MmoPlugin.Instance?.World.SetLayout(w?.World, w?.LayoutHash ?? "");
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
        _lastFaction = rs.Faction;
        _remoteState[rs.SessionId] = new RemotePose
        {
            X = rs.X, Y = rs.Y, Rot = rs.Rot, Vx = rs.Vx, Vy = rs.Vy,
            TsMs = Environment.TickCount,
        };
        SyncMarker(rs.SessionId, rs.Name, rs.FullName, rs.Title, rs.TitleColor, rs.ShipId, null,
            rs.X, rs.Y, rs.Rot, rs.Vx, rs.Vy);
        foreach (var c in rs.Companions ?? new List<CompanionState>())
            SyncMarker(rs.SessionId + "#" + c.Id, null, null, null, null, c.ShipId,
                FindLiveFighter(c.Id)?.transform,
                c.X, c.Y, c.Rot, c.Vx, c.Vy);
    }

    /// <summary>Latest relayed pose per remote session (layout-local units).
    /// Movement is re-executed in TickReplicas at physics cadence; the pose here
    /// is the remote's trajectory, not a target to be painted onto the replica.</summary>
    private struct RemotePose { public float X, Y, Rot, Vx, Vy; public long TsMs; }
    private readonly Dictionary<string, RemotePose> _remoteState = new();

    // Trajectory-follow tuning (world units; 1 layout unit = mapToLocalConversion = 250):
    private const float LookAhead = 0.8f;       // s — target = remotePos + vel * look
    private const float LookAheadRetarget = 1.6f; // s — wider net while the replica is behind
    private const float LookAheadStale = 2.5f;    // s — keep the last trajectory during a gap
    private const float SoftError = 120f;         // world u — below this the pursuit converges on its own
    private const float HardError = 1500f;        // world u — above this: pose snap + reseed
    private const long StaleMs = 2000;            // no fresh pose for this long = treat as a gap

    /// <summary>Physics-cadence driver: every FixedUpdate, steer each real-unit
    /// replica along the remote's trajectory via the vanilla wingman API. The
    /// ShipNavigator/ApplyThrust pipeline turns that into real motion + animations;
    /// corrections are soft (wider look-ahead) -&gt; retarget -&gt; rare hard snap.</summary>
    private void FixedUpdate()
    {
        TickStaleReplicas();
        if (_remoteState.Count == 0 || _remoteUnits.Count == 0) return;
        foreach (var kv in _remoteUnits)
        {
            if (!_remoteState.TryGetValue(kv.Key, out var p)) continue;
            var s = MapPointOfInterest.mapToLocalConversion;
            var remote = new Vector2(p.X, p.Y) * s;
            var rvel = new Vector2(p.Vx, p.Vy) * s;
            if (kv.Value.Unit == null) continue;
            var local = kv.Value.Unit.rigidbody.position;
            var err = (local - remote).magnitude;
            var fresh = Environment.TickCount - p.TsMs <= StaleMs;
            if (fresh && err >= HardError)
            {
                RemoteUnitSpawner.HardSnap(kv.Value, remote.x, remote.y, p.Rot, rvel.x, rvel.y);
                Log($"replica: hard snap {kv.Value.Unit.name} err={err:F0}");
            }
            else
            {
                var look = fresh ? (err > SoftError ? LookAheadRetarget : LookAhead) : LookAheadStale;
                RemoteUnitSpawner.SetTarget(kv.Value,
                    remote.x + rvel.x * look, remote.y + rvel.y * look, rvel.x, rvel.y);
            }
            CounterLabel(kv.Key, kv.Value.Unit.transform);
            ThrottledTargetLog(kv.Key, kv.Value, err);
        }
    }

    // R1 (audit): a departed remote sends no packet (the protocol has no
    // session-left type), so without this a physics-driven replica would hover
    // forever. 10 s grace covers packet jitter; then despawn + clean the label.
    private const long StaleDespawnMs = 10000;
    private void TickStaleReplicas()
    {
        if (_remoteUnits.Count == 0) return;
        var now = Environment.TickCount;
        foreach (var key in _remoteUnits.Keys.ToList())
        {
            if (!_remoteState.TryGetValue(key, out var p) || now - p.TsMs > StaleDespawnMs)
            {
                if (_remoteUnits.TryGetValue(key, out var h)) RemoteUnitSpawner.Despawn(h);
                _remoteUnits.Remove(key);
                _remoteState.Remove(key);
                RemoveLabel(key);
                Log("replica: despawn " + key + " (silent)");
            }
        }
    }

    private readonly Dictionary<string, long> _targetLogAt = new();
    private void ThrottledTargetLog(string key, RemoteUnitSpawner.Handle h, float err)
    {
        if (Environment.TickCount - _targetLogAt.GetValueOrDefault(key) < 1000) return;
        _targetLogAt[key] = Environment.TickCount;
        Log($"replica: target {h.Unit.name} err={err:F0}");
    }

    private void SyncMarker(string key, string? name, string? fullName, string? title,
        string? titleColor, string shipId, Transform? liveSrc, float x, float y, float rot, float vx, float vy)
    {
        // Real-unit path: a spawned game ship is driven by the relayed state; the
        // renderer clone path below only runs when no unit could be spawned.
        if (_remoteUnits.TryGetValue(key, out var rh))
        {
            if (_markerShip.GetValueOrDefault(key) != shipId)
            {
                RemoteUnitSpawner.Despawn(rh);
                _remoteUnits.Remove(key);
                _remoteState.Remove(key);
                RemoveLabel(key);
            }
            else
            {
                CounterLabel(key, rh.Unit.transform);
                return;
            }
        }
        if (!_markers.TryGetValue(key, out var marker) || marker == null
            || _markerShip.GetValueOrDefault(key) != shipId)
        {
            if (marker != null)
            {
                Destroy(marker);
                _markers.Remove(key);
                _markerShip.Remove(key);
                if (_markerLabels.Remove(key, out var old)) Destroy(old.gameObject);
            }
            RemoveLabel(key);
            CreateRemoteMarker(key, name, fullName, title, titleColor, shipId, liveSrc, x, y);
        }
        marker = _markers.GetValueOrDefault(key);
        if (marker == null) return;
        // Empty clone (built while the ship was docked/hidden): rebuild the moment
        // the ship's visuals become active (flying). At most once per 1.5s.
        if (_markerCount.GetValueOrDefault(key) == 0 && Time.time - _rebuildT.GetValueOrDefault(key) > 1.5f)
        {
            _rebuildT[key] = Time.time;
            var live = liveSrc ?? (shipId.Length == 0 || shipId == CurrentShipId() ? LocalShip()?.transform : null);
            if (HasVisibleRenderer(live))
            {
                Destroy(marker);
                if (_markerLabels.Remove(key, out var old)) Destroy(old.gameObject);
                _markers.Remove(key);
                _markerShip.Remove(key);
                CreateRemoteMarker(key, name, fullName, title, titleColor, shipId, liveSrc, x, y);
                marker = _markers.GetValueOrDefault(key);
                if (marker == null) return;
            }
        }
        var renderer = marker.GetComponent<Renderer>();
        if (renderer != null) renderer.material.color = ColorFor(name ?? key);
        // rs position is in server layout frame; game world = local * mapToLocalConversion.
        var scale = MapPointOfInterest.mapToLocalConversion;
        marker.transform.position = new Vector3(x * scale, y * scale, 0f);
        // Direction: rot is Rigidbody2D degrees (0 = +X forward).
        marker.transform.rotation = Quaternion.Euler(0f, 0f, rot);
        // Label rides the marker but stays world-up (the marker rotates with the ship
        // and carries the ship scale, so offset must be inverse-rotated each tick).
        if (_markerLabels.TryGetValue(key, out var label))
        {
            var inv = Quaternion.Inverse(marker.transform.rotation);
            label.transform.localRotation = inv;
            label.transform.localPosition = inv * (_markerLabelOff.TryGetValue(key, out var off) ? off : new Vector3(0f, scale * 0.15f, 0f));
        }
        // Animations are DERIVED from the transferred doing (velocity), never
        // replicated as data: the protocol carries authoritative state only.
        var speed = Mathf.Sqrt(vx * vx + vy * vy);
        var flameChild = marker.transform.Find("flame");
        if (flameChild != null)
        {
            var on = speed > 0.15f;
            flameChild.gameObject.SetActive(on);
            if (on)
            {
                var k = Mathf.Clamp(speed / 8f, 0.35f, 1.6f);
                flameChild.localScale = flameChild.GetComponent<FlameSize>()?.Base * k
                    ?? flameChild.localScale;
            }
        }
    }

    /// <summary>Remembers the flame's base scale so the speed pulse multiplies it.</summary>
    private sealed class FlameSize : MonoBehaviour { public Vector3 Base; }

    private void CreateRemoteMarker(string key, string? name, string? fullName, string? title,
        string? titleColor, string shipId, Transform? liveSrc, float x, float y)
    {
        var display = fullName ?? name ?? "?";
        if (_markers.Count < 12 || !_markerShip.ContainsKey(key))
            Log($"remote player {display} appeared @ ({x:F0},{y:F0}) ship '{shipId}'");
        try
        {
            var u = MapPointOfInterest.mapToLocalConversion;   // 250
            // Preferred: a REAL game unit (hover tooltip / UI like native ships). The
            // renderer clone is only the fallback (unknown hull or no current POI).
            if (name != null && fullName != null)
            {
                var h = RemoteUnitSpawner.TrySpawn(shipId, _lastFaction, fullName, name, title, titleColor, x, y);
                if (h != null)
                {
                    _remoteUnits[key] = h;
                    _markerShip[key] = shipId;
                    var off = new Vector3(0f, u * 0.25f, 0f);
                    _markerLabels[key] = BuildLabelRoot(h.Unit.gameObject, name, fullName, title, titleColor, off);
                    _markerLabelOff[key] = off;
                    Log($"replica: real unit for {display} (ship '{shipId}')");
                    return;
                }
            }
            var marker = BuildShipVisual(shipId, liveSrc, out var halfLen, out var cloned);
            if (marker == null)
            {
                marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                var col = marker.GetComponent<Collider>();
                if (col != null) Destroy(col);
                marker.transform.localScale = new Vector3(u * 0.3f, u * 0.14f, u * 0.14f);
                halfLen = u * 0.15f;
            }
            _markerCount[key] = cloned;
            if (marker.transform.Find("flame") == null) AttachFlame(marker, halfLen);
            marker.transform.SetParent(transform, false);
            if (name != null && fullName != null)
            {
                var off = new Vector3(0f, Mathf.Max(halfLen * 0.45f, u * 0.05f), 0f);
                _markerLabels[key] = BuildLabelRoot(marker, name, fullName, title, titleColor, off);
                _markerLabelOff[key] = off;
            }
            _markers[key] = marker;
            _markerShip[key] = shipId;
        }
        catch (System.Exception e) { Log("marker: " + e.Message); }
    }

    /// <summary>Title-colored line above the name (character-menu style), parented to
    /// the host (marker or spawned unit); counter-rotated per tick by the caller.</summary>
    private GameObject BuildLabelRoot(GameObject host, string name, string fullName,
        string? title, string? titleColor, Vector3 off)
    {
        var root = new GameObject("labelRoot");
        root.transform.SetParent(host.transform, false);
        root.transform.localPosition = off;
        if (!string.IsNullOrEmpty(title))
        {
            var t = new GameObject("title").AddComponent<TextMesh>();
            t.text = title;
            t.characterSize = 0.3f;
            t.fontSize = 32;
            if (!(titleColor != null && ColorUtility.TryParseHtmlString(titleColor, out var tc))) tc = Color.white;
            t.color = tc;
            t.transform.SetParent(root.transform, false);
            t.transform.localPosition = new Vector3(0f, 0.09f, 0f);
        }
        var n = new GameObject("name").AddComponent<TextMesh>();
        n.text = fullName.Length > 0 ? fullName : name;
        n.characterSize = 0.3f;
        n.fontSize = 32;
        n.color = new Color(0.91f, 0.93f, 0.98f);
        n.transform.SetParent(root.transform, false);
        return root;
    }

    private readonly Dictionary<string, float> _rebuildT = new();
    private readonly Dictionary<string, int> _markerCount = new();

    private void AttachFlame(GameObject marker, float halfLen)
    {
        var flame = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        var fc = flame.GetComponent<Collider>();
        if (fc != null) Destroy(fc);
        flame.name = "flame";
        flame.transform.SetParent(marker.transform, false);
        flame.transform.localPosition = new Vector3(-halfLen * 1.1f, 0f, 0f);
        var s = new Vector3(halfLen * 0.22f, halfLen * 0.1f, halfLen * 0.1f);
        flame.transform.localScale = s;
        flame.AddComponent<FlameSize>().Base = s;
        flame.GetComponent<Renderer>().material.color = new Color(1f, 0.55f, 0.1f);
        flame.SetActive(false);
    }

    /// <summary>Builds a 1:1 visual copy of the ship identified by `shipId` — same sprites,
    /// same relative layout — so the mirror looks like the actual ship the pilot flies
    /// (including after a ship swap). Sources: the local ship when the class matches,
    /// else the fleet's ship data definition; null when neither matches.</summary>
    private GameObject? BuildShipVisual(string shipId, Transform? liveSrc, out float halfLen, out int cloned)
    {
        halfLen = 0f; cloned = 0;
        var root = new GameObject("mirror-ship");
        // Prefer an ACTIVE live source (the hull only renders while flying; while
        // docked every renderer on the ship is inactive). Fall back to the ship
        // definition, then any live transform (companions are usually active).
        Transform? src = null;
        if (liveSrc != null && HasVisibleRenderer(liveSrc)) src = liveSrc;
        var local = LocalShip();
        var localId = CurrentShipId();
        if (src == null && (shipId.Length == 0 || shipId == localId) && local != null
            && HasVisibleRenderer(local.transform)) src = local.transform;
        if (src == null)
        {
            var def = GamePlayer.current?.spaceShips?.FirstOrDefault(d => d.name == shipId)?.unitDefinition;
            if (def != null && HasVisibleRenderer(def.transform)) src = def.transform;
        }
        if (src == null)
        {
            if (liveSrc != null) src = liveSrc;   // inactive but better than nothing
            else if (shipId.Length == 0 || shipId == localId) src = local?.transform;
        }
        if (src == null) { Destroy(root); return null; }
        root.transform.localScale = src.lossyScale;
        var (n, hl) = CloneRenderers(root.transform, src, src);
        halfLen = hl; cloned = n;
        Log($"mirror: ship visual '{shipId}' — {n} renderers from '{src.name}'");
        return root;
    }

    private static bool HasVisibleRenderer(Transform t)
        => t != null && t.GetComponentsInChildren<Renderer>(true).Any(r => r.gameObject.activeInHierarchy);

    private static (int count, float halfLen) CloneRenderers(Transform dstRoot, Transform srcRoot, Transform relativeTo)
    {
        var count = 0;
        var halfLen = 0f;
        var relScale = relativeTo.lossyScale;
        var invRelRot = Quaternion.Inverse(relativeTo.rotation);
        foreach (var sr in srcRoot.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (sr.sprite == null) continue;
            // Hardpoint socket indicators (SpaceShipHardpoint) are UI-only: colored
            // module-size squares that are SpriteRenderer.enabled=false in flight and
            // appear only on hover in the hangar UI. The game excludes them from ship
            // renderer toggles (SetRenderersVisible) — we exclude them from the clone.
            if (sr.GetComponent<SpaceShipHardpoint>() != null) continue;
            if (!sr.gameObject.activeInHierarchy) continue;
            var go = new GameObject(sr.name);
            go.transform.SetParent(dstRoot, false);
            go.transform.localPosition = relativeTo.InverseTransformPoint(sr.transform.position);
            go.transform.localRotation = invRelRot * sr.transform.rotation;
            var ls = sr.transform.lossyScale;
            go.transform.localScale = new Vector3(ls.x / relScale.x, ls.y / relScale.y, ls.z / relScale.z);
            var r = go.AddComponent<SpriteRenderer>();
            r.sprite = sr.sprite;
            r.color = sr.color;
            r.enabled = sr.enabled;   // some parts are renderer-disabled (not GO-inactive)
            r.flipX = sr.flipX; r.flipY = sr.flipY;
            r.sortingLayerID = sr.sortingLayerID;
            r.sortingOrder = sr.sortingOrder;
            r.material = sr.material;
            r.drawMode = sr.drawMode;
            r.size = sr.size;
            halfLen = Mathf.Max(halfLen, Mathf.Abs(go.transform.localPosition.x) + r.size.x * go.transform.localScale.x * 0.5f);
            count++;
        }
        // The hull is often MeshRenderers (not sprites) — clone those too.
        foreach (var mr in srcRoot.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (!mr.gameObject.activeInHierarchy) continue;
            var mf = mr.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;
            var go = new GameObject(mr.name);
            go.transform.SetParent(dstRoot, false);
            go.transform.localPosition = relativeTo.InverseTransformPoint(mr.transform.position);
            go.transform.localRotation = invRelRot * mr.transform.rotation;
            var ls = mr.transform.lossyScale;
            go.transform.localScale = new Vector3(ls.x / relScale.x, ls.y / relScale.y, ls.z / relScale.z);
            var f = go.AddComponent<MeshFilter>();
            f.sharedMesh = mf.sharedMesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mr.sharedMaterial;
            r.enabled = mr.enabled;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            halfLen = Mathf.Max(halfLen, Mathf.Abs(go.transform.localPosition.x) + mf.sharedMesh.bounds.extents.x * go.transform.localScale.x);
            count++;
        }
        return (count, halfLen);
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
