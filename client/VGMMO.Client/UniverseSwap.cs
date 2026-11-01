using System.Collections;
using System.Linq;
using Behaviour.Managers;
using Behaviour.UI.Main_Menu;
using Source.Player;
using Source.Galaxy;
using Source.Player;
using Source.Util;
using UnityEngine;
using VGMMO.Protocol;

namespace VGMMO.Client;

/// <summary>
/// M5 universe swap — the staged JOIN flow (contract §3 S4). Mirrors the
/// vanilla world-swap pattern (GamePlayer.TransitionTutorialToSandboxStaged):
/// guards -> wait for pending writes -> sidecar capture -> ephemeral guard ->
/// worldTransferInProgress + loading stages -> detach map-referencing state ->
/// build the whole server layout (multi-sector) -> arrival jump gate ->
/// release transfer -> join the entry system (replicas spawn via the existing
/// path). Cluster mode is untouched; cluster content found in the save stays
/// in the sidecar (D4 — it restores with the home galaxy at leave).
/// </summary>
public static class UniverseSwap
{
    /// <summary>True while the join flow owns the game loop (GameplayManager is
    /// gated by the vanilla worldTransferInProgress flag).</summary>
    public static bool Joining { get; private set; }

    /// <summary>Refuse conditions (D2): interior, mid-travel, a stale transfer, or an
    /// already-active universe session. Returns a reason string or null when ok.</summary>
    public static string? GuardReason()
    {
        if (MmoUniverse.Active) return "already in a universe session";
        if (Joining) return "join already in progress";
        if (GamePlayer.current == null) return "no loaded player";
        if (GamePlayer.current.worldTransferInProgress) return "world transfer in progress";
        if (Behaviour.UI.Spacestation.SpaceStationInterior.instance != null) return "inside a space station interior (leave it first)";
        var travel = Behaviour.Util.Singleton<TravelManager>.Instance;
        if (travel != null && travel.TravelActive()) return "ship is traveling";
        return null;
    }

    private static void SetTransfer(Source.Player.GamePlayer player, bool value)
    {
        var pi = typeof(Source.Player.GamePlayer).GetProperty("worldTransferInProgress");
        if (pi == null)
        {
            MmoPlugin.Instance?.LogSource.LogWarning("[universe] CRITICAL: worldTransferInProgress property missing — vanilla changed; aborting swap semantics");
            return;
        }
        pi.SetValue(player, value);
    }

    /// <summary>Abort-after-detach recovery: the map is cleared and the guard armed —
    /// restore the sidecar in place and disarm before yielding the join. Restore is
    /// atomic (S2) and proven; the player lands back in their home galaxy.</summary>
    private static IEnumerator RecoverInPlace(string slot)
    {
        if (slot.Length > 0)
            yield return HomeGalaxySidecar.RestoreRoutine(slot, ok =>
                MmoPlugin.Instance?.LogSource.LogWarning($"[universe] join aborted — home galaxy restored: {ok}"));
        MmoUniverse.Exit();
    }

