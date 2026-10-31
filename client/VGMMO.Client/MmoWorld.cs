using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Source.Galaxy;
using Source.Galaxy.POI;
using Source.Player;
using Source.Simulation.World;
using UnityEngine;
using VGMMO.Protocol;

namespace VGMMO.Client;

/// <summary>
/// M1: materializes the server's authored world layout inside the game — a dedicated
/// sector holding the hosted systems, their POIs and paired jump gates (PocketWorlds
/// pattern, current-build seams). Idempotent: rebuilds on reconnect, reconciles stale
/// copies leaked by a crash, and tears down safely once nobody is inside (deferred if
/// the player is still in the cluster when the connection drops).
/// </summary>
public sealed class MmoWorldBuilder : MonoBehaviour
{
    /// <summary>Where the cluster sector goes: recomputed at build time to the spot with
    /// the most clearance from every existing vanilla sector (GalaxyLayout allocates
    /// quadrant frames + content rects; we must not sit inside a populated one).</summary>
    private static readonly Vector2 FallbackOrigin = new(120f, 4f);
    private static readonly float MinClusterClearance = 350f;

    private WorldLayout? _layout;
    private bool _built;
    private bool _teardownPending;

    private SectorMapData? _sector;
    private readonly List<SystemMapData> _systems = new();
    private readonly List<(SystemMapData system, MapPointOfInterest poi)> _entryGates = new();

    /// <summary>The world instance the cluster was built in. Loading a new save replaces
    /// GamePlayer.current.map with a fresh instance and destroys the cluster; a
    /// mismatch means the cluster must be rebuilt from the layout.</summary>
    private GalaxyMapData? _mapRef;

    private static readonly MethodInfo CreateSectorMethod = typeof(SandboxWorld)
        .GetMethod("CreateSector", BindingFlags.NonPublic | BindingFlags.Static)!;

    public void SetLayout(WorldLayout? layout)
    {
        _layout = layout;
        _built = false;
        _systems.Clear();
        _sector = null;
        _mapRef = null;
    }

    public bool IsClusterSystem(string storyId)
        => _layout != null && _layout.Systems.Exists(s => s.StoryId == storyId);

    private void Update()
    {
        if (GamePlayer.current == null) return;

        if (_teardownPending)
        {
            // Do not rip the world out from under the player: wait until they are
            // outside the cluster, then remove.
            var cur = GamePlayer.current.currentSystem;
            if (cur == null || !IsClusterSystem(cur.storyId)) Teardown();
            return;
        }

        // Build only while connected to a server that owns the layout.
        if (!_built)
        {
            if (_layout == null) return;
            if (MmoPlugin.Instance == null || !MmoPlugin.Instance.Session.Connected) return;
            Build();
        }

        // Save-load recovery: a new save swaps the whole galaxy map (GamePlayer.map is
        // recreated), destroying the cluster objects. Drop the built flag so the branch
        // above rebuilds from the layout while we stay connected.
        if (_built && _mapRef != null && GamePlayer.current.map != _mapRef)
        {
            Log("world reloaded (new save) — rebuilding cluster");
            _built = false;
            _sector = null;
            _systems.Clear();
            _entryGates.Clear();
            _mapRef = null;
            return;
        }

        // The entry gate needs a real current system; at load time it can still be
        // null, so retry on later frames until the player has one.
        if (_built && _entryGates.Count == 0)
        {
            var eorigin = GamePlayer.current.currentSystem;
            if (eorigin != null) TryCreateEntry(eorigin);
        }
    }

    // ---------- build ----------

