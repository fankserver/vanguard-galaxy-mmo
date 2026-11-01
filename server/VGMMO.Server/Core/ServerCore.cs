using LiteNetLib;
using Newtonsoft.Json.Linq;
using VGMMO.Protocol;
using VGMMO.Server.Content;
using VGMMO.Server.Ledger;
using VGMMO.Server.Persistence;
using VGMMO.Server.Transport;

namespace VGMMO.Server.Core;

public sealed class ServerCore
{
    public sealed class Session
    {
        public string Id = "";
        public int PeerId;
        public string Name = "";            // nickname (MMO handle)
        public string FullName = "";        // captain GetFullName() from the save
        public string Title = "";           // selected title display name
        public string TitleColor = "";      // hex color of the title
        public string GameVersion = "";
        public string BuildHash = "";
        public string WorldMode = "cluster";
        public bool HelloReceived;
        public string? SystemId;
        public long LastActivityMs;
        public long ConnectedAtMs;
        public long PingMs;
        public Protocol.ShipStateMessage? LastPos;
    }

    private readonly Dictionary<int, Session> _byPeer = new();
    private readonly Dictionary<string, Session> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorldDefinition> _worlds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<int>> _groups = new(StringComparer.Ordinal); // "world/system" -> peerIds
    private readonly Dictionary<string, uint> _eventSeq = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _worldStartMs = new(StringComparer.Ordinal);
    private readonly List<GhostPilot> _ghosts = new();
    private long _lastGhostMs;
    private long _lastSaveMs;
    private readonly object _gate = new();

    public IReadOnlyList<GhostPilot> Ghosts => _ghosts;

