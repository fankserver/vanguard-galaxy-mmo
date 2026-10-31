using System.IO;
using Behaviour.GalaxyMap;
using Behaviour.UI;
using Behaviour.UI.Tooltip;
using Source.Galaxy;
using UnityEngine;

namespace VGMMO.Client;

/// <summary>
/// Dev hook: exports the in-game galaxy-map POI icons (WorldMapPOI prefab sprites,
/// cropped from their atlas via textureRect) to PNG, so the web UI can reuse the
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
        try
        {
            Directory.CreateDirectory(dir);
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
                        var png = CropPng(sprite);
                        if (png == null) continue;
                        var path = Path.Combine(dir, kind + ".r" + i + ".png");
                        File.WriteAllBytes(path, png);
                        MmoPlugin.Instance?.LogSource.LogInfo($"[icons] exported {kind} r{i} ({(renderers[i] == prefab.spriteRenderer ? "sprite" : renderers[i] == prefab.backgroundRenderer ? "background" : "other")}) -> {path} [{sprite.name}]");
                    }
                }
                catch (System.Exception e) { MmoPlugin.Instance?.LogSource.LogWarning($"[icons] {kind}: {e.Message}"); }
            }
            // Real map icons: per-faction (FactionIconSet.mapIcon — what the in-game
            // galaxy map actually draws at POIs). The WorldMapPOI spriteRenderer is only
            // the location plate, which is NOT the icon.
            foreach (Faction faction in Faction.all)
            {
                try
                {
                    var set = FactionIconSet.Get(faction);
                    var sprite = set?.mapIcon;
                    if (sprite == null) continue;
                    var png = CropPng(sprite);
                    if (png == null) continue;
                    var path = Path.Combine(dir, "Faction_" + faction.identifier + ".png");
                    File.WriteAllBytes(path, png);
                    MmoPlugin.Instance?.LogSource.LogInfo($"[icons] exported faction icon {faction.identifier} -> {path} (sprite '{sprite.name}')");
                }
                catch (System.Exception e) { MmoPlugin.Instance?.LogSource.LogWarning($"[icons] faction {faction.identifier}: {e.Message}"); }
            }
            // THE POI-type icons: PoiIconLibrary (keyed, e.g. 'SpaceStation', 'Mining',
            // 'Salvage', 'JumpGate') — what the game draws at POIs (tooltipIconKey).
            try
            {
                var lib = PoiIconLibrary.Instance;
                if (lib != null)
                {
                    foreach (var entry in lib.entries)
                    {
                        if (entry.sprite == null) continue;
                        var png = CropPng(entry.sprite); // POI icons: raw sub-rect, 1:1
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

            MmoPlugin.Instance?.LogSource.LogInfo("[icons] done.");
        }
        catch (System.Exception e)
        {
            MmoPlugin.Instance?.LogSource.LogWarning("[icons] export failed: " + e.Message);
        }
    }

    /// <summary>Extracts one sprite from its atlas (the game's textures are packed, so
    /// the sub-rect MUST be cropped) at raw 1:1 pixels — no trimming, no resampling.
    /// Returns null on failure.</summary>
    private static byte[]? CropPng(Sprite sprite)
    {
        var tex = sprite.texture;
        var rect = sprite.textureRect;
        var w = Mathf.Max(1, (int)rect.width);
        var h = Mathf.Max(1, (int)rect.height);
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
        var prevActive = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
        var uv = new Rect(rect.x / tex.width, rect.y / tex.height, rect.width / tex.width, rect.height / tex.height);
        Graphics.DrawTexture(new Rect(0f, 0f, w, h), tex, uv, 0, 0, 0, 0, Color.white);
        var crop = new Texture2D(w, h, TextureFormat.RGBA32, false);
        crop.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
        crop.Apply();
        RenderTexture.active = prevActive;
        RenderTexture.ReleaseTemporary(rt);
        var png = crop.EncodeToPNG();
        Object.Destroy(crop);
        return png;
    }
}