    public static IEnumerator JoinRoutine(WorldLayout layout, string layoutHash)
    {
        var guard = GuardReason();
        if (guard != null)
        {
            MmoPlugin.Instance?.LogSource.LogWarning($"[universe] join refused: {guard}");
            yield break;
        }
        if (layout?.Systems == null || layout.Systems.Count == 0)
        {
            MmoPlugin.Instance?.LogSource.LogWarning("[universe] join refused: empty layout");
            yield break;
        }

        Joining = true;
        var player = GamePlayer.current!;
        var slot = HomeGalaxySidecar.LastSaveName ?? "";
        var targetId = "";
        try
        {
            // Ordering is load-bearing (reviewer contract): wait for any in-flight
            // write (nothing pending at connect, but be correct), snapshot the home
            // galaxy, THEN arm the guard; a pre-guard in-flight write completing is
            // harmless (pre-capture content), but the sidecar must postdate it.
            SaveGame.WaitForPendingWrite();
            yield return null;

            if (!HomeGalaxySidecar.Capture(slot))
            {
                MmoPlugin.Instance?.LogSource.LogWarning($"[universe] join aborted: sidecar capture failed (slot '{slot}')");
                yield break;
            }
            if (!MmoUniverse.Enter())
            {
                MmoPlugin.Instance?.LogSource.LogWarning("[universe] join aborted: ephemeral guard refused");
                yield break;
            }

            SetTransfer(player, true);
            try
            {
                yield return LoadingScreen.ShowStage(LoadingStage.CreateWorld, "@UITransferFrontier");

                // Detach map-referencing state (the sidecar holds it; restore re-points
                // at leave). Mirrors vanilla LeavePrologueForFrontier.
                player.missions.Clear();
                // Active contracts tick EVERY frame against map POIs and would complete
                // mid-universe (rewards land on the persisted player; the sidecar
                // restore would then resurrect the mission -> dupe window). The sidecar
                // holds them (S2 restore re-points); nulling here is lossless.
                player.currentBounty = null;
                player.currentPatrol = null;
                player.currentIndustry = null;
                player.routes.routes.Clear();
                player.map.ClearSectors();
                Source.Player.Register.ClearVisitedSystems();
                player.homeStation = null;
                player.lastVisitedMiningPOI = null;
                player.lastStation = null;
                player.waypoints.Clear();
                player.currentSystem = null;
                player.currentPointOfInterest = null;

                // Whole-galaxy build (multi-sector, hash-stamped) via the world builder.
                var world = MmoPlugin.Instance?.World;
                if (world == null)
                {
                    MmoPlugin.Instance?.LogSource.LogWarning("[universe] join aborted: no world builder");
                    yield return RecoverInPlace(slot);
                    yield break;
                }
                world.UniverseMode = true;
                world.SetLayout(layout, layoutHash);
                if (!world.BuildUniverse(layout))
                {
                    MmoPlugin.Instance?.LogSource.LogWarning("[universe] join aborted: universe build failed");
                    yield return RecoverInPlace(slot);
                    yield break;
                }

                // Arrival: place the player at the layout's entry system with a vanilla
                // arrival gate (CreateArrivalJumpGate pattern).
                yield return LoadingScreen.ShowStage(LoadingStage.Arrival, "@UITransferArrival");
                var entry = layout.Entry;
                targetId = (entry != null && entry.TargetSystemId.Length > 0)
                    ? entry.TargetSystemId
                    : layout.Systems[0].Id;
                var system = player.map.allSystems.FirstOrDefault(x => x.storyId == targetId || x.guid == targetId)
                    ?? player.map.GetSystemByStoryId(targetId)
                    ?? player.map.GetSystemByStoryId(layout.Systems[0].StoryId);
                if (system == null)
                {
                    MmoPlugin.Instance?.LogSource.LogWarning($"[universe] join aborted: entry system '{targetId}' not found");
                    yield return RecoverInPlace(slot);
                    yield break;
                }
                player.currentSystem = system;
                var gate = new Source.Galaxy.POI.JumpGate();
                gate.UnlockJumpgate();
                gate.hasGate = false;
                gate.timeLeft = 5f;
                system.SetupPOI(gate);
                system.pointsOfInterest.Add(gate);
                player.currentPointOfInterest = gate;
                player.waypoints.Clear();
                player.waypoints.Add(gate);
                LoadingScreen.SetStage(LoadingStage.Arrival, 1f, null);
            }
            finally
            {
                SetTransfer(player, false);
            }

            // Join the entry system through the normal presence path (the in-cluster
            // detection keys on hosted storyIds in MmoSession.Update — currentSystem is
            // now a hosted universe system, so it joins automatically).
            MmoPlugin.Instance?.LogSource.LogInfo($"[universe] joined '{layout.Name}' at {targetId} — ephemeral guard ON");
        }
        finally
        {
            Joining = false;
        }
    }
}
