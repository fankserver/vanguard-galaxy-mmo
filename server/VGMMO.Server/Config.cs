using Newtonsoft.Json;

namespace VGMMO.Server;

public sealed class ServerConfig
{
    public int Port { get; set; } = 34567;
    public int WebPort { get; set; } = 8080;
    public string DataDir { get; set; } = "data";
    public string WorldsDir { get; set; } = "worlds";
    public int MaxPeers { get; set; } = 64;
    public int MaxPlayersPerWorld { get; set; } = 32;
    /// <summary>Accepted client-build hashes. Empty list = accept any (dev mode).</summary>
    public List<string> AcceptedHashes { get; set; } = new();

    public static ServerConfig Load(string path)
    {
        if (!File.Exists(path)) return new ServerConfig();
        return JsonConvert.DeserializeObject<ServerConfig>(File.ReadAllText(path)) ?? new ServerConfig();
    }
}
