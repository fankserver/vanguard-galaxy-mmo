using System;
using System.Reflection;
using Behaviour.Managers;
using Behaviour.Unit;
using HarmonyLib;
using Source.Player;
using Source.SpaceShip.Auto;
using UnityEngine;

namespace VGMMO.Client;

/// <summary>
/// Vanilla fleet-travel safety fix. The game's SetFleetPosition dereferences
/// `fleetShip.autoActions as WingmanActions` unguarded: a fleet ship whose AutoActions
/// is null (or a different type, e.g. CombatActions) yields a null cast and an NRE,
/// which kills the jump-arrival coroutine and freezes the ship mid-arrival.
/// We replicate the vanilla positioning with a null-safe offset; the broken original
/// never runs.
/// </summary>
[HarmonyPatch(typeof(GameplayManager), nameof(GameplayManager.SetFleetPosition))]
public static class SafeSetFleetPosition
{
    private static bool Prefix(GameplayManager __instance, Vector2 position)
    {
        try
        {
            var ship = __instance.spaceShip;
            if (ship == null) return false;
            ship.transform.position = position;
            var rb = ship.GetComponent<Rigidbody2D>();
            if (rb != null) rb.position = position;

            foreach (var fleetShip in __instance.fleetSpaceShips)
            {
                if (fleetShip == null) continue;
                var offset = (fleetShip.autoActions as WingmanActions)?.relativeFormationPos ?? Vector2.zero;
                fleetShip.transform.position = position + offset;
                var frb = fleetShip.GetComponent<Rigidbody2D>();
                if (frb != null) frb.position = position + offset;
            }
        }
        catch (Exception e)
        {
            MmoPlugin.Instance?.LogSource.LogWarning("[patches] SetFleetPosition fallback: " + e.Message);
        }
        return false; // never run the original — it is the broken one
    }
}

/// <summary>
/// Same vanilla bug, second site: TravelManager.FixedUpdate casts a fleet ship's
/// `autoActions as WingmanActions` unguarded and NREs every physics frame while
/// travelling with a non-wingman fleet member — the log spanner. Null-safe replica.
/// </summary>
[HarmonyPatch(typeof(TravelManager), "FixedUpdate")]
public static class SafeTravelFixedUpdate
{
    private static FieldInfo? _spaceShipField;

    private static bool Prefix(TravelManager __instance)
    {
        try
        {
            _spaceShipField ??= typeof(TravelManager).GetField("spaceShip", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var ship = (SpaceShip?)_spaceShipField?.GetValue(__instance);
            if (ship == null || !ship.unitData.travelling || __instance.localTarget == null
                || ship.IsNearWorldPosition(__instance.worldLocationToTravelTo))
                return false; // same early-out as the vanilla guard

            ship.MoveTowards(__instance.worldLocationToTravelTo);
            foreach (var item in GamePlayer.current.activeFleet)
            {
                if (item.unit == null) continue;
                var fleetShip = item.unit as SpaceShip;
                if (fleetShip == null) continue;
                var off = Vector2.zero;
                if (fleetShip.autoActions != null)
                {
                    if (fleetShip.autoActions.leaving) continue;
                    off = (fleetShip.autoActions as WingmanActions)?.relativeFormationPos ?? Vector2.zero;
                }
                item.travelSpeed = ship.spaceShipData.travelSpeed;
                fleetShip.MoveTowards(__instance.worldLocationToTravelTo + off);
            }
        }
        catch (Exception e)
        {
            MmoPlugin.Instance?.LogSource.LogWarning("[patches] TravelFixedUpdate fallback: " + e.Message);
        }
        return false;
    }
}
