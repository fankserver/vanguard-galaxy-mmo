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

    private WorldLayout? _layout;
    private bool _built;
    private bool _teardownPending;

    private SectorMapData? _sector;
    private readonly List<SystemMapData> _systems = new();
    private readonly List<(SystemMapData system, MapPointOfInterest poi)> _entryGates = new();

    private static readonly MethodInfo CreateSectorMethod = typeof(SandboxWorld)
        .GetMethod("CreateSector", BindingFlags.NonPublic | BindingFlags.Static)!;

    public void SetLayout(WorldLayout? layout)
    {
        _layout = layout;
        _built = false;
        _systems.Clear();
        _sector = null;
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

        // The entry gate needs a real current system; at load time it can still be
        // null, so retry on later frames until the player has one.
        if (_built && _entryGates.Count == 0)
        {
            var eorigin = GamePlayer.current.currentSystem;
            var hub = _systems.FirstOrDefault();
            if (eorigin != null && hub != null && !IsClusterSystem(eorigin.storyId) && eorigin != hub)
            {
                try
                {
                    var og = Gate(eorigin, hub, "MMO Gate", sectorGate: true);
                    if (og != null)
                    {
                        _entryGates.Add((eorigin, og));
                        var t = og.GetTargetPOI();
                        Log("entry gate created from " + eorigin.name + " to " + hub.name + " (target resolves: " + (t?.name ?? "NULL") + ")");
                    }
                }
                catch (Exception e) { Log("entry gate: " + e.Message); }
            }
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
                Log("cluster already present — adopting (" + _layout.Systems.Count + " systems)");
                return;
            }

            // Find a free spot: max clearance from every existing sector in the same
            // coordinate frame (sector positions are absolute, per the saved layout).
            var clusterOrigin = FindClusterOrigin(map);

            var sector = (SectorMapData)CreateSectorMethod.Invoke(null, new object[] { clusterOrigin, "MMO - " + _layout.Name })!;
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
                system.storyteller = new ClusterStoryteller(system);
                system.storyteller.SetupSystem();
                _systems.Add(system);
            }

            foreach (var link in _layout.Links)
            {
                var a = _systems.Find(s => s.storyId == link.A || s.guid == link.A);
                var b = _systems.Find(s => s.storyId == link.B || s.guid == link.B);
                if (a == null || b == null) continue;
                try
                {
                    Gate(a, b, "MMO Gate");
                }
                catch (Exception e) { Log("gate " + link.A + "<->" + link.B + ": " + e.Message); }
            }

            // Entry: a gate from the player's current vanilla system into the hub, so the
            // cluster is reachable (PocketWorlds-style door). Tracked for teardown.
            var hub = _systems.FirstOrDefault();
            var origin = GamePlayer.current.currentSystem;
            if (hub != null && origin != null && !IsClusterSystem(origin.storyId) && origin != hub)
            {
                try
                {
                    var og = Gate(origin, hub, "MMO Gate", sectorGate: true);
                    if (og != null) _entryGates.Add((origin, og));
                    Log("entry gate created from " + origin.name + " to " + hub.name);
                }
                catch (Exception e) { Log("entry gate: " + e.Message); }
            }

            _built = true;
            Log("cluster built: " + _systems.Count + " systems in sector " + sector.name
                + " (travel via the gates on the galaxy map)");
        }
        catch (Exception e)
        {
            Log("cluster build failed: " + e);
        }
    }

    /// <summary>Creates a bidirectional JumpGate pair between two systems, exactly like the
    /// vanilla helper does (public seams only): the gate ON `a` targets `b` and the gate ON
    /// `b` targets `a`, cross-linked via targetSystemGuid/targetPoiGuid and unlocked.
    /// `sectorGate` marks the gate on `b` as a sector jumpgate, which is what unlocks the
    /// containing sector (SectorMapData.IsUnlocked scans for an open sectorJumpgate).
    /// Returns the gate on `a`.</summary>
    private static JumpGate? Gate(SystemMapData a, SystemMapData b, string label, bool sectorGate = false)
    {
        var ga = new JumpGate();
        ga.name = label + " -> " + a.name;       // sits on b, points back at a
        ga.targetSystemGuid = a.guid;
        ga.sectorJumpgate = sectorGate;
        // Directional: the gate sits on the side facing its peer, so left system's gate
        // arrives on hub's LEFT and right system's gate on the RIGHT (no crossing).
        b.SetupPOI(ga, DirectionalPoiPos(b.position, a.position));
        b.pointsOfInterest.Add(ga);

        var gb = new JumpGate();
        gb.name = label + " -> " + b.name;       // sits on a, points at b
        gb.targetSystemGuid = b.guid;
        a.SetupPOI(gb, DirectionalPoiPos(a.position, b.position));
        a.pointsOfInterest.Add(gb);

        ga.targetPoiGuid = gb.guid;
        gb.targetPoiGuid = ga.guid;
        ga.UnlockJumpgate();
        gb.UnlockJumpgate();
        return gb;
    }

    /// <summary>Gate POI position: 12 units out from the system center toward the peer
    /// system, clamped to the vanilla POI band (±19 x, ±4.5 y).</summary>
    private static Vector2 DirectionalPoiPos(Vector2 from, Vector2 to)
    {
        var d = to - from;
        var dir = d.sqrMagnitude < 0.0001f ? Vector2.right : d.normalized;
        var pos = from + dir * 12f;
        pos.x = Mathf.Clamp(pos.x, -19f, 19f);
        pos.y = Mathf.Clamp(pos.y, -4.5f, 4.5f);
        return pos;
    }

    /// <summary>Scans the game's settled space and returns the candidate position with
    /// the greatest distance to its nearest existing sector (no overlap, guaranteed).
    /// Vanilla sectors live on a sparse grid (e.g. Xeradon Belt at (41.8, 21.3)); a naive
    /// origin like (30,0) lands inside that grid system.</summary>
    private static Vector2 FindClusterOrigin(GalaxyMapData map)
    {
        var sectors = map.allSectors.ToList();
        if (sectors.Count == 0) return FallbackOrigin;
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
        Log("cluster origin " + bestPos + " (nearest sector " + best.ToString("F0") + " u)");
        return bestPos;
    }

    private void AddPoi(SystemMapData system, LayoutPoi def)
    {
        try
        {
            MapPointOfInterest poi = def.Kind switch
            {
                "station" => new SpaceStation(),
                "mining" => new Mining(),
                "salvage" => new Salvage(),
                "gate" => new JumpGate(),
                "wormhole" => new Wormhole(),
                _ => new Mining(),
            };
            poi.SetIdentifier(def.Id);
            poi.name = def.Name.Length > 0 ? def.Name : def.Kind;
            var faction = def.Faction.Length == 0 ? null : Faction.Get(def.Faction);
            system.SetupPOI(poi, new Vector2(def.X, def.Y), faction, Mathf.Max(1, def.Level));
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

/// <summary>Per-system storyteller for hosted systems. Server-driven: nothing is
/// generated locally beyond what the layout defines.</summary>
public sealed class ClusterStoryteller : SystemStoryteller
{
    public ClusterStoryteller(SystemMapData system) : base(system) { }
    public override void SetupSystem() { }
}
