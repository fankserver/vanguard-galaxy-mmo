using Newtonsoft.Json;
using VGMMO.Server.Ledger;

namespace VGMMO.Server.Persistence;

/// <summary>
/// Server world state persistence: atomic JSON snapshot of the ledger and the event
/// planner cursor. Written every 60 s and on shutdown.
/// </summary>
public sealed class StateStore
{
    public sealed class WorldState
    {
        public LedgerStore.Snapshot Ledger { get; set; } = new();
        public Core.EventPlanner.Snapshot Planner { get; set; } = new();
    }

    private readonly string _dir;
    private readonly string _path;

    public StateStore(string dataDir)
    {
        _dir = dataDir;
        _path = Path.Combine(dataDir, "state.json");
    }

    public WorldState? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonConvert.DeserializeObject<WorldState>(File.ReadAllText(_path));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[state] load failed: {e.Message}");
            return null;
        }
    }

    public void Save(WorldState state)
    {
        Directory.CreateDirectory(_dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(state, Formatting.Indented));
        File.Move(tmp, _path, overwrite: true);
    }
}
