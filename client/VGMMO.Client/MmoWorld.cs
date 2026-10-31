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
    private static readonly Vector2 ClusterOrigin = new(30f, 0f);

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
        if (_built || _layout == null) return;
        if (MmoPlugin.Instance == null || !MmoPlugin.Instance.Session.Connected) return;
        Build();

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
                    var og = Gate(eorigin, hub, "MMO Gate");
                    if (og != null)
                    {
                        _entryGates.Add((eorigin, og));
                        Log("entry gate created from " + eorigin.name + " to " + hub.name);
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

            var sector = (SectorMapData)CreateSectorMethod.Invoke(null, new object[] { ClusterOrigin, "MMO - " + _layout.Name })!;
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
                    var og = Gate(origin, hub, "MMO Gate");
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
    /// vanilla helper does (public seams only): each side gets a JumpGate POI wired via
    /// targetSystemGuid/targetPoiGuid and is unlocked. Returns the gate on `a`.</summary>
    private static JumpGate? Gate(SystemMapData a, SystemMapData b, string label)
    {
        var ga = new JumpGate();
        ga.name = label + " -> " + b.name;
        ga.targetSystemGuid = b.guid;
        b.SetupPOI(ga, b.GetRandomPosition(-20f, 20f, -5f, 5f));
        b.pointsOfInterest.Add(ga);

        var gb = new JumpGate();
        gb.name = label + " -> " + a.name;
        gb.targetSystemGuid = a.guid;
        a.SetupPOI(gb, a.GetRandomPosition(-20f, 20f, -5f, 5f));
        a.pointsOfInterest.Add(gb);

        ga.targetPoiGuid = gb.guid;
        gb.targetPoiGuid = ga.guid;
        ga.UnlockJumpgate();
        gb.UnlockJumpgate();
        return gb;
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
