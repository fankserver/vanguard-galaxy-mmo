using VGMMO.Protocol;
namespace VGMMO.Server.Ledger;

/// <summary>
/// Server-arbitrated state: loot instances, per-player cargo ledger and trades.
/// Currency/quantities are plain integers keyed by opaque item ids.
/// </summary>
public sealed class LedgerStore
{
    public sealed class LootEntry
    {
        public string InstanceId = "";
        public string SystemId = "";
        public string ItemId = "";
        public int Amount = 1;
        public float X;
        public float Y;
        public string OwnerFaction = "";
        public string? Taker;
        public long SpawnedAtMs;
    }

    private readonly Dictionary<string, LootEntry> _loot = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _cargo = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TradeOfferMessage> _pending = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, LootEntry> Loot => _loot;
    public IReadOnlyDictionary<string, Dictionary<string, int>> Cargo => _cargo;

    public event Action<string>? Changed; // fired on any state mutation (persistence dirtiness)

    public LootEntry Spawn(Protocol.LootSpawnMessage msg)
    {
        var id = string.IsNullOrEmpty(msg.InstanceId) ? Guid.NewGuid().ToString("N") : msg.InstanceId;
        var entry = new LootEntry
        {
            InstanceId = id, SystemId = msg.SystemId, ItemId = msg.ItemId, Amount = Math.Max(1, msg.Amount),
            X = msg.X, Y = msg.Y, OwnerFaction = msg.OwnerFaction, SpawnedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        _loot[id] = entry;
        Changed?.Invoke("loot-spawn");
        return entry;
    }

    /// <summary>Take loot; moves the item into the taker's cargo. Returns success.</summary>
    public bool Take(string instanceId, string taker)
    {
        if (!_loot.TryGetValue(instanceId, out var e) || e.Taker != null) return false;
        e.Taker = taker;
        Add(taker, e.ItemId, e.Amount);
        Changed?.Invoke("loot-take");
        return true;
    }

    public void Add(string player, string itemId, int count)
    {
        if (!_cargo.TryGetValue(player, out var inv)) _cargo[player] = inv = new Dictionary<string, int>(StringComparer.Ordinal);
        inv[itemId] = inv.GetValueOrDefault(itemId) + count;
    }

    public bool Has(string player, string itemId, int count)
        => _cargo.TryGetValue(player, out var inv) && inv.GetValueOrDefault(itemId) >= count;

    public bool Remove(string player, string itemId, int count)
    {
        if (!Has(player, itemId, count)) return false;
        var inv = _cargo[player];
        inv[itemId] -= count;
        if (inv[itemId] <= 0) inv.Remove(itemId);
        return true;
    }

    /// <summary>Two-phase trade: accept moves offer and want atomically. Returns (ok, reason).</summary>
    public (bool ok, string reason) ResolveTrade(string tradeId, Protocol.TradeOfferMessage offer, bool accepted)
    {
        if (!accepted) { _pending.Remove(tradeId); return (false, "declined"); }
        foreach (var i in offer.Offer)
            if (!Has(offer.From, i.ItemId, i.Count)) return (false, $"offerer lacks {i.ItemId}");
        foreach (var i in offer.Want)
            if (!Has(offer.To, i.ItemId, i.Count)) return (false, $"receiver lacks {i.ItemId}");
        foreach (var i in offer.Offer) Remove(offer.From, i.ItemId, i.Count);
        foreach (var i in offer.Want) Remove(offer.To, i.ItemId, i.Count);
        foreach (var i in offer.Offer) Add(offer.To, i.ItemId, i.Count);
        foreach (var i in offer.Want) Add(offer.From, i.ItemId, i.Count);
        _pending.Remove(tradeId);
        Changed?.Invoke("trade");
        return (true, "");
    }

    public Protocol.TradeOfferMessage? GetPending(string tradeId)
        => _pending.TryGetValue(tradeId, out var t) ? t : null;

    public void PutPending(Protocol.TradeOfferMessage offer) => _pending[offer.TradeId] = offer;

    // ---- Persistence (plain data transfer) ----

    public sealed class Snapshot
    {
        public List<LootEntry> Loot { get; set; } = new();
        public Dictionary<string, Dictionary<string, int>> Cargo { get; set; } = new();
    }

    public Snapshot Capture() => new() { Loot = _loot.Values.ToList(), Cargo = _cargo.ToDictionary(kv => kv.Key, kv => new Dictionary<string, int>(kv.Value)) };

    public void Restore(Snapshot snap)
    {
        _loot.Clear(); _cargo.Clear();
        foreach (var e in snap.Loot) _loot[e.InstanceId] = e;
        foreach (var kv in snap.Cargo) _cargo[kv.Key] = new Dictionary<string, int>(kv.Value);
    }
}
