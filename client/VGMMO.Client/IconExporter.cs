using System.IO;
using System.Collections.Generic;
using System.Linq;
using Behaviour.GalaxyMap;
using Behaviour.UI;
using Behaviour.UI.Tooltip;
using Source.Galaxy;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VGMMO.Client;

/// <summary>
/// Dev hook: exports the in-game galaxy-map POI icons (WorldMapPOI prefab sprites,
/// rendered in an isolated scene) to PNG, so the web UI can reuse the
/// exact same icons. Enable via config [test] exportIconsPath.
/// </summary>
public static class IconExporter
{
    private static readonly string[] Kinds =
    {
        "SpaceStation", "Mining", "Salvage", "JumpGate", "Wormhole",
    };

    public static void Export(string dir)
    {
        Scene exportScene = default;
        try
        {
            Directory.CreateDirectory(dir);
            exportScene = SceneManager.CreateScene("VGMMO Icon Export");
            // Keys exported from WorldMapPOI prefabs below; the PoiIconLibrary loop
            // skips these (the shipped library asset points every key at the same
            // red-X error placeholder — NOT the real map icons).
            var prefabExported = new HashSet<string>();
            foreach (var kind in Kinds)
            {
                try
                {
                    var prefab = WorldMapPOI.GetPrefab(kind);
                    if (prefab == null) { MmoPlugin.Instance?.LogSource.LogWarning($"[icons] no prefab for {kind}"); continue; }
                    var renderers = prefab.GetSpriteRenderers();
                    if (renderers.Count == 0) { MmoPlugin.Instance?.LogSource.LogWarning($"[icons] {kind}: no renderers on prefab"); continue; }
                    for (var i = 0; i < renderers.Count; i++)
                    {
                        var sprite = renderers[i].sprite;
                        if (sprite == null) { MmoPlugin.Instance?.LogSource.LogWarning($"[icons] {kind} r{i}: no sprite"); continue; }
                        MmoPlugin.Instance?.LogSource.LogInfo($"[icons] {kind} r{i}: sprite={sprite.name}, rect={sprite.rect}, bounds={sprite.bounds}, drawMode={renderers[i].drawMode}");
                        var png = CropPng(sprite, exportScene);
                        if (png == null) continue;
                        var path = Path.Combine(dir, kind + ".r" + i + ".png");
                        File.WriteAllBytes(path, png);
                        // The prefab's MAIN sprite (r0) IS the in-game map POI icon
                        // (Map_Poi_Location_*). Publish it as the canonical Poi_<kind>
                        // the web UI draws — the PoiIconLibrary asset is a placeholder.
                        if (renderers[i] == prefab.spriteRenderer)
                        {
                            var poiPath = Path.Combine(dir, "Poi_" + kind + ".png");
                            File.WriteAllBytes(poiPath, png);
                            prefabExported.Add(kind);
                            MmoPlugin.Instance?.LogSource.LogInfo($"[icons] canonical poi icon {kind} -> {poiPath} [{sprite.name}]");
                        }
                        MmoPlugin.Instance?.LogSource.LogInfo($"[icons] exported {kind} r{i} ({(renderers[i] == prefab.spriteRenderer ? "sprite" : renderers[i] == prefab.backgroundRenderer ? "background" : "other")}) -> {path} [{sprite.name}]");
                    }
                }
                catch (System.Exception e) { MmoPlugin.Instance?.LogSource.LogWarning($"[icons] {kind}: {e.Message}"); }
            }
            // The map uses FactionIconSet.mapIcon (tinySize, falling back to fullSize).
            // Names such as LuminateSmall are real emblems, not sprite sheets.
            var factionSets = Resources.FindObjectsOfTypeAll<FactionIconSet>();
            foreach (Faction faction in Faction.all)
            {
                try
                {
                    Sprite? sprite = FactionIconSet.Get(faction)?.mapIcon;
                    if (sprite == null)
                        sprite = factionSets.FirstOrDefault(set => set.name == faction.identifier && set.mapIcon != null)?.mapIcon;
                    if (sprite == null)
                    {
                        MmoPlugin.Instance?.LogSource.LogWarning($"[icons] faction {faction.identifier}: no map emblem");
                        continue;
                    }
                    var png = CropPng(sprite, exportScene);
                    if (png == null) continue;
                    var path = Path.Combine(dir, "Faction_" + faction.identifier + ".png");
                    File.WriteAllBytes(path, png);
                    MmoPlugin.Instance?.LogSource.LogInfo($"[icons] exported faction icon {faction.identifier} -> {path} (sprite '{sprite.name}')");
                }
                catch (System.Exception e) { MmoPlugin.Instance?.LogSource.LogWarning($"[icons] faction {faction.identifier}: {e.Message}"); }
            }
            // Remaining POI-type icons from PoiIconLibrary (tooltip keys like Ok,
            // Warning, Captain.*, ...) — only for keys NOT exported from prefabs above.
            // NOTE: the shipped library asset points most keys at a shared red-X error
            // placeholder, so anything that falls through here is not map-authentic.
            try
            {
                var lib = PoiIconLibrary.Instance;
                if (lib != null)
                {
                    foreach (var entry in lib.entries)
                    {
                        if (entry.sprite == null || prefabExported.Contains(entry.key)) continue;
                        var png = CropPng(entry.sprite, exportScene);
                        if (png == null) continue;
                        var key = entry.key.Replace('.', '_').Replace('/', '_');
                        var path = Path.Combine(dir, "Poi_" + key + ".png");
                        File.WriteAllBytes(path, png);
                        MmoPlugin.Instance?.LogSource.LogInfo($"[icons] exported poi icon {entry.key} -> {path} [{entry.sprite.name}]");
                    }
                }
                else MmoPlugin.Instance?.LogSource.LogWarning("[icons] PoiIconLibrary.Instance is null");
            }
            catch (System.Exception e) { MmoPlugin.Instance?.LogSource.LogWarning("[icons] poi library: " + e.Message); }

            // All exports happen in one frame: catch deferred-Destroy contamination
            // by rendering each main sprite again in reverse order after the batch.
            foreach (var kind in Kinds.Reverse())
            {
                if (!prefabExported.Contains(kind)) continue;
                var repeated = CropPng(WorldMapPOI.GetPrefab(kind).spriteRenderer.sprite, exportScene);
                if (!File.ReadAllBytes(Path.Combine(dir, "Poi_" + kind + ".png")).SequenceEqual(repeated))
                    throw new System.InvalidOperationException($"Render-order independence failed for {kind}");
            }
            MmoPlugin.Instance?.LogSource.LogInfo($"[icons] render-order independence passed for {prefabExported.Count} POI icons; done.");
        }
        catch (System.Exception e)
        {
            MmoPlugin.Instance?.LogSource.LogWarning("[icons] export failed: " + e.Message);
        }
        finally
        {
            if (exportScene.IsValid()) SceneManager.UnloadSceneAsync(exportScene);
        }
    }

