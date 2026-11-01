using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using LightJson;
using Source.Galaxy;
using Source.Util;
using UnityEngine;

namespace VGMMO.Client;

/// <summary>
/// M5 universe-swap sidecar: captures the player's HOME galaxy (the whole
/// SaveCurrentState JSON) before entering a shared universe and restores it on
/// the LIVE GamePlayer on leave — the map + every map-referencing player field
/// come back from the sidecar; player-level state (credits, ships, XP, empire)
/// is never touched because it was never replaced.
/// Files: &lt;plugin dir&gt;/home-&lt;saveName&gt;.json.gz — ONE stale-guarded file per save
/// name (D3): Capture refuses to overwrite an existing sidecar (crash orphan);
/// the orphan is removed only by explicit user action after inspection.
/// </summary>
public static class HomeGalaxySidecar
{
    /// <summary>Last save slot name seen by the game (recorded by UniverseSwapPatches).</summary>
    public static string? LastSaveName { get; set; }

    public static string Dir { get; } = Path.GetDirectoryName(typeof(HomeGalaxySidecar).Assembly.Location)
        ?? Path.Combine(Application.persistentDataPath, "VGMMO");

    public static string SidecarPathFor(string saveName) => Path.Combine(Dir, $"home-{saveName}.json.gz");

    public static bool Exists(string saveName) => File.Exists(SidecarPathFor(saveName));

    public static bool Delete(string saveName)
    {
        try { if (Exists(saveName)) { File.Delete(SidecarPathFor(saveName)); return true; } }
        catch (Exception e) { MmoPlugin.Instance?.LogSource.LogWarning("[sidecar] delete failed: " + e.Message); }
        return false;
    }

    /// <summary>Captures SaveCurrentState (Version + Player) into the sidecar.
    /// Refuses to overwrite an existing file (crash orphan — inspect first).</summary>
    public static bool Capture(string saveName)
    {
        try
        {
            if (string.IsNullOrEmpty(saveName)) return false;
            var path = SidecarPathFor(saveName);
            if (File.Exists(path))
            {
                MmoPlugin.Instance?.LogSource.LogWarning($"[sidecar] capture refused: {Path.GetFileName(path)} already exists (stale orphan — keep for inspection and delete manually)");
                return false;
            }
            Directory.CreateDirectory(Dir);
            var json = SaveGame.SaveCurrentState().ToString();
            File.WriteAllBytes(path, Gzip(json));
            MmoPlugin.Instance?.LogSource.LogInfo($"[sidecar] captured {Path.GetFileName(path)} ({json.Length} bytes)");
            return true;
        }
        catch (Exception e)
        {
            MmoPlugin.Instance?.LogSource.LogWarning("[sidecar] capture failed: " + e);
            return false;
        }
    }

    /// <summary>Loads the sidecar file; null when missing/corrupt.</summary>
    public static JsonObject? LoadRaw(string saveName)
    {
        try
        {
            var path = SidecarPathFor(saveName);
            if (!File.Exists(path)) return null;
            var json = Ungzip(File.ReadAllBytes(path));
            return json == null ? null : (JsonObject)JsonValue.Parse(json);
        }
        catch (Exception e)
        {
            MmoPlugin.Instance?.LogSource.LogWarning("[sidecar] read failed: " + e.Message);
            return null;
        }
    }

    /// <summary>Staged restore on the LIVE GamePlayer: rebuild the map first (guids
    /// must re-resolve), then re-point every map-referencing field from the sidecar
    /// (mirrors GamePlayer.LoadGalaxyReferences + the missions sections). Player-level
    /// fields are never touched. Yield-safe wrapper: the inner enumerator is stepped
    /// outside any try/catch (C# forbids yield inside try-with-catch).</summary>
    public static IEnumerator RestoreRoutine(string saveName, Action<bool> onDone)
    {
        // Custom iterator: steps RestoreInner frame-by-frame but catches throw
        // OUTSIDE any yield (C# forbids yield inside try-with-catch), so a throwing
        // stage reports a clean failure instead of killing the Unity coroutine.
        return new RestoreFlow(saveName, onDone);
    }

    private sealed class RestoreFlow : IEnumerator
    {
        private readonly string _name;
        private readonly Action<bool> _onDone;
        private readonly IEnumerator _inner;
        private bool _done;

        public RestoreFlow(string saveName, Action<bool> onDone)
        {
            _name = saveName; _onDone = onDone;
            _inner = RestoreInner(saveName);
        }
        public object? Current { get; private set; }
        public bool MoveNext()
        {
            if (_done) return false;
            try
            {
                var moved = _inner.MoveNext();
                Current = _inner.Current;
                if (!moved)
                {
                    _done = true;
                    _onDone?.Invoke(Current is bool b && b);
                }
                return moved;
            }
            catch (Exception e)
            {
                MmoPlugin.Instance?.LogSource.LogWarning("[sidecar] restore failed: " + e);
                _done = true;
                _onDone?.Invoke(false);
                return false;
            }
        }
        public void Reset() { }
        public void Dispose() { }
    }

