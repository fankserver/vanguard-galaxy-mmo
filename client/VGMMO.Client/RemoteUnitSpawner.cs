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
            var faction = !string.IsNullOrEmpty(factionId) ? Faction.Get(factionId) : null;
            var data = new SpaceShipData(hull, isPlayer: false, faction ?? Faction.player)
            {
                autoActions = null,   // no local AI — the server drives this ship
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
            MmoPlugin.Instance?.LogSource.LogInfo($"[replica] spawned real unit '{shipId}' ({unit.name}) at poi '{poi.name}'");
            return new Handle { Data = data, Unit = unit, Poi = poi };
        }
        catch (Exception e)
        {
            MmoPlugin.Instance?.LogSource.LogWarning("[replica] spawn failed: " + e.Message);
            return null;
        }
    }

    /// <summary>Applies relayed state to the unit (server-side control): position,
    /// rotation, velocity in world units (local * conversion).</summary>
    public static void Drive(Handle h, float x, float y, float rot, float vx, float vy)
    {
        var s = MapPointOfInterest.mapToLocalConversion;
        h.Data.positionData.position = new Vector2(x, y) * s;
        h.Data.positionData.rotation = rot;
        h.Data.positionData.velocity = new Vector2(vx, vy) * s;
        h.Data.positionData.SetDataToRigidbody(h.Unit.rigidbody);
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
