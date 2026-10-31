using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace VGMMO.Protocol;

/// <summary>Wire protocol version. Bumped on any incompatible change.</summary>
public static class ProtocolInfo
{
    public const int Version = 1;
    public const string GameName = "vanguard-galaxy";
}

/// <summary>Message type discriminator values (wire strings).</summary>
public static class MsgTypes
{
    public const string Hello = "hello";
    public const string Welcome = "welcome";
    public const string Refused = "refused";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string JoinWorld = "join-world";
    public const string LeaveWorld = "leave-world";
    public const string SystemEntered = "system-entered";
    public const string SystemLeft = "system-left";
    public const string ShipState = "ship-state";
    public const string RemoteShipState = "remote-ship-state";
    public const string EntitySnapshot = "entity-snapshot";
    public const string Event = "event";
    public const string LootSpawn = "loot-spawn";
    public const string LootTaken = "loot-taken";
    public const string LootGone = "loot-gone";
    public const string TradeOffer = "trade-offer";
    public const string TradeAnswer = "trade-answer";
    public const string TradeResult = "trade-result";
}

/// <summary>Envelope: every frame carries a type discriminator and one payload object.</summary>
public sealed class Envelope
{
    [JsonProperty("t")] public string Type = "";
    [JsonProperty("msg")] public JObject? Msg;
    [JsonIgnore] public byte[]? Raw;
}

public static class Wire
{
    public static readonly JsonSerializerSettings Settings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None,
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
    };

    public static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);

    /// <summary>Serialize one message into an envelope JSON (UTF-8).</summary>
    public static byte[] Encode(string type, object? msg)
    {
        var env = new Envelope { Type = type, Msg = msg == null ? null : JObject.FromObject(msg, Serializer) };
        return EncodingUtf8(JsonConvert.SerializeObject(env, Settings));
    }

    public static Envelope Decode(byte[] frame)
    {
        var obj = JObject.Parse(System.Text.Encoding.UTF8.GetString(frame));
        var env = new Envelope
        {
            Type = obj.Value<string>("t") ?? "",
            Msg = obj["msg"] as JObject,
        };
        return env;
    }

    public static T? Read<T>(Envelope env) where T : class
        => env.Msg == null ? null : env.Msg.ToObject<T>(Serializer);

    private static byte[] EncodingUtf8(string s) => System.Text.Encoding.UTF8.GetBytes(s);
}

// ---- Messages (shared by client and server) ----

public sealed class HelloRequest
{
    public int Protocol = ProtocolInfo.Version;
    public string GameVersion = "";
    public string ClientBuildHash = "";
    public string Name = "";          // nickname (MMO handle) — what the web UI shows
    public string FullName = "";      // captain GetFullName() from the save (mirror display)
    public string Title = "";         // selected title display name (mirror display)
    public string TitleColor = "";    // hex color of the title
}

public sealed class WelcomeResponse
{
    public string SessionId = "";
    public string WorldId = "";
    public string WorldName = "";
    public long ServerTimeMs = 0;
    public string LayoutHash = "";
    /// <summary>Hosted systems: storyId -> system id. A client is "in the MMO cluster"
    /// when its current in-game system's storyId is in here.</summary>
    public List<WelcomeSystem> Systems { get; set; } = new();
    /// <summary>Full authored world layout — the client materializes it in-game (M1).</summary>
    public WorldLayout? World { get; set; }
}

public sealed class WelcomeSystem
{
    public string Id = "";
    public string StoryId = "";
}

// ---- Authored world layout (mirrors server worlds/*.json; pure data) ----

public sealed class WorldLayout
{
    public string Id = "";
    public string Name = "";
    public List<LayoutSystem> Systems { get; set; } = new();
    public List<LayoutLink> Links { get; set; } = new();
    public LayoutEntry? Entry { get; set; }
}

public sealed class LayoutSystem
{
    public string Id = "";
    public string StoryId = "";
    public string Name = "";
    public float X;
    public float Y;
    public int Level = 1;
    public string Faction = "";
    public List<LayoutPoi> Pois { get; set; } = new();
}

