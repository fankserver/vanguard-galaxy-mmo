using VGMMO.Server.Content;

namespace VGMMO.Server.Core;

/// <summary>
/// World event timeline: fires authored entries once each, at (server-start + at) seconds,
/// in timeline order, with a per-world monotonic sequence. Cursor is persisted so a server
/// restart resumes exactly where it left off.
/// </summary>
public sealed class EventPlanner
{
    public sealed class Snapshot
    {
        public Dictionary<string, int> NextEntry { get; set; } = new();
    }

    private readonly ServerCore _core;
    private readonly Dictionary<string, int> _nextEntry = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint> _seq = new(StringComparer.Ordinal);

    public EventPlanner(ServerCore core) => _core = core;

    public void Restore(Snapshot? snap)
    {
        if (snap == null) return;
        foreach (var kv in snap.NextEntry) _nextEntry[kv.Key] = kv.Value;
    }

    public Snapshot Capture() => new() { NextEntry = new Dictionary<string, int>(_nextEntry) };

    /// <summary>Test hook: rewind a world's timeline (clock + entry index) so a
    /// scheduled event can be observed deterministically after a session joined.</summary>
    public void ResetWorldClock(string worldId)
    {
        if (!_core.Worlds().TryGetValue(worldId, out var w))
        {
            Console.WriteLine($"ResetWorldClock: world '{worldId}' not found (test API misuse)");
            return;
        }
        _nextEntry[worldId] = 0;
        w.StartedAtMs = ServerCore.NowMs();
    }

    public void Tick()
    {
        var nowMs = ServerCore.NowMs();
        foreach (var (worldId, world) in _core.Worlds())
        {
            var startMs = world.StartedAtMs != 0 ? world.StartedAtMs : ServerCore.NowMs();
            if (world.StartedAtMs == 0) { world.StartedAtMs = startMs; }

            var index = _nextEntry.GetValueOrDefault(worldId);
            while (index < world.Timeline.Count)
            {
                var entry = world.Timeline[index];
                if (nowMs - startMs < entry.At * 1000) break;
                var seq = _seq.GetValueOrDefault(worldId) + 1;
                _seq[worldId] = seq;
                _core.FirePlannedEvent(world, entry, seq);
                _nextEntry[worldId] = ++index;
                _core.MarkDirty();
            }
        }
    }
}
