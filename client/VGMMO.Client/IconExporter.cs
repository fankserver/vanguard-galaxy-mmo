using System.IO;
using Behaviour.GalaxyMap;
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
                    var sprite = renderers.Count > 0 ? renderers[0].sprite : null;
                    if (sprite == null) { MmoPlugin.Instance?.LogSource.LogWarning($"[icons] {kind}: no sprite on prefab"); continue; }

                    var tex = sprite.texture;
                    var rect = sprite.textureRect;
                    var w = Mathf.Max(1, (int)rect.width);
                    var h = Mathf.Max(1, (int)rect.height);

                    // Game textures are import-non-readable, so crop on the GPU: draw the
                    // atlas sub-rect into a RenderTexture, then ReadPixels into an RGBA
                    // texture (readable) and encode PNG.
                    var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                    var prevActive = RenderTexture.active;
                    RenderTexture.active = rt;
                    GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
                    var uv = new Rect(rect.x / tex.width, rect.y / tex.height,
                        rect.width / tex.width, rect.height / tex.height);
                    Graphics.DrawTexture(new Rect(0f, 0f, w, h), tex, uv, 0, 0, 0, 0, Color.white);
                    var crop = new Texture2D(w, h, TextureFormat.RGBA32, false);
                    crop.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
                    crop.Apply();
                    RenderTexture.active = prevActive;
                    RenderTexture.ReleaseTemporary(rt);
                    var png = crop.EncodeToPNG();
                    Object.Destroy(crop);
                    var path = Path.Combine(dir, kind + ".png");
                    File.WriteAllBytes(path, png);
                    MmoPlugin.Instance?.LogSource.LogInfo($"[icons] exported {kind} -> {path} ({w}x{h}, sprite '{sprite.name}')");
                }
                catch (System.Exception e) { MmoPlugin.Instance?.LogSource.LogWarning($"[icons] {kind}: {e.Message}"); }
            }
            MmoPlugin.Instance?.LogSource.LogInfo("[icons] done.");
        }
        catch (System.Exception e)
        {
            MmoPlugin.Instance?.LogSource.LogWarning("[icons] export failed: " + e.Message);
        }
    }
}