    private void Build()
    {
        try
        {
            var map = GamePlayer.current.map;
            if (map == null) return;

            // Idempotent: if the hosted systems already exist (reconnect, or the layout
            // was already materialized), just adopt them.
            if (_layout!.Systems.Any(s => s.StoryId.Length > 0 && map.GetSystemByStoryId(s.StoryId) != null))
            {
                _built = true;
                _mapRef = map;
                Log("cluster already present — adopting (" + _layout.Systems.Count + " systems)");
                return;
            }

            // Find a free spot: max clearance from every existing sector in the same
            // coordinate frame (sector positions are absolute, per the saved layout).
            // Defer when the map isn't settled yet or no spot has enough clearance —
            // never fall back to a hardcoded origin (it sits inside the vanilla grid).
            var clusterOrigin = FindClusterOrigin(map);
            if (clusterOrigin == null) return;   // retried on the next Update

            var sector = (SectorMapData)CreateSectorMethod.Invoke(null, new object[] { clusterOrigin.Value, "MMO - " + _layout.Name })!;
            sector.SetIdentifier("mmo-sector-" + _layout.Id);
            map.AddSector(sector);
            _sector = sector;

            foreach (var def in _layout.Systems)
            {
                SystemMapData system;
                try
                {
                    var faction = def.Faction.Length == 0 ? null : Faction.Get(def.Faction);
                    system = SandboxWorld.CreateEmptySystem(sector, Mathf.Max(1, def.Level), faction, new Vector2(def.X, def.Y));
                    system.SetIdentifier(def.Id);
                    system.storyId = def.StoryId; // CreateEmptySystem stamps "tutorial_system_*"; ours wins
                    system.name = def.Name;
                }
                catch (Exception e)
                {
                    Log("failed to create system " + def.Id + ": " + e.Message);
                    continue;
                }

                foreach (var p in def.Pois) AddPoi(system, p);
                // No storyteller: the game null-guards it everywhere and omits it from
                // the save; a custom one serialized its un-resolvable type name into
                // the save and broke every later load (SystemStoryteller.Create NRE).
                _systems.Add(system);
            }

            foreach (var link in _layout.Links)
            {
                var a = _systems.Find(s => s.storyId == link.A || s.guid == link.A);
                var b = _systems.Find(s => s.storyId == link.B || s.guid == link.B);
                if (a == null || b == null) continue;
                try
                {
                    CreateGate(a, b,
                        new Vector2(link.PosAX, link.PosAY), new Vector2(link.PosBX, link.PosBY),
                        link.Name, link.SectorGate, unlocked: true);
                }
                catch (Exception e) { Log("gate " + link.A + "<->" + link.B + ": " + e.Message); }
            }

            // Entry: a gate from the player's current vanilla system into the hub, so the
            // cluster is reachable (PocketWorlds-style door). Tracked for teardown. The
            // door spec (target, name, sectorGate, hub-side position) comes from the server.
            var origin = GamePlayer.current.currentSystem;
            if (origin != null) TryCreateEntry(origin);

            _built = true;
            _mapRef = map;
            Log("cluster built: " + _systems.Count + " systems in sector " + sector.name
                + " (travel via the gates on the galaxy map)");
        }
        catch (Exception e)
        {
            Log("cluster build failed: " + e);
        }
    }

    /// <summary>Server-driven gate pair: pure replication. `a`'s gate sits at posA and
    /// targets `b`; `b`'s gate sits at posB (optionally flagged sectorJumpgate, which
    /// unlocks the containing sector via the vanilla rule) and targets `a`. Names and
    /// positions come from the server layout — the client invents nothing.
    /// Returns the gate on `a` (for entry-door teardown tracking).</summary>
    private static JumpGate? CreateGate(SystemMapData a, SystemMapData b, Vector2 posA, Vector2 posB,
        string name, bool sectorGateOnB, bool unlocked)
    {
        var gb = new JumpGate();
        gb.name = name + " -> " + b.name;
        gb.targetSystemGuid = b.guid;
        a.SetupPOI(gb, posA);
        a.pointsOfInterest.Add(gb);

        var ga = new JumpGate();
        ga.name = name + " -> " + a.name;
        ga.targetSystemGuid = a.guid;
        ga.sectorJumpgate = sectorGateOnB;
        b.SetupPOI(ga, posB);
        b.pointsOfInterest.Add(ga);

        gb.targetPoiGuid = ga.guid;
        ga.targetPoiGuid = gb.guid;
        if (unlocked)
        {
            gb.UnlockJumpgate();
            ga.UnlockJumpgate();
        }

        // Hard verification: both endpoints must resolve through the vanilla lookup.
        var tA = gb.GetTargetPOI();
        var tB = ga.GetTargetPOI();
        Log($"gate '{gb.name}' resolves -> {(tA?.name ?? "NULL")}; '{ga.name}' -> {(tB?.name ?? "NULL")}");
        return gb;
    }

    /// <summary>Gate POI LOCAL position: 12 units out from the system center toward the
    /// peer, clamped to the vanilla POI band. POI coordinates are system-local — this
    /// returns an offset from the system's own center, not an absolute layout position.</summary>
    private static Vector2 DirectionalPoiPos(Vector2 from, Vector2 to)
    {
        var d = to - from;
        var dir = d.sqrMagnitude < 0.0001f ? Vector2.right : d.normalized;
        var pos = dir * 12f;
        pos.x = Mathf.Clamp(pos.x, -19f, 19f);
        pos.y = Mathf.Clamp(pos.y, -4.5f, 4.5f);
        return pos;
    }

    /// <summary>Materializes the server-defined entry door into the joining player's
    /// current vanilla system (the only anchor discovery on the client; the server names
    /// the door, its target, its sector-gate flag, openness and the hub-side position).</summary>
    private bool TryCreateEntry(SystemMapData origin)
    {
        var entry = _layout?.Entry;
        if (entry == null || origin == null) return false;
        var target = _systems.FirstOrDefault(s => s.guid == entry.TargetSystemId || s.storyId == entry.TargetSystemId);
        if (target == null || IsClusterSystem(origin.storyId) || origin == target) return false;
        try
        {
            var originPos = DirectionalPoiPos(origin.position, target.position);
            var targetPos = new Vector2(entry.HubX, entry.HubY);
            var og = CreateGate(origin, target, originPos, targetPos, entry.Name, entry.SectorGate, entry.Unlocked);
            if (og != null)
            {
                _entryGates.Add((origin, og));
                Log("entry gate created from " + origin.name + " to " + target.name);
                return true;
            }
        }
        catch (Exception e) { Log("entry gate: " + e.Message); }
        return false;
    }

