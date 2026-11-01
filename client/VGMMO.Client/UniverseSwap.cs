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

    /// <summary>Double-leave latch: a re-click while a leave is in flight would send
    /// LeaveUniverse twice -> two FlushAcks -> two interleaved restore coroutines
    /// against the same player/sidecar. Refuse + hint (the re-click is also the retry
    /// path when a FlushAck never arrives — documented, no more code needed).</summary>
    public static bool Leaving { get; private set; }

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

    /// <summary>Graceful LEAVE (contract §2 + S5): runs only after the server's
    /// FlushAck. Staged restore -> guard release -> sidecar delete -> builder reset.
    /// Exception-aware + timeout: LeaveFlow catches every throw and releases the
    /// guard regardless; a stalled restore (onDone never fires) is timed out after
    /// 60s so the leave can never hang the session.</summary>
    public static IEnumerator LeaveRoutine(string slot)
    {
        if (Leaving)
        {
            MmoPlugin.Instance?.LogSource.LogWarning("[universe] leave already in progress (re-request = retry if the flush-ack never arrived)");
            yield break;
        }
        if (!MmoUniverse.Active)
        {
            MmoPlugin.Instance?.LogSource.LogWarning("[universe] leave ignored: no active session");
            yield break;
        }
        Leaving = true;
        yield return LeaveInnerDriven(slot);
    }

    private static IEnumerator LeaveInnerDriven(string slot)
    {
        var flow = new LeaveFlow(slot);
        try
        {
            while (flow.MoveNext()) yield return flow.Current;
        }
        finally
        {
            Leaving = false;
        }
    }

    private sealed class LeaveFlow : IEnumerator
    {
        private readonly string _slot;
        private readonly IEnumerator _inner;
        private bool _done;
        private float _startReal;

        public LeaveFlow(string slot)
        {
            _slot = slot;
            _inner = LeaveInner(slot);
        }
        public object? Current { get; private set; }
        public bool MoveNext()
        {
            if (_done) return false;
            try
            {
                if (_startReal == 0f) _startReal = Time.unscaledTime;
                // Timeout of a stalled restore: never hang the leave.
                if (Time.unscaledTime - _startReal > 60f)
                    throw new System.Exception("leave restore timed out after 60s");
                var moved = _inner.MoveNext();
                Current = _inner.Current;
                if (!moved) { _done = true; }
                return moved;
            }
            catch (System.Exception e)
            {
                MmoPlugin.Instance?.LogSource.LogWarning("[universe] leave flow error (recovering): " + e.Message);
                _done = true;
                Leaving = false;
                // Recovery: whatever state we're in, release the guard and reset the
                // builder; the sidecar is KEPT (inspect) — never delete on uncertainty.
                MmoUniverse.Exit();
                if (MmoPlugin.Instance?.World != null)
                {
                    MmoPlugin.Instance.World.UniverseMode = false;
                    MmoPlugin.Instance.World.SetLayout(null, "");
                }
                return false;
            }
        }
        public void Reset() { }
        public void Dispose() { }
    }

    private static IEnumerator LeaveInner(string slot)
    {
        var player = GamePlayer.current!;
        var ok = false;
        SetTransfer(player, true);
        try
        {
            yield return LoadingScreen.ShowStage(LoadingStage.CreateWorld, "@UITransferFrontier");
            var restoreDone = false;
            var restore = HomeGalaxySidecar.RestoreRoutine(slot, r => restoreDone = r);
            while (!restoreDone)
            {
                if (!restore.MoveNext()) break;
                yield return restore.Current;
            }
            ok = restoreDone;
        }
        finally
        {
            SetTransfer(player, false);
        }
        if (MmoPlugin.Instance?.World is { } world)
        {
            world.UniverseMode = false;
            world.SetLayout(null, "");
        }
        MmoUniverse.Exit();
        if (ok)
        {
            if (HomeGalaxySidecar.Delete(slot)) MmoPlugin.Instance?.LogSource.LogInfo("[universe] left — home galaxy restored, sidecar removed");
        }
        else
        {
            MmoPlugin.Instance?.LogSource.LogWarning($"[universe] leave: restore NOT ok — sidecar kept for inspection ({HomeGalaxySidecar.SidecarPathFor(slot)})");
        }
        MmoPlugin.Instance?.Session?.SendLeaveWorld();
        MmoPlugin.Instance?.LogSource.LogInfo("[universe] departed");
    }

    /// <summary>Stale sidecar scan (crash safety): at plugin start, any sidecar file
    /// with no active session is an orphan from a killed universe visit — keep it for
    /// inspection and log loudly; never auto-restore without user action.</summary>
    public static void CheckOrphans()
    {
        try
        {
            if (!System.IO.Directory.Exists(HomeGalaxySidecar.Dir)) return;
            foreach (var f in System.IO.Directory.GetFiles(HomeGalaxySidecar.Dir, "home-*.json.gz"))
                MmoPlugin.Instance?.LogSource.LogWarning($"[universe] STALE SIDE CAR found: {f} (crash orphan from a previous universe visit — kept for inspection; delete to discard)");
        }
        catch (System.Exception e)
        {
            MmoPlugin.Instance?.LogSource.LogWarning("[universe] orphan scan failed: " + e.Message);
        }
    }

    /// <summary>Save-comparison helper (S5 acceptance): deep-compare the sidecar's
    /// Player JSON against the ACTUAL save file on disk, excluding documented
    /// volatile fields (timers/cooldowns/active effects — they legitimately drift).</summary>
    public static string VerifyAgainstSaveFile(string slot)
    {
        try
        {
            var sidecar = HomeGalaxySidecar.LoadRaw(slot);
            if (sidecar == null) return "no sidecar";
            var path = System.IO.Path.Combine(SaveGame.SavesPath, slot + ".save");
            if (!System.IO.File.Exists(path)) return "save file missing: " + path;
            var json = System.Text.Encoding.UTF8.GetString(
                DecompressAll(System.IO.File.ReadAllBytes(path)));
            var file = LightJson.JsonValue.Parse(json);
            var a = Newtonsoft.Json.Linq.JToken.Parse(sidecar["Player"].ToString());
            var b = Newtonsoft.Json.Linq.JToken.Parse(file["Player"].ToString());
            // Documented volatile whitelist (legitimately changes in-session):
            // elapsedTime, dynamicEventTimer, extraMercenarySpawnTimer, abilityCooldowns,
            // activeEffects, activeToggles, bossSpawnLevels.
            var volatileKeys = new[] { "elapsedTime", "dynamicEventTimer", "extraMercenarySpawnTimer",
                "abilityCooldowns", "activeEffects", "activeToggles", "bossSpawnLevels" };
            foreach (var key in volatileKeys)
            {
                if (a is Newtonsoft.Json.Linq.JObject ao && ao.ContainsKey(key)) ao[key].Remove();
                if (b is Newtonsoft.Json.Linq.JObject bo && bo.ContainsKey(key)) bo[key].Remove();
            }
            return Newtonsoft.Json.Linq.JToken.DeepEquals(a, b)
                ? "save matches sidecar (volatile excluded)"
                : "SAVE DIFFERS from sidecar (volatile excluded)";
        }
        catch (System.Exception e)
        {
            return "verify failed: " + e.Message;
        }
    }

    private static byte[] DecompressAll(byte[] raw)
    {
        using var ms = new System.IO.MemoryStream(raw);
        using var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress);
        using var outMs = new System.IO.MemoryStream();
        gz.CopyTo(outMs);
        return outMs.ToArray();
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
