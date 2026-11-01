using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Source.Player;
using Source.Util;
using UnityEngine;

namespace VGMMO.Client;

/// <summary>M5.S3: the universe-session ephemeral guard. Setting
/// GamePlayer.isEphemeral makes vanilla no-op EVERY save path (verified by S3
/// audit: StoreAutosaveInBackground -> early return, Store forks on the flag,
/// and ALL writers - autosave tick, StoreAutosaveState incl. quit-flow,
/// SaveGameUI.DoSave - funnel through those two). This class is the gatekeeper:
/// re-entry is refused, exit clears the flag AFTER restore (S5 owns that order).</summary>
public static class MmoUniverse
{
    public static bool Active { get; private set; }

    public static bool Enter()
    {
        if (Active)
        {
            MmoPlugin.Instance?.LogSource.LogWarning("[universe] re-entry refused (session already active)");
            return false;
        }
        // The flag lives on GamePlayer — with no loaded player the guard would be
        // Active=true but NON-FUNCTIONAL (saves would silently proceed while the
        // suppression logs fire). Refuse instead of lying about the state.
        if (GamePlayer.current == null)
        {
            MmoPlugin.Instance?.LogSource.LogWarning("[universe] enter refused: no loaded player (guard cannot apply)");
            return false;
        }
        Active = true;
        GamePlayer.current.isEphemeral = true;
        MmoPlugin.Instance?.LogSource.LogInfo("[universe] ephemeral guard ON (no save writes while connected)");
        return true;
    }

    public static void Exit()
    {
        Active = false;
        if (GamePlayer.current != null) GamePlayer.current.isEphemeral = false;
        MmoPlugin.Instance?.LogSource.LogInfo("[universe] ephemeral guard OFF");
    }
}

/// <summary>M5 universe-swap console commands + save-slot recorder + S3 save-path
/// suppression logs. Dedicated *Patches.cs per project convention (Harmony only).</summary>
[HarmonyPatch(typeof(SaveGame))]
public static class UniverseSaveSuppressPatches
{
    /// <summary>Store is the funnel for DoSave (manual save UI) and StoreAutosaveState
    /// (side menu + quit flow). Log every suppressed attempt while a universe session
    /// is active — the vanilla isEphemeral short-circuit already blocks the write
    /// (SaveGame.cs:149); this is visibility for the acceptance criteria.</summary>
    [HarmonyPatch(nameof(SaveGame.Store))]
    [HarmonyPrefix]
    private static void OnStore(string saveName)
    {
        if (MmoUniverse.Active)
            MmoPlugin.Instance?.LogSource.LogInfo($"[universe] save suppressed: Store('{saveName}') — ephemeral guard ON");
    }

    /// <summary>Autosave tick (GameplayManager) and the debug 'asv' command take the
    /// StoreAutosaveInBackground path — its own isEphemeral short-circuit is
    /// SaveGame.cs:114; log here for the same acceptance visibility.</summary>
    [HarmonyPatch(nameof(SaveGame.StoreAutosaveInBackground))]
    [HarmonyPrefix]
    private static void OnBackgroundAutosave()
    {
        if (MmoUniverse.Active)
            MmoPlugin.Instance?.LogSource.LogInfo("[universe] save suppressed: StoreAutosaveInBackground — ephemeral guard ON");
    }
}

[HarmonyPatch(typeof(SaveGameFile))]
public static class UniverseSwapPatches
{
    /// <summary>Records the slot name whenever a save is loaded — the sidecar needs
    /// to know which home save it belongs to (D3: one stale-guarded sidecar per
    /// save name). The game exposes no "current slot" API, so we observe loads.</summary>
    [HarmonyPatch(nameof(SaveGameFile.LoadSaveGame))]
    [HarmonyPrefix]
    private static void OnLoadSaveGame(SaveGameFile __instance)
    {
        var name = __instance.Name;
        if (!string.IsNullOrEmpty(name))
        {
            HomeGalaxySidecar.LastSaveName = name;
            MmoPlugin.Instance?.LogSource.LogInfo($"[sidecar] save slot: {name}");
        }
    }

