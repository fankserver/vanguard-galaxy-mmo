using System;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace VGMMO.Client;

[BepInPlugin(PluginId, "VGMMO Client", "0.1.0")]
public sealed class MmoPlugin : BaseUnityPlugin
{
    public const string PluginId = "vgmmo.client";

    internal static MmoPlugin? Instance;
    internal BepInEx.Logging.ManualLogSource LogSource = null!;
    internal MmoSession Session = null!;
    internal MmoUi Ui = null!;
    internal MmoWorldBuilder World = null!;

    public ConfigEntry<string> Host = null!;
    public ConfigEntry<int> Port = null!;
    public ConfigEntry<string> PlayerName = null!;
    public ConfigEntry<string> StartSystem = null!;
    public ConfigEntry<string> BuildHash = null!;
    public ConfigEntry<bool> AutoConnect = null!;
    public ConfigEntry<string> AutoLoadSlot = null!;
    public ConfigEntry<string> ExportIconsPath = null!;

    private void Awake()
    {
        Instance = this;
        LogSource = Logger;

        try
        {
            var harmony = new HarmonyLib.Harmony(PluginId);
            harmony.PatchAll();
            LogSource.LogInfo("vanilla travel patches applied");
        }
        catch (Exception e) { LogSource.LogWarning("travel patch failed: " + e.Message); }

        Host = Config.Bind("server", "host", "127.0.0.1", "VGMMO server address (Windows-side 127.0.0.1 reaches the WSL2 server via localhost forwarding).");
        Port = Config.Bind("server", "port", 34567, "VGMMO server UDP port.");
        PlayerName = Config.Bind("session", "name", "Player", "Display name sent in Hello.");
        StartSystem = Config.Bind("session", "system", "hub-1", "System id to join after Welcome (must exist in the server world).");
        BuildHash = Config.Bind("session", "clientBuildHash", "vgmmo-night-1", "Client build identity checked by the server's accepted-hashes gate.");
        AutoConnect = Config.Bind("session", "autoConnect", true, "Connect automatically on game start.");
        AutoLoadSlot = Config.Bind("test", "autoLoadSlot", "", "Test hook: load a save copy by slot name (Saves/<slot>.save) a few seconds after boot. Empty = disabled.");
        ExportIconsPath = Config.Bind("test", "exportIconsPath", "", "Dev hook: export galaxy-map POI icon PNGs to this folder at boot. Empty = disabled.");

        var go = new GameObject("VGMMO");
        DontDestroyOnLoad(go);
        Session = go.AddComponent<MmoSession>();
        Ui = go.AddComponent<MmoUi>();
        World = go.AddComponent<MmoWorldBuilder>();

        Logger.LogInfo("VGMMO client loaded (autoConnect=" + AutoConnect.Value + ")");
        if (AutoConnect.Value)
        {
            Session.Connect(Host.Value, Port.Value, PlayerName.Value, BuildHash.Value);
        }

        if (AutoLoadSlot.Value.Length > 0)
        {
            StartCoroutine(LoadTestSlot(AutoLoadSlot.Value));
        }

        if (ExportIconsPath.Value.Length > 0)
        {
            StartCoroutine(ExportIcons(ExportIconsPath.Value));
        }
    }

    private System.Collections.IEnumerator ExportIcons(string dir)
    {
        yield return new WaitForSeconds(2f);
        IconExporter.Export(dir);
    }

    private System.Collections.IEnumerator LoadTestSlot(string slot)
    {
        yield return new WaitForSeconds(3f);
        try
        {
            var path = System.IO.Path.Combine(Application.persistentDataPath, "Saves", slot + ".save");
            var info = new System.IO.FileInfo(path);
            if (!info.Exists) { LogSource.LogWarning("autoLoadSlot: not found " + path); yield break; }
            LogSource.LogInfo("autoLoadSlot: loading " + path);
            new Source.Util.SaveGameFile(info).LoadSaveGame();
        }
        catch (System.Exception e) { LogSource.LogError("autoLoadSlot failed: " + e); }
    }
}