    private static IEnumerator RestoreInner(string saveName)
    {
        var data = LoadRaw(saveName);
        if (data == null) { yield return false; yield break; }
        if (data["Version"].IsNull || GameVersion.IsFuture(data["Version"].AsString))
        {
            MmoPlugin.Instance?.LogSource.LogWarning($"[sidecar] restore refused: sidecar version '{data["Version"].AsString}' is newer than this game");
            yield return false; yield break;
        }
        var player = Source.Player.GamePlayer.current;
        if (player == null) { MmoPlugin.Instance?.LogSource.LogWarning("[sidecar] restore: no current player"); yield return false; yield break; }
        var p = data["Player"].AsJsonObject;
        if (p["map"].IsNull) { MmoPlugin.Instance?.LogSource.LogWarning("[sidecar] restore: sidecar has no map"); yield return false; yield break; }

        // 1) Map rebuild first — run to completion synchronously inside one step so
        // RestoreFlow's catch covers the whole staged load (no dangling coroutine).
        StagedRoutine.RunToEnd(player.map.FromJsonStaged(p["map"], null));
        // 2) Map-referencing fields — mirrors GamePlayer.LoadGalaxyReferences.
        player.routes.Load(p["routes"]);
        if (!p["capitalSystemGuid"].IsNull) typeof(Source.Player.GamePlayer).GetProperty("capitalSystemGuid")
            ?.SetValue(player, p["capitalSystemGuid"].AsString);
        player.currentSystem = player.map.GetSystem(p["currentSystem"].AsString);
        player.mapPosition = JsonUtil.JsonObjectToVector2(p["mapPosition"]);
        void LoadPoi(string key, GalaxyMapData.MapPOILoader set)
        {
            var v = p[key];
            if (v.IsNull) return;
            player.map.LoadPointOfInterest(v, set);
        }
        LoadPoi("lastMiningPOI", poi => player.lastVisitedMiningPOI = poi);
        LoadPoi("homeStation", poi => player.homeStation = poi as Source.Galaxy.POI.SpaceStation);
        LoadPoi("lastStation", poi => player.lastStation = poi as Source.Galaxy.POI.SpaceStation);
        LoadPoi("currentPointOfInterest", poi => player.currentPointOfInterest = poi);
        // Unlock flags have private setters — vanilla assigns them inside the class;
        // we set them via reflection to honor the same restore semantics.
        void SetFlag(string key, string prop)
        {
            var v = p[key];
            if (v.IsNull) return;
            var pi = typeof(Source.Player.GamePlayer).GetProperty(prop);
            pi?.SetValue(player, v.AsBoolean);
        }
        SetFlag("mapUnlocked", "mapUnlocked");
        SetFlag("autoPlayUnlocked", "autoPlayUnlocked");
        SetFlag("fastLaneTravelUnlocked", "fastLaneTravelUnlocked");
        SetFlag("wormholesUnlocked", "wormholesUnlocked");
        player.waypoints.Clear();
        if (p["waypoints"].IsJsonArray)
        {
            var idx = 0;
            foreach (var wp in p["waypoints"].AsJsonArray)
            {
                player.waypoints.Add(null);
                var captureIdx = idx++;
                player.map.LoadPointOfInterest(wp, poi => player.waypoints[captureIdx] = poi);
            }
        }
        // 3) Missions + mission-related sections (home-map references) — vanilla load path.
        player.missions.FromJsonArray(p["missions"].AsJsonArray, Source.MissionSystem.Mission.FromJson);
        // Active-contract refs tick EVERY FRAME (player.currentBounty?.Update()) and
        // hold live POI objects — they must be re-pointed or they dangle after the
        // map rebuild. Mirrors vanilla GamePlayer 2377-2385.
        if (!p["currentBounty"].IsNull)
            player.currentBounty = Source.MissionSystem.BountyMission.FromJson(p["currentBounty"].AsJsonObject);
        if (!p["currentPatrol"].IsNull)
            player.currentPatrol = Source.MissionSystem.PatrolMission.FromJson(p["currentPatrol"].AsJsonObject);
        if (!p["currentIndustry"].IsNull)
            player.currentIndustry = Source.MissionSystem.IndustryMission.FromJson(p["currentIndustry"].AsJsonObject);
        player.missionsArchive.FromJsonArray(p["missionsArchive"].AsJsonArray, v => v.AsString);
        if (p["pendingStationConstructions"].IsJsonArray)
            player.pendingStationConstructions.FromJsonArray(p["pendingStationConstructions"].AsJsonArray, Source.Spacestation.PendingStationConstruction.FromJson);
        yield return true;
    }

    /// <summary>Acceptance helper: canonical map JSON equality (sidecar vs live map).</summary>
    public static bool MapMatchesSidecar(string saveName)
    {
        var data = LoadRaw(saveName);
        var player = Source.Player.GamePlayer.current;
        if (data == null || player == null) return false;
        try
        {
            var a = Newtonsoft.Json.Linq.JToken.Parse(data["Player"]["map"].ToString());
            var b = Newtonsoft.Json.Linq.JToken.Parse(player.map.ToJson().ToString());
            return Newtonsoft.Json.Linq.JToken.DeepEquals(a, b);
        }
        catch (Exception e)
        {
            MmoPlugin.Instance?.LogSource.LogWarning("[sidecar] compare failed: " + e.Message);
            return false;
        }
    }

    private static byte[] Gzip(string text)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        using (var w = new StreamWriter(gz, new UTF8Encoding(false))) w.Write(text);
        return ms.ToArray();
    }

    private static string? Ungzip(byte[] raw)
    {
        try
        {
            using var ms = new MemoryStream(raw);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var r = new StreamReader(gz, Encoding.UTF8);
            return r.ReadToEnd();
        }
        catch { return null; }
    }
}
