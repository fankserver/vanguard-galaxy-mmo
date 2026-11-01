using Newtonsoft.Json;

namespace VGMMO.Server;

public sealed class ServerConfig
{
    public int Port { get; set; } = 34567;
    public int WebPort { get; set; } = 8080;
    public string DataDir { get; set; } = "data";
    public string WorldsDir { get; set; } = "worlds";
    public string AssetsDir { get; set; } = "assets";
    public int MaxPeers { get; set; } = 64;
    public int MaxPlayersPerWorld { get; set; } = 32;
    /// <summary>Test mode: maintain a 'shadow' copy of a real in-cluster player at a
    /// small offset and relay it to the whole system group (including the source),
    /// so ship state/direction/thrust can be verified with one client.</summary>
    public bool ShadowGhost { get; set; } = true;
    /// <summary>Number of simulated pilots (see Core.GhostPilot) that patrol the hosted
    /// systems; 0 disables. They appear on the web map and in in-game mirrors. Test
    /// scaffolding — shipped default 0.</summary>
    public int GhostPilots { get; set; } = 0;
    /// <summary>Accepted client-build hashes. Empty list = accept any (dev mode).</summary>
    public List<string> AcceptedHashes { get; set; } = new();

    public static ServerConfig Load(string path)
    {
        if (!File.Exists(path)) return new ServerConfig();
        return JsonConvert.DeserializeObject<ServerConfig>(File.ReadAllText(path)) ?? new ServerConfig();
    }
}