    /// <summary>Renders the sprite mesh/UVs, including packed atlas rotation, without
    /// capturing the live game or other exports. Retains the existing 2% framing margin.</summary>
    private static byte[] CropPng(Sprite sprite, Scene exportScene)
    {
        var w = Mathf.Max(1, Mathf.RoundToInt(sprite.rect.width));
        var h = Mathf.Max(1, Mathf.RoundToInt(sprite.rect.height));
        GameObject? go = null;
        GameObject? camGo = null;
        Camera? cam = null;
        RenderTexture? rt = null;
        Texture2D? crop = null;
        var prevActive = RenderTexture.active;
        try
        {
            go = new GameObject("_vgmmo_icon");
            SceneManager.MoveGameObjectToScene(go, exportScene);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;

            camGo = new GameObject("_vgmmo_iconcam");
            SceneManager.MoveGameObjectToScene(camGo, exportScene);
            cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.scene = exportScene;
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 20f;
            cam.transform.position = sprite.bounds.center + Vector3.back * 10f;
            cam.orthographicSize = Mathf.Max(sprite.bounds.extents.y, sprite.bounds.extents.x / (w / (float)h)) * 1.02f;
            cam.aspect = w / (float)h;

            rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();

            crop = new Texture2D(w, h, TextureFormat.RGBA32, false);
            RenderTexture.active = rt;
            crop.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
            crop.Apply();
            return crop.EncodeToPNG();
        }
        finally
        {
            // Destroy is deferred until end-of-frame, but the next export renders now.
            if (go != null) go.SetActive(false);
            if (camGo != null) camGo.SetActive(false);
            RenderTexture.active = prevActive;
            if (cam != null) cam.targetTexture = null;
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
            if (crop != null) Object.Destroy(crop);
            if (camGo != null) Object.Destroy(camGo);
            if (go != null) Object.Destroy(go);
        }
    }
}
