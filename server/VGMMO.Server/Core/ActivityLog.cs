using Newtonsoft.Json.Linq;

namespace VGMMO.Server.Core;

public sealed class ActivityLog
{
    public sealed record Entry(long Id, long TMs, string Kind, string Text, string SystemId);

    private readonly List<Entry> _entries = new();
    private readonly int _cap;
    private long _nextId = 1;

    public ActivityLog(int cap = 500) => _cap = cap;

    public long Add(string kind, string text, string systemId = "")
    {
        var e = new Entry(_nextId++, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), kind, text, systemId);
        lock (_entries)
        {
            _entries.Add(e);
            if (_entries.Count > _cap) _entries.RemoveRange(0, _entries.Count - _cap);
        }
        return e.Id;
    }

    public List<Entry> Tail(int count)
    {
        lock (_entries)
        {
            var start = Math.Max(0, _entries.Count - count);
            return _entries.GetRange(start, _entries.Count - start);
        }
    }

    public List<Entry> Since(long id, int max = 500)
    {
        lock (_entries)
        {
            return _entries.Where(e => e.Id > id).Take(max).ToList();
        }
    }
}