    public ServerConfig Config { get; }
    public LedgerStore Ledger { get; } = new();
    public ActivityLog Activity { get; } = new();
    public EventPlanner Planner { get; }
    public StateStore State { get; }
    public GameServer? Transport { get; private set; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;

    public ServerCore(ServerConfig config)
    {
        Config = config;
        State = new StateStore(config.DataDir);
        Planner = new EventPlanner(this);
        Ledger.Changed += _ => MarkDirty();
    }

    public void AttachTransport(GameServer transport) => Transport = transport;

    // ---------- startup ----------

    public void Init()
    {
        foreach (var world in WorldLoader.LoadAll(Config.WorldsDir))
        {
            _worlds[world.Id] = world;
            _worldStartMs[world.Id] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _eventSeq[world.Id] = 0;
            Log($"world '{world.Id}' ({world.Name}): {world.Systems.Count} systems, {world.Timeline.Count} timeline entries, hash {world.ComputeLayoutHash()[..12]}");
        }
        var snap = State.Load();
        if (snap != null)
        {
            Ledger.Restore(snap.Ledger);
            Planner.Restore(snap.Planner);
            Log($"restored state: {Ledger.Loot.Count} loot, {Ledger.Cargo.Count} cargo owners");
        }
        if (_worlds.Count == 0) Log("WARNING: no worlds found in " + Config.WorldsDir);
    }

    private bool _dirty;

    public void MarkDirty() => _dirty = true;

    public bool SaveState()
    {
        lock (_gate)
        {
            try
            {
                State.Save(new StateStore.WorldState
                {
                    Ledger = Ledger.Capture(),
                    Planner = Planner.Capture(),
                });
                _dirty = false;
                return true;
            }
            catch (Exception e)
            {
                LogError("save failed: " + e.Message);
                return false;
            }
        }
    }

    // ---------- peer lifecycle ----------

    public void OnPeerConnected(NetPeer peer)
    {
        lock (_gate)
        {
            Transport?.TrackPeer(peer);
            _byPeer[peer.Id] = new Session
            {
                Id = Guid.NewGuid().ToString("N")[..8], PeerId = peer.Id,
                ConnectedAtMs = NowMs(), LastActivityMs = NowMs(),
            };
            Log($"peer connected: {peer}");
        }
    }

    public void OnPeerDisconnected(NetPeer peer)
    {
        lock (_gate)
        {
            Transport?.UntrackPeer(peer);
            if (!_byPeer.Remove(peer.Id, out var s)) return;
            _byId.Remove(s.Id);
            if (s.HelloReceived)
            {
                if (s.SystemId != null) LeaveGroup(s, "disconnect");
                Activity.Add("leave", $"{s.Name} disconnected", s.SystemId ?? "");
                Log($"{s.Name} disconnected");
            }
            else Log($"pending peer disconnected: {peer}");
            _dirty = true;
        }
    }

    public void OnFrame(NetPeer peer, Envelope env)
    {
        if (peer == null) return;
        lock (_gate)
        {
            if (!_byPeer.TryGetValue(peer.Id, out var session)) return;
            session.LastActivityMs = NowMs();
            try { Dispatch(peer, session, env); }
            catch (Exception e) { LogError($"dispatch {env.Type}: {e.Message}"); }
        }
    }

    // ---------- dispatch ----------

    private void Dispatch(NetPeer peer, Session session, Envelope env)
    {
        // Nothing before a proper Hello: join/state/loot channels must not run for
        // a peer that skipped the handshake (build-hash + capacity gates live there).
        if (!session.HelloReceived && env.Type != MsgTypes.Hello)
        {
            RefuseAndDrop(peer, "say hello first");
            return;
        }
        switch (env.Type)
        {
            case MsgTypes.Hello:
                HandleHello(peer, session, env);
                break;
            case MsgTypes.Ping:
                Transport?.Send(peer, MsgTypes.Pong, new PingMessage { T = NowMs() });
                break;
            case MsgTypes.JoinWorld:
                HandleJoin(session, env);
                break;
            case MsgTypes.LeaveWorld:
                if (session.SystemId != null) LeaveGroup(session, "leave");
                Transport?.Send(peer, MsgTypes.SystemLeft, new SystemLeftMessage { SystemId = session.SystemId ?? "" });
                session.SystemId = null;
                break;
            case MsgTypes.LeaveUniverse:
                // Flush-before-restore (contract §2 leave): the ledger/persistence
                // must be durable before the client touches local state, else a crash
                // after restore loses the final trade.
                SaveState();
                Transport?.Send(peer, MsgTypes.FlushAck, new FlushAckMessage());
                Log($"{session.Name} leaving universe (flushed)");
                break;
            case MsgTypes.ShipState:
                HandleShipState(peer, session, env);
                break;
            case MsgTypes.EntitySnapshot:
                HandleEntitySnapshot(peer, session, env);
                break;
            case MsgTypes.LootSpawn:
                HandleLootSpawn(peer, session, env);
                break;
            case MsgTypes.LootTaken:
                HandleLootTaken(peer, session, env);
                break;
            case MsgTypes.TradeOffer:
                HandleTradeOffer(peer, session, env);
                break;
            case MsgTypes.TradeAnswer:
                HandleTradeAnswer(peer, session, env);
                break;
            default:
                LogError($"unknown message type '{env.Type}' from {session.Name}");
                break;
        }
    }

    private void HandleHello(NetPeer peer, Session session, Envelope env)
    {
        if (session.HelloReceived) { Transport?.Send(peer, MsgTypes.Refused, new RefusedResponse { Reason = "already greeted" }); return; }
        var hello = Wire.Read<HelloRequest>(env);
        if (hello == null) { RefuseAndDrop(peer, "malformed hello"); return; }
        if (hello.Protocol != ProtocolInfo.Version) { RefuseAndDrop(peer, $"protocol mismatch (server {ProtocolInfo.Version}, client {hello.Protocol})"); return; }
        if (Config.AcceptedHashes.Count > 0 && !Config.AcceptedHashes.Contains(hello.ClientBuildHash))
        {
            Activity.Add("refuse", $"refused connect: build '{HelloTrim(hello.ClientBuildHash)}' not accepted");
            RefuseAndDrop(peer, "client build mismatch");
            return;
        }
        if (ActivePlayerCount() >= Config.MaxPlayersPerWorld * Math.Max(1, _worlds.Count))
        { RefuseAndDrop(peer, "server full"); return; }

        // Mode resolution must mirror every other refusal: BEFORE the session is
        // registered, so a refused join never occupies a player slot nor logs a
        // phantom leave on disconnect.
        var mode = string.IsNullOrEmpty(hello.Mode) ? "cluster" : hello.Mode;
        var world = _worlds.Values.FirstOrDefault(w => w.Mode == mode);
        if (world == null)
        {
            Activity.Add("refuse", $"refused {HelloTrim(hello.Name)}: no '{mode}' world hosted");
            RefuseAndDrop(peer, $"no '{mode}' world hosted");
            return;
        }

        session.HelloReceived = true;
        session.Name = SanitizeName(hello.Name);
        session.FullName = hello.FullName;
        session.Title = hello.Title;
        session.TitleColor = hello.TitleColor;
        session.GameVersion = hello.GameVersion;
        session.BuildHash = hello.ClientBuildHash;
        _byId[session.Id] = session;
        session.WorldMode = mode;
        Transport?.Send(peer, MsgTypes.Welcome, new WelcomeResponse
        {
            SessionId = session.Id,
            WorldId = world.Id,
            WorldName = world.Name,
            Mode = world.Mode,
            ServerTimeMs = NowMs(),
            LayoutHash = world.ComputeLayoutHash(),
            Systems = world.Systems.Select(s => new WelcomeSystem { Id = s.Id, StoryId = s.StoryId }).Where(s => s.StoryId.Length > 0).ToList(),
            World = ToLayout(world),
        });
        Log($"hello: {session.Name} (game {session.GameVersion}, build {HelloTrim(session.BuildHash)}) -> {session.Id}");
    }

    private void HandleJoin(Session session, Envelope env)
    {
        var join = Wire.Read<JoinWorldRequest>(env);
        if (join == null || !SystemExists(join.SystemId)) { Transport?.SendPeer(session.PeerId, MsgTypes.Refused, new RefusedResponse { Reason = "unknown system" }); return; }
        LeaveGroup(session, "resystem");
        session.SystemId = join.SystemId;
        // Identity refresh on cluster entry (captain data is loaded by then — the
        // hello was sent at connect, before a save existed).
        if (join.Name.Length > 0) session.Name = SanitizeName(join.Name);
        if (join.FullName.Length > 0 || join.Title.Length > 0)
        {
            session.FullName = join.FullName;
            session.Title = join.Title;
            session.TitleColor = join.TitleColor;
        }
        Group(join.SystemId).Add(session.PeerId);
        Transport?.SendPeer(session.PeerId, MsgTypes.SystemEntered, new SystemEnteredMessage { SystemId = join.SystemId });
        Activity.Add("join", $"{session.Name} entered {join.SystemId}", join.SystemId);
        Log($"{session.Name} joined {join.SystemId}");
        _dirty = true;
    }

    private void HandleShipState(NetPeer peer, Session session, Envelope env)
    {
        var state = Wire.Read<ShipStateMessage>(env);
        if (state == null) return;
        session.LastPos = state;
        if (session.SystemId == null || state.SystemId != session.SystemId) return;
        foreach (var target in Group(state.SystemId))
        {
            if (target == session.PeerId) continue;
            Transport?.SendPeer(target, MsgTypes.RemoteShipState, new RemoteShipState
            {
                SessionId = session.Id, Name = session.Name, SystemId = state.SystemId,
                ShipId = state.ShipId, Faction = "Player",
                X = state.X, Y = state.Y, Rot = state.Rot, Vx = state.Vx, Vy = state.Vy,
                Seq = state.Seq, Ts = NowMs(), Companions = state.Companions,
            }, reliable: false);
        }
        if (state.Companions.Count != _lastCompanionRelay)
        {
            _lastCompanionRelay = state.Companions.Count;
            Log($"relay {session.Name}: {state.Companions.Count} companions in {state.SystemId}");
        }
        RelayShadow(session, state);
    }

    private int _lastCompanionRelay = -1;

    // ---------- shadow replica (test mode) ----------

    /// <summary>Test-only clone of one in-cluster player: same ship state at a small
    /// offset, relayed to the whole system group INCLUDING the source, so rotation,
    /// velocity and (client-side) thruster visuals can be compared side by side.
    /// One shadow, tracking the most recently active in-cluster session.</summary>
    private ShadowReplica? _shadow;

    public ShadowReplica? GetShadow() => _shadow;

    public sealed class ShadowReplica
    {
        public string Name = "Shadow";
        public string FullName = "";
        public string SystemId = "";
        public ShipStateMessage State = new();
    }

    private void RelayShadow(Session session, ShipStateMessage state)
    {
        if (!Config.ShadowGhost) return;
        _shadow ??= new ShadowReplica();
        _shadow.SystemId = state.SystemId;
        _shadow.FullName = session.FullName.Length > 0 ? session.FullName + " (shadow)" : session.Name + " (shadow)";
        _shadow.State = new ShipStateMessage
        {
            SystemId = state.SystemId,
            ShipId = state.ShipId,
            X = state.X + 0.5f,     // ~125 game units — clearly beside the ship, so the
                                    // clone reads as a distinct vessel while it mirrors
            Y = state.Y,
            Rot = state.Rot, Vx = state.Vx, Vy = state.Vy, Seq = state.Seq,
            // Companions mirror too (offset keeps them aligned with the shadow).
            Companions = state.Companions?.Select(c => new CompanionState
            {
                Id = c.Id, ShipId = c.ShipId,
                X = c.X + 0.012f, Y = c.Y, Rot = c.Rot, Vx = c.Vx, Vy = c.Vy,
            }).ToList() ?? new List<CompanionState>(),
        };
        var rs = new RemoteShipState
        {
            SessionId = "shadow:" + session.Id, Name = _shadow.Name,
            FullName = _shadow.FullName, Title = "", TitleColor = "#BDBDBD",
            SystemId = _shadow.SystemId, ShipId = _shadow.State.ShipId, Faction = "Player",
            X = _shadow.State.X, Y = _shadow.State.Y, Rot = _shadow.State.Rot,
            Vx = _shadow.State.Vx, Vy = _shadow.State.Vy, Seq = _shadow.State.Seq, Ts = NowMs(),
            Companions = _shadow.State.Companions,
        };
        // Deliberately includes the source player — the whole point is to see it.
        foreach (var target in Group(state.SystemId))
            Transport?.SendPeer(target, MsgTypes.RemoteShipState, rs, reliable: false);
    }

    private void HandleEntitySnapshot(NetPeer peer, Session session, Envelope env)
    {
        var snap = Wire.Read<EntitySnapshot>(env);
        if (snap == null || snap.SystemId != session.SystemId) return;
        if (!SystemExists(snap.SystemId)) return;
        foreach (var target in Group(snap.SystemId))
        {
            if (target == session.PeerId) continue;
            Transport?.SendPeer(target, MsgTypes.EntitySnapshot, snap, reliable: false);
        }
    }

    private void HandleLootSpawn(NetPeer peer, Session session, Envelope env)
    {
        var msg = Wire.Read<LootSpawnMessage>(env);
        if (msg == null || msg.SystemId != session.SystemId) return;
        if (!SystemExists(msg.SystemId)) return;
        var entry = Ledger.Spawn(msg);
        Broadcast(msg.SystemId, MsgTypes.LootSpawn, new LootSpawnMessage
        {
            InstanceId = entry.InstanceId, SystemId = entry.SystemId, ItemId = entry.ItemId,
            Amount = entry.Amount, X = entry.X, Y = entry.Y, OwnerFaction = entry.OwnerFaction,
        });
        Activity.Add("loot", $"{session.Name} dropped {entry.Amount}x {entry.ItemId} in {entry.SystemId}", entry.SystemId);
    }

    private void HandleLootTaken(NetPeer peer, Session session, Envelope env)
    {
        var msg = Wire.Read<LootTakenMessage>(env);
        // The taker is the SESSION, never the wire: clients must not credit loot
        // to a third party. Broadcast already stamps session.Name.
        if (msg == null || !Ledger.Take(msg.InstanceId, session.Name)) return;
        BroadcastByInstance(msg.InstanceId, MsgTypes.LootGone, new LootGoneMessage { InstanceId = msg.InstanceId, Taker = session.Name });
        Activity.Add("loot", $"{session.Name} took loot {msg.InstanceId[..Math.Min(6, msg.InstanceId.Length)]}", "");
    }

    private void HandleTradeOffer(NetPeer peer, Session session, Envelope env)
    {
        var offer = Wire.Read<TradeOfferMessage>(env);
        if (offer == null || string.IsNullOrEmpty(offer.TradeId) || offer.To == session.Name) return;
        if (!_byId.Values.Any(s => s.Name.Equals(offer.To, StringComparison.OrdinalIgnoreCase)))
        { Transport?.SendPeer(session.PeerId, MsgTypes.TradeResult, new TradeResultMessage { TradeId = offer.TradeId, Ok = false, Reason = "no such player" }); return; }
        offer.From = session.Name;
        Ledger.PutPending(offer);
        foreach (var target in _byId.Values)
            if (target.Name.Equals(offer.To, StringComparison.OrdinalIgnoreCase))
                Transport?.SendPeer(target.PeerId, MsgTypes.TradeOffer, offer);
        Activity.Add("trade", $"{session.Name} offered a trade to {offer.To}");
    }

    private void HandleTradeAnswer(NetPeer peer, Session session, Envelope env)
    {
        var answer = Wire.Read<TradeAnswerMessage>(env);
        var offer = answer == null ? null : Ledger.GetPending(answer.TradeId);
        if (offer == null || !offer.To.Equals(session.Name, StringComparison.OrdinalIgnoreCase)) return;
        var (ok, reason) = Ledger.ResolveTrade(offer.TradeId, offer, answer.Accepted);
        var result = new TradeResultMessage { TradeId = offer.TradeId, Ok = ok, Reason = reason };
        foreach (var target in _byId.Values)
            if (target.Name.Equals(offer.From, StringComparison.OrdinalIgnoreCase) || target.Name.Equals(offer.To, StringComparison.OrdinalIgnoreCase))
                Transport?.SendPeer(target.PeerId, MsgTypes.TradeResult, result);
        Activity.Add("trade", ok ? $"trade {offer.TradeId} completed between {offer.From} and {offer.To}" : $"trade {offer.TradeId} failed: {reason}");
    }

    // ---------- planner hooks ----------

    public void FirePlannedEvent(WorldDefinition world, TimelineDef entry, uint seq)
    {
        Broadcast(entry.SystemId, MsgTypes.Event, new EventMessage
        {
            Seq = seq, SystemId = entry.SystemId, Command = entry.Command,
            Args = entry.Args == null ? null : JObject.FromObject(entry.Args),
        });
        Log($"planned event '{entry.Command}' fired in {entry.SystemId} (seq {seq})");
        Activity.Add("event", $"world {world.Id}: '{entry.Command}' in {entry.SystemId}", entry.SystemId);
        MarkDirty();
    }

    public WorldDefinition? WorldBySystem(string systemId)
        => _worlds.Values.FirstOrDefault(w => w.Systems.Any(s => s.Id == systemId));

    public bool SystemExists(string systemId) => WorldBySystem(systemId) != null;

    // ---------- tick ----------

    public void Tick()
    {
        var now = NowMs();
        lock (_gate)
        {
            // hello deadline + idle timeout
            foreach (var s in _byPeer.Values.ToList())
            {
                if (!s.HelloReceived && now - s.ConnectedAtMs > Config.HelloTimeoutMs) Drop(s, "hello timeout");
                else if (s.HelloReceived && now - s.LastActivityMs > 30000) Drop(s, "idle timeout");
            }
            TickGhosts(now);
            Planner.Tick();
            if (_dirty && now - _lastSaveMs > 60000) { _lastSaveMs = now; SaveState(); }
        }
    }

    // ---------- simulated pilots ----------

    private void InitGhosts()
    {
        var world = _worlds.Values.FirstOrDefault();
        if (world == null || world.Systems.Count == 0) return;
        var names = new[] { "Vega", "Orion" };
        var fulls = new[] { "Vega Sable", "Orion Vale" };
        for (var i = 0; i < Config.GhostPilots; i++)
        {
            var sys = world.Systems[i % world.Systems.Count];
            // Anchor AT the POI in the SYSTEM-LOCAL frame (same frame the client uses:
            // ship state = rb.position / mapToLocalConversion, web draws system origin
            // + local). Absolute coords would double-offset every system with a nonzero
            // origin -- and the in-game scale is 250 game units per layout unit, so a
            // wide orbit is hundreds of units off the POI.
            var poi = sys.Pois.Count > 0 ? sys.Pois[i % sys.Pois.Count] : null;
            var cx = poi?.X ?? 0f;
            var cy = poi?.Y ?? 0f;
            var g = new GhostPilot("g" + (i + 1), names[i % names.Length], fulls[i % fulls.Length], "Hauler",
                sys.Id, cx, cy, 0.4f, 0.4f, 0.15f);
            _ghosts.Add(g);
            Activity.Add("join", $"{g.Name} entered {sys.Id}", sys.Id);
            Log($"ghost pilot {g.Name} hovering {sys.Id} @ ({cx:F0},{cy:F0})");
        }
    }

    private void TickGhosts(long now)
    {
        if (Config.GhostPilots <= 0) return;
        if (_ghosts.Count == 0) InitGhosts();
        var dt = _lastGhostMs == 0 ? 0.1f : Math.Clamp((now - _lastGhostMs) / 1000f, 0.02f, 0.5f);
        _lastGhostMs = now;
        foreach (var g in _ghosts)
        {
            g.Tick(dt);
            foreach (var pid in Group(g.SystemId))
            {
                Transport?.SendPeer(pid, MsgTypes.RemoteShipState, new RemoteShipState
                {
                    SessionId = "ghost:" + g.Id, Name = g.Name, FullName = g.FullName,
                    Title = g.Title, TitleColor = g.TitleColor, SystemId = g.SystemId, Faction = "Player",
                    X = g.State.X, Y = g.State.Y, Rot = g.State.Rot, Vx = g.State.Vx, Vy = g.State.Vy,
                    Seq = g.State.Seq, Ts = now,
                }, reliable: false);
            }
        }
    }

    private void Drop(Session s, string reason)
    {
        Log($"dropping {s.Name} ({reason})");
        if (s.SystemId != null) LeaveGroup(s, "drop");
        // Record the disappearance here — the peer-disconnect callback can't (the
        // session is gone by then).
        Activity.Add("leave", $"{s.Name} disconnected ({reason})", s.SystemId ?? "");
        _byPeer.Remove(s.PeerId);
        _byId.Remove(s.Id);
        _dirty = true;
        Transport?.DisconnectPeer(s.PeerId);
    }

    private void LeaveGroup(Session s, string reason)
    {
        if (s.SystemId == null) return;
        var key = GroupKey(s.SystemId);
        if (_groups.TryGetValue(key, out var set))
        {
            set.Remove(s.PeerId);
            if (set.Count == 0) _groups.Remove(key);
        }
        Activity.Add("leave", $"{s.Name} left {s.SystemId} ({reason})", s.SystemId);
        s.SystemId = null;
    }

    private HashSet<int> Group(string systemId)
    {
        var key = GroupKey(systemId);
        if (!_groups.TryGetValue(key, out var set)) _groups[key] = set = new HashSet<int>();
        return set;
    }

    private static string GroupKey(string systemId) => systemId; // ids are globally unique per content rule

    private void Broadcast(string systemId, string type, object msg, bool reliable = true)
    {
        foreach (var pid in Group(systemId)) Transport?.SendPeer(pid, type, msg, reliable);
    }

    private void BroadcastByInstance(string instanceId, string type, object msg)
    {
        var loot = Ledger.Loot.GetValueOrDefault(instanceId);
        Broadcast(loot?.SystemId ?? "", type, msg);
    }

    // ---------- queries (web) ----------

    public int ActivePlayerCount()
    {
        // "Available" = in the MMO cluster, not merely connected.
        lock (_gate) return _byId.Values.Count(s => s.SystemId != null);
    }

    public int ConnectedCount()
    {
        lock (_gate) return _byId.Count;
    }

    private static WorldLayout ToLayout(WorldDefinition world) => new()
    {
        Id = world.Id,
        Name = world.Name,
        Systems = world.Systems.Select(s => new LayoutSystem
        {
            Id = s.Id, StoryId = s.StoryId, Name = s.Name, X = s.X, Y = s.Y, Level = s.Level, Faction = s.Faction,
            Pois = s.Pois.Select(p => new LayoutPoi { Id = p.Id, Kind = p.Kind, Name = p.Name, X = p.X, Y = p.Y, Faction = p.Faction, Level = p.Level, Facilities = p.Facilities }).ToList(),
        }).ToList(),
        Links = world.Links.Select(l =>
        {
            var a = world.Systems.FirstOrDefault(s => s.Id == l.A);
            var b = world.Systems.FirstOrDefault(s => s.Id == l.B);
            var pa = GatePos(a, b);   // gate on a, facing b
            var pb = GatePos(b, a);   // gate on b, facing a
            return new LayoutLink
            {
                A = l.A, B = l.B, Name = "MMO Gate", SectorGate = false,
                PosAX = pa.x, PosAY = pa.y, PosBX = pb.x, PosBY = pb.y,
            };
        }).ToList(),
        Entry = world.Entry == null ? null : new LayoutEntry
        {
            TargetSystemId = world.Entry.TargetSystemId,
            Name = world.Entry.Name, SectorGate = world.Entry.SectorGate, Unlocked = world.Entry.Unlocked,
            HubX = world.Entry.HubX, HubY = world.Entry.HubY,
        },
    };

    /// <summary>Gate POI LOCAL position (offset from the system center toward the peer,
    /// clamped to the vanilla POI band). POI coordinates are system-local everywhere —
    /// never absolute layout coords.</summary>
    private static (float x, float y) GatePos(SystemDef from, SystemDef? to)
    {
        if (to == null) return (0f, 0f);
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.001f) { dx = 1f; dy = 0f; len = 1f; }
        var x = Math.Clamp(dx / len * 12f, -19f, 19f);
        var y = Math.Clamp(dy / len * 12f, -4.5f, 4.5f);
        return (x, y);
    }

