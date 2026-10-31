using Newtonsoft.Json;

namespace VGMMO.Server.Content;

/// <summary>Authored world definition (worlds/*.json). Content-only: ids are opaque strings.</summary>
public sealed class WorldDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<SystemDef> Systems { get; set; } = new();
    public List<LinkDef> Links { get; set; } = new();
    public List<TimelineDef> Timeline { get; set; } = new();
    public EntryDef? Entry { get; set; }

    [JsonIgnore] public long StartedAtMs;

    public string ComputeLayoutHash()
    {
        // Stable hash over the FULL authored layout: ids, positions, levels, POIs
        // (incl. level + payloads), links, entry door and timeline — everything the
        // client materializes, so any authored change is detected by the baker.
        var sb = new System.Text.StringBuilder();
        void W(string s) { sb.Append(s.Length).Append(':').Append(s).Append('|'); }
        W(Id); W(Name);
        foreach (var s in Systems.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            W(s.Id); W(s.StoryId); W(s.Name); W(s.X.ToString("R")); W(s.Y.ToString("R"));
            W(s.Level.ToString()); W(s.Faction);
            foreach (var p in s.Pois.OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                W(p.Id); W(p.Kind); W(p.Name); W(p.X.ToString("R")); W(p.Y.ToString("R")); W(p.Faction);
                W(p.Level.ToString());
            }
            foreach (var pay in s.Payloads)
            {
                W(pay.ShipClass); W(pay.Faction); W(pay.Count.ToString()); W(pay.Rank); W(pay.AutoActions);
            }
        }
        foreach (var l in Links.OrderBy(l => l.A, StringComparer.Ordinal).ThenBy(l => l.B, StringComparer.Ordinal))
        {
            W(l.A); W(l.B);
        }
        if (Entry != null)
        {
            W(Entry.Name); W(Entry.TargetSystemId); W(Entry.SectorGate.ToString()); W(Entry.Unlocked.ToString());
            W(Entry.HubX.ToString("R")); W(Entry.HubY.ToString("R"));
        }
        foreach (var t in Timeline.OrderBy(t => t.At))
        {
            W(t.At.ToString("R")); W(t.SystemId); W(t.Command);
            if (t.Args != null)
                foreach (var kv in t.Args.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                { W(kv.Key); W(kv.Value?.ToString() ?? ""); }
        }
        return System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()))
            .Aggregate(new System.Text.StringBuilder(), (b, x) => b.Append(x.ToString("x2"))).ToString();
    }
}

public sealed class SystemDef
{
    public string Id { get; set; } = "";
    public string StoryId { get; set; } = "";
    public string Name { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public int Level { get; set; } = 1;
    public string Faction { get; set; } = "";
    public List<PoiDef> Pois { get; set; } = new();
    public List<PayloadDef> Payloads { get; set; } = new();
}

public sealed class PoiDef
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public string Faction { get; set; } = "";
    public int Level { get; set; } = 1;
}

public sealed class PayloadDef
{
    public string ShipClass { get; set; } = "";
    public string Faction { get; set; } = "";
    public int Count { get; set; } = 1;
    public string Rank { get; set; } = "";
    public string AutoActions { get; set; } = "Combat";
}

public sealed class LinkDef
{
    public string A { get; set; } = "";
    public string B { get; set; } = "";
}

public sealed class EntryDef
{
    public string TargetSystemId { get; set; } = "";
    public string Name { get; set; } = "MMO Gate";
    public bool SectorGate { get; set; } = true;
    public bool Unlocked { get; set; } = true;
    public float HubX { get; set; }
    public float HubY { get; set; }
}

public sealed class TimelineDef
{
    /// <summary>Seconds after world start.</summary>
    public double At { get; set; }
    public string SystemId { get; set; } = "";
    public string Command { get; set; } = "";
    public System.Collections.Generic.Dictionary<string, object>? Args { get; set; }
}

public static class WorldLoader
{
    public static List<WorldDefinition> LoadAll(string worldsDir)
    {
        var result = new List<WorldDefinition>();
        if (!Directory.Exists(worldsDir)) return result;
        foreach (var file in Directory.GetFiles(worldsDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var world = JsonConvert.DeserializeObject<WorldDefinition>(File.ReadAllText(file));
            if (world == null) throw new InvalidDataException($"world {file}: unreadable");
            Validate(world, file);
            result.Add(world);
        }
        return result;
    }

    public static void Validate(WorldDefinition world, string source)
    {
        if (string.IsNullOrWhiteSpace(world.Id)) throw new InvalidDataException($"{source}: world id required");
        if (string.IsNullOrWhiteSpace(world.Name)) world.Name = world.Id;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in world.Systems)
        {
            if (string.IsNullOrWhiteSpace(s.Id)) throw new InvalidDataException($"{source}: system id required");
            if (!ids.Add(s.Id)) throw new InvalidDataException($"{source}: duplicate system id {s.Id}");
            foreach (var p in s.Pois)
                if (string.IsNullOrWhiteSpace(p.Id)) throw new InvalidDataException($"{source}: POI id required in {s.Id}");
        }
        foreach (var l in world.Links)
        {
            if (!ids.Contains(l.A) || !ids.Contains(l.B))
                throw new InvalidDataException($"{source}: link {l.A}<->{l.B} references unknown system");
        }
        foreach (var t in world.Timeline)
            if (!ids.Contains(t.SystemId)) throw new InvalidDataException($"{source}: timeline targets unknown system {t.SystemId}");
        if (world.Entry != null && !ids.Contains(world.Entry.TargetSystemId))
            throw new InvalidDataException($"{source}: entry targets unknown system {world.Entry.TargetSystemId}");
    }
}