    /// <summary>Scans the game's settled space and returns the candidate position with
    /// the greatest distance to its nearest existing sector (no overlap, guaranteed).
    /// Vanilla sectors live on a sparse grid (e.g. Xeradon Belt at (41.8, 21.3)); a naive
    /// origin like (30,0) lands inside that grid system. Returns null when the map is not
    /// settled yet or no candidate has enough clearance — the caller must retry, never
    /// blind-place: FallbackOrigin (120,4) sits inside the vanilla grid.</summary>
    private Vector2? FindClusterOrigin(GalaxyMapData map)
    {
        var sectors = map.allSectors.ToList();
        if (sectors.Count == 0) return null;   // map not settled yet (save load) — retry
        var best = -1f;
        var bestPos = FallbackOrigin;
        for (var x = -1400f; x <= 1500f; x += 25f)
        {
            for (var y = -350f; y <= 350f; y += 25f)
            {
                var c = new Vector2(x, y);
                var minD = float.MaxValue;
                foreach (var s in sectors)
                {
                    var d = Vector2.Distance(c, s.position);
                    if (d < minD) minD = d;
                }
                if (minD > best) { best = minD; bestPos = c; }
            }
        }
        if (best < MinClusterClearance)
        {
            Log($"no safe cluster area yet (nearest sector {best:F0} u) — retrying");
            return null;
        }
        Log("cluster origin " + bestPos + " (nearest sector " + best.ToString("F0") + " u)");
        return bestPos;
    }

    private void AddPoi(SystemMapData system, LayoutPoi def)
    {
        try
        {
            // Stations must be created via SystemMapData.AddSpaceStation: it calls
            // SetFacilities() which constructs every service member (shops, bar,
            // economy, shipyard, refinery/forge, mission board...). A bare
            // `new SpaceStation()` leaves them all null — an empty station.
            if (def.Kind == "station")
            {
                var faction = def.Faction.Length == 0 ? null : Faction.Get(def.Faction);
                var facilities = new HashSet<SpaceStationFacility>
                {
                    SpaceStationFacility.Airlock,
                    SpaceStationFacility.GeneralShop,
                    SpaceStationFacility.TradeTerminal,
                    SpaceStationFacility.Bar,
                    SpaceStationFacility.Shipyard,
                    SpaceStationFacility.MissionBoard,
                    SpaceStationFacility.Refinery,
                };
                var station = system.AddSpaceStation(facilities, new Vector2(def.X, def.Y), faction,
                    def.Name.Length > 0 ? def.Name : def.Kind);
                station.SetIdentifier(def.Id);
                Log($"station '{def.Id}' created via AddSpaceStation ({facilities.Count} facilities: "
                    + string.Join(",", facilities) + ")");
                // AddSpaceStation already did SetupPOI + pointsOfInterest.Add.
                return;
            }

            MapPointOfInterest poi = def.Kind switch
            {
                "mining" => new Mining(),
                "salvage" => new Salvage(),
                "gate" => new JumpGate(),
                "wormhole" => new Wormhole(),
                _ => new Mining(),
            };
            poi.SetIdentifier(def.Id);
            poi.name = def.Name.Length > 0 ? def.Name : def.Kind;
            var poiFaction = def.Faction.Length == 0 ? null : Faction.Get(def.Faction);
            system.SetupPOI(poi, new Vector2(def.X, def.Y), poiFaction, Mathf.Max(1, def.Level));
            system.pointsOfInterest.Add(poi);
        }
        catch (Exception e) { Log("poi " + def.Id + ": " + e.Message); }
    }

    // ---------- teardown (disconnect / stale reconcile) ----------

    public void RequestTeardown() => _teardownPending = true;

    public void Teardown()
    {
        try
        {
            var map = GamePlayer.current?.map;
            foreach (var (system, poi) in _entryGates)
            {
                try { system.RemovePointOfInterest(poi); } catch { /* already gone */ }
            }
            _entryGates.Clear();
            foreach (var system in _systems)
            {
                foreach (var poi in system.pointsOfInterest.ToArray()) system.RemovePointOfInterest(poi);
                system.sector?.RemoveSystem(system);
            }
            _systems.Clear();
            if (map != null && _sector != null) map.RemoveSector(_sector);
            _sector = null;
            _teardownPending = false;
            _built = false;
            Log("cluster torn down");
        }
        catch (Exception e)
        {
            Log("teardown: " + e.Message);
        }
    }

    /// <summary>Crash safety: remove any leftover hosted systems from a previous session
    /// (loaded from a save that was baked while connected).</summary>
    private static void Log(string line)
    {
        MmoPlugin.Instance?.Session.Log("[world] " + line);
    }
}