    public void Kick(string sessionId)
    {
        lock (_gate)
        {
            if (_byId.Remove(sessionId, out var s)) Drop(s, "kicked by admin");
        }
    }

    public List<Session> Sessions()
    {
        lock (_gate) return _byId.Values.ToList();
    }

    public IReadOnlyDictionary<string, WorldDefinition> Worlds() => _worlds;

    public Dictionary<string, List<string>> GroupMembers()
    {
        // systemId -> player names (for web)
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var s in _byId.Values)
                if (s.SystemId != null)
                    (result.TryGetValue(s.SystemId, out var l) ? l : result[s.SystemId] = l = new List<string>()).Add(s.Name);
        }
        return result;
    }

    // ---------- helpers ----------

    private void RefuseAndDrop(NetPeer peer, string reason)
    {
        Transport?.Send(peer, MsgTypes.Refused, new RefusedResponse { Reason = reason });
        Log($"refused {peer}: {reason}");
        // Give the refused frame a moment to flush before disconnecting; otherwise the
        // client sees disconnect before Refused and shows a generic error.
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(250); Transport?.DisconnectPeer(peer.Id); }
            catch { /* peer already gone */ }
        });
    }

    private static string HelloTrim(string s) => s.Length > 16 ? s[..16] + "…" : s;

    private static string SanitizeName(string raw)
    {
        // No fabricated placeholder names: the captain callsign arrives with the
        // first JoinWorld (save loaded); until then the name is simply empty.
        var s = raw ?? "";
        s = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length > 24 ? s[..24] : s;
    }

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
    public void LogError(string msg) => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] ERROR {msg}");
}
