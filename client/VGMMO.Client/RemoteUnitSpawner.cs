using System;
using System.Text.RegularExpressions;
using Behaviour.Unit;
using Source.Galaxy;
using Source.Player;
using Source.Personnel;
using Source.SpaceShip;
using Source.Util;
using UnityEngine;

namespace VGMMO.Client;

/// <summary>
/// Spawns remote pilots as REAL game units via the vanilla NPC path
/// (BasePoiManager.CreatePasserbyShipData -> poi.AddUnit -> AddToWorld), so the
/// ship gets everything the game provides for native ships: hover tooltips
/// (name/class/faction/captain), targetability, health bars. The ONLY difference
/// from an NPC: autoActions stays null (no local AI) and the unit is driven by
/// relayed server state instead. Falls back to the renderer clone when no hull
/// class is available or no POI is current.
/// </summary>
public static class RemoteUnitSpawner
{
    public sealed class Handle
    {
        public SpaceShipData Data = null!;
        public AbstractUnit Unit = null!;
        public MapPointOfInterest Poi = null!;
    }

    /// <summary>Creates NPC-like ship data and spawns it at the current POI.
    /// Returns null when the hull class is unknown or no POI is current.</summary>
    public static Handle? TrySpawn(string shipId, string factionId, string? fullName, string? callsignName,
        string? title, string? titleColor, float x, float y)
    {
        var poi = MapPointOfInterest.currentOrNext;
        if (poi == null || string.IsNullOrEmpty(shipId)) return null;
        var hull = GamePlayer.current?.spaceShips?.Find(d => d.name == shipId)?.unitDefinition as Behaviour.Unit.SpaceShip;
        if (hull == null) return null;
        try
        {
            // Player-flagged data (faction == Faction.player) makes
            // LoadDefaultEquipment use the LEVEL-GATED player builder set
            // (availableBuilders); a fresh replica has no level, so every slot
            // comes up empty - 'Cannot find equipmentbuilder' - including the
            // ENGINE, and the ship can never thrust. NPC resolution uses the
            // full unfiltered builder set, so replicas spawn as INDEPENDENT
            // data and are marked alwaysFriendly (no combat behavior). The
            // server-stamped wire identity stays authoritative; the tooltip
            // owner line reads Independent instead of Player (engine first).
            var data = new SpaceShipData(hull, isPlayer: false, Faction.independent)
            {
                autoActions = null,   // no local AI — the server drives this ship
                alwaysFriendly = true,
            };
            if (!string.IsNullOrEmpty(fullName))
            {
                // Captain shown in the tooltip: pilot's name + title.
                var (first, callsign, last) = SplitName(fullName);
                var cap = CaptainData.CreateRandom(new SeededRandom((ulong)DateTime.UtcNow.Ticks));
                cap.SetName(first, callsign, last);
                if (!string.IsNullOrEmpty(title))
                {
                    Color c = Color.white;
                    if (titleColor != null) ColorUtility.TryParseHtmlString(titleColor, out c);
                    cap.SetTitle(title, c);
                }
                data.commanderData = cap;
                data.customShipName = callsignName ?? fullName;
            }
            var scale = MapPointOfInterest.mapToLocalConversion;
            data.positionData.position = new Vector2(x, y) * scale;
            data.positionData.rotation = 0f;
            var unit = poi.AddUnit(data);
            if (unit == null) return null;
            // Replicas are player-owned ships: they must not behave like armed NPCs.
            // AddToWorld does NOT run the travel path (ReactivateEquipmentAfterTravel),
            // so equipment state on spawn is unverified-by-design — the game itself
            // disables equipment for ships it doesn't want shooting (travel path), and
            // we apply the same rule here: kill the whole turret/weapon risk class.
            // The guard must NEVER fail the spawn: a fresh AddUnit's module children
            // may not have bound their refs yet, so a throwing module degrades the
            // replica to the clone fallback. Try now; on throw, retry next frame.
            TryDisableEquipment(unit);
            Reseed(unit);
            MmoPlugin.Instance?.LogSource.LogInfo($"[replica] spawned real unit '{shipId}' ({unit.name}) at poi '{poi.name}'");
            return new Handle { Data = data, Unit = unit, Poi = poi };
        }
        catch (Exception e)
        {
            MmoPlugin.Instance?.LogSource.LogWarning("[replica] spawn failed: " + e);
            return null;
        }
    }

