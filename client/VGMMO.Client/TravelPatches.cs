using System;
using Behaviour.Unit;
using HarmonyLib;
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