    /// <summary>Registers debug-console commands for the S2 acceptance round trip:
    ///   vgmmo sc capture   — snapshot the home galaxy to home-&lt;slot&gt;.json.gz
    ///   vgmmo sc restore   — rebuild the live map + re-point references from it
    ///   vgmmo sc roundtrip — capture -> mutate (map refs) -> restore -> compare
    ///   vgmmo sc check     — map equality check only
    ///   vgmmo sc delete    — remove the sidecar (inspect-then-delete flow)</summary>
    [HarmonyPatch(typeof(Behaviour.UI.DebugScreen.ConsoleScreen), "RegisterCommands")]
    [HarmonyPostfix]
    private static void OnRegisterCommands(Behaviour.UI.DebugScreen.ConsoleScreen __instance)
    {
        var field = typeof(Behaviour.UI.DebugScreen.ConsoleScreen)
            .GetField("commands", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(__instance) is not IList list) return;
        list.Add(new Behaviour.UI.DebugScreen.ConsoleCommand("VGMMOUniverseSwap", "vgmmo sc", arg =>
        {
            var parts = (arg ?? "").Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "help";
            var slot = HomeGalaxySidecar.LastSaveName ?? "unknown";
            switch (sub)
            {
                case "capture":
                    MmoPlugin.Instance?.LogSource.LogInfo($"[sidecar] capture: {(HomeGalaxySidecar.Capture(slot) ? "ok" : "FAILED")} (slot '{slot}')");
                    break;
                case "restore":
                    MmoPlugin.Instance?.StartCoroutine(RestoreAndReport(slot));
                    break;
                case "roundtrip":
                    MmoPlugin.Instance?.StartCoroutine(RoundTrip(slot));
                    break;
                case "check":
                    MmoPlugin.Instance?.LogSource.LogInfo($"[sidecar] check: {(HomeGalaxySidecar.MapMatchesSidecar(slot) ? "map equal" : "MAP DIFFERS")}");
                    break;
                case "ue":
                    if (parts.Length > 1 && parts[1].ToLowerInvariant() == "on") { MmoUniverse.Enter(); }
                    else if (parts.Length > 1 && parts[1].ToLowerInvariant() == "off") { MmoUniverse.Exit(); }
                    else MmoPlugin.Instance?.LogSource.LogInfo($"[universe] ue: {(MmoUniverse.Active ? "on" : "off")}");
                    break;
                case "delete":
                    MmoPlugin.Instance?.LogSource.LogInfo($"[sidecar] delete: {(HomeGalaxySidecar.Delete(slot) ? "ok" : "not found")}");
                    break;
                default:
                    MmoPlugin.Instance?.LogSource.LogInfo("[sidecar] usage: capture | restore | roundtrip | check | delete");
                    break;
            }
        }, "[universe] ue on|off (ephemeral guard debug) | sidecar capture/restore/roundtrip/check/delete"));
    }

    private static IEnumerator RestoreAndReport(string slot)
    {
        yield return HomeGalaxySidecar.RestoreRoutine(slot, ok =>
            MmoPlugin.Instance?.LogSource.LogInfo($"[sidecar] restore: {(ok ? "ok" : "FAILED")}"));
    }

    /// <summary>Acceptance round trip: capture -> corrupt map-referencing state ->
    /// restore -> assert map + references match the sidecar.</summary>
    private static IEnumerator RoundTrip(string slot)
    {
        if (!HomeGalaxySidecar.Capture(slot)) yield break;
        var player = GamePlayer.current;
        if (player == null) { MmoPlugin.Instance?.LogSource.LogWarning("[sidecar] roundtrip: no player"); yield break; }
        yield return null;
        // Mutate: clear waypoints, move the map cursor, drop currentSystem/POI refs,
        // and drop the active-contract refs (the S2-review hole: they tick every
        // frame and must be re-pointed by restore).
        player.waypoints.Clear();
        player.mapPosition = new Vector2(999f, 999f);
        player.currentSystem = null;
        player.currentPointOfInterest = null;
        player.currentBounty = null;
        player.currentPatrol = null;
        player.currentIndustry = null;
        yield return HomeGalaxySidecar.RestoreRoutine(slot, ok =>
            MmoPlugin.Instance?.LogSource.LogInfo($"[sidecar] roundtrip restore: {(ok ? "ok" : "FAILED")}"));
        yield return null;
        var eq = HomeGalaxySidecar.MapMatchesSidecar(slot);
        var wp = player.waypoints.Count;
        var b = player.currentBounty;
        var bPoiOk = b != null && b.sourcePoi != null && player.map.GetPointOfInterest(b.sourcePoi.guid) == b.sourcePoi;
        var pOk = player.currentPatrol == null || (player.currentPatrol.sourcePoi != null);
        var iOk = player.currentIndustry == null || (player.currentIndustry.sourcePoi != null);
        MmoPlugin.Instance?.LogSource.LogInfo(
            $"[sidecar] roundtrip: map={(eq ? "equal" : "DIFFERS")} waypoints={wp} mapPos={player.mapPosition} " +
            $"currentSystem={(player.currentSystem != null ? (player.currentSystem.storyId ?? player.currentSystem.name) : "null")} " +
            $"bounty={(b == null ? "null" : (bPoiOk ? "poi-resolved" : "DANGLING"))} patrol={(pOk ? "ok" : "DANGLING")} industry={(iOk ? "ok" : "DANGLING")}");
    }
}