public sealed class LayoutPoi
{
    public string Id = "";
    public string Kind = "";
    public string Name = "";
    public float X;
    public float Y;
    public string Faction = "";
    public int Level = 1;
}

public sealed class LayoutLink
{
    public string A = "";
    public string B = "";
    public string Name = "Gate";
    public bool SectorGate;
    /// <summary>Gate POI local positions (server-authoritative; client replicates verbatim).</summary>
    public float PosAX;
    public float PosAY;
    public float PosBX;
    public float PosBY;
}

/// <summary>Entry-door spec (server-authoritative): a gate pair from the joining player's
/// current vanilla system into the world. The hub-side position is server-fixed; the
/// origin-side position is the single replicated anchor-seam formula on the client.</summary>
public sealed class LayoutEntry
{
    public string TargetSystemId = "";
    public string Name = "MMO Gate";
    public bool SectorGate = true;
    public bool Unlocked = true;
    public float HubX;
    public float HubY;
}

public sealed class RefusedResponse
{
    public string Reason = "";
}

public sealed class PingMessage
{
    public long T = 0;
}

public sealed class JoinWorldRequest
{
    public string SystemId = "";
    public string Name = "";           // nickname (callsign) — refreshes the session name
    // Identity refresh on cluster entry (authoritative — the commander is loaded by then):
    public string FullName = "";
    public string Title = "";
    public string TitleColor = "";
}

public sealed class SystemEnteredMessage
{
    public string SystemId = "";
}

public sealed class SystemLeftMessage
{
    public string SystemId = "";
}

/// <summary>Client ship state (in-cluster only). COORDINATE FRAME (applies to
/// everything in this protocol): positions are SYSTEM-LOCAL layout units — the game
/// world divides by MapPointOfInterest.mapToLocalConversion (250) before sending.
/// The web renders them as system-origin + local; servers never add an origin.</summary>
public sealed class ShipStateMessage
{
    public string SystemId = "";
    public float X;
    public float Y;
    public float Rot;
    public float Vx;
    public float Vy;
    public uint Seq;
}

/// <summary>Server relay of one player's last ship state to the same-system group.
/// Positions are system-local layout units (see ShipStateMessage).</summary>
public sealed class RemoteShipState
public sealed class RemoteShipState
{
    public string SessionId = "";
    public string Name = "";          // nickname
    public string FullName = "";      // captain name from the save (mirror display)
    public string Title = "";         // selected title display name
    public string TitleColor = "";    // hex color of the title
    public string SystemId = "";
    public float X;
    public float Y;
    public float Rot;
    public float Vx;
    public float Vy;
    public uint Seq;
    public long Ts = 0;
}

/// <summary>Sim-host relay of a simulated entity (NPC/ambient ship) state.</summary>
public sealed class EntitySnapshot
{
    public string SystemId = "";
    public string EntityId = "";
    public string ClassId = "";
    public float X;
    public float Y;
    public float Rot;
    public float Vx;
    public float Vy;
    public float HpFrac = 1f;
    public uint Flags;
    public uint Seq;
}

public sealed class EventMessage
{
    public uint Seq;
    public string SystemId = "";
    public string Command = "";
    public JObject? Args;
}

public sealed class LootSpawnMessage
{
    public string InstanceId = "";
    public string SystemId = "";
    public string ItemId = "";
    public int Amount = 1;
    public float X;
    public float Y;
    public string OwnerFaction = "";
    public bool KeepUntilCollected = true;
    public string? ProducerGuid;
}

public sealed class LootTakenMessage
{
    public string InstanceId = "";
    public string Taker = "";
}

public sealed class LootGoneMessage
{
    public string InstanceId = "";
    public string Taker = "";
}

public sealed class ItemQty
{
    public string ItemId = "";
    public int Count = 1;
}

public sealed class TradeOfferMessage
{
    public string TradeId = "";
    public string From = "";
    public string To = "";
    public List<ItemQty> Offer = new();
    public List<ItemQty> Want = new();
}

public sealed class TradeAnswerMessage
{
    public string TradeId = "";
    public bool Accepted;
}

public sealed class TradeResultMessage
{
    public string TradeId = "";
    public bool Ok;
    public string Reason = "";
}