    /// <summary>Steers the replica toward a MOVING target in world units — the same
    /// vanilla call the game uses for wingmen/recovery (SetMovingOverrideDestination).
    /// The replica's own ShipNavigator + ApplyThrust execute the pursuit, so
    /// thrust/rotation/banking and their effects fire from real engine calls.</summary>
    public static void SetTarget(Handle h, float tx, float ty, float tvx, float tvy)
    {
        h.Unit.SetMovingOverrideDestination(new Vector2(tx, ty), new Vector2(tvx, tvy));
    }

    /// <summary>Rare hard correction: pose snap + effect reseed. Only used when the
    /// replica diverged beyond HardError or went stale (respawn/transfer edges).</summary>
    public static void HardSnap(Handle h, float x, float y, float rot, float vx, float vy)
    {
        h.Data.positionData.position = new Vector2(x, y);
        h.Data.positionData.rotation = rot;
        h.Data.positionData.velocity = new Vector2(vx, vy);
        h.Data.positionData.SetDataToRigidbody(h.Unit.rigidbody);
        Reseed(h.Unit);
    }

    /// <summary>Best-effort equipment guard: disables all weapon/module equipment on
    /// the replica. A derived module's Deactivate can throw while the fresh unit's
    /// children are still binding refs; that must degrade nothing — retry on the
    /// next frame once Awake/OnEnable finished.</summary>
    private static void TryDisableEquipment(AbstractUnit unit)
    {
        var ship = unit as SpaceShip;
        if (ship == null) return;
        try { ship.DisableEquipment(); }
        catch (Exception e)
        {
            MmoPlugin.Instance?.LogSource.LogWarning("[replica] equipment guard deferred: " + e.Message);
            MmoPlugin.Instance?.StartCoroutine(DeferredDisable(ship));
        }
    }

    private static System.Collections.IEnumerator DeferredDisable(SpaceShip ship)
    {
        yield return null;
        try { if (ship != null) ship.DisableEquipment(); }
        catch (Exception e) { MmoPlugin.Instance?.LogSource.LogWarning("[replica] equipment guard failed: " + e.Message); }
    }

    /// <summary>Clears particle/trail buffers so a hard snap (or spawn/despawn/
    /// system transfer) does not streak artifacts from the old pose. Particle
    /// simulation spaces are left as authored on the prefabs: world-space smoke
    /// stays behind, local-space exhaust follows the hull.</summary>
    private static void Reseed(AbstractUnit unit)
    {
        try
        {
            foreach (var ps in unit.GetComponentsInChildren<ParticleSystem>(true)) ps.Clear(true);
            foreach (var tr in unit.GetComponentsInChildren<TrailRenderer>(true)) tr.Clear();
        }
        catch (Exception e) { MmoPlugin.Instance?.LogSource.LogWarning("[replica] reseed: " + e.Message); }
    }

    public static void Despawn(Handle h)
    {
        try
        {
            if (h != null && h.Poi != null && h.Data != null)
                h.Poi.RemoveUnit(h.Data, UnitRemovalReason.Cleared);
        }
        catch (Exception e) { MmoPlugin.Instance?.LogSource.LogWarning("[replica] despawn: " + e.Message); }
    }

    /// <summary>Splits 'First "Callsign" Last' into (first, callsign, last).</summary>
    private static (string first, string callsign, string last) SplitName(string full)
    {
        var m = Regex.Match(full, @"^\s*([^""]+?)\s*(?:""([^""]+)"")?\s*([^""]*?)\s*$");
        if (!m.Success) return (full, "", "");
        var first = m.Groups[1].Value.Trim();
        var callsign = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";
        var last = m.Groups[3].Value.Trim();
        if (first.Length == 0 && callsign.Length == 0 && last.Length == 0) return (full, "", "");
        if (first.Length == 0) first = callsign;
        return (first, callsign, last);
    }
}
