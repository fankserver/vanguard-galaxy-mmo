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
    public string Name = "";
}

public sealed class WelcomeResponse
{
    public string SessionId = "";
    public string WorldId = "";
    public string WorldName = "";
    public long ServerTimeMs = 0;
    public string LayoutHash = "";
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
}

public sealed class SystemEnteredMessage
{
    public string SystemId = "";
}

public sealed class SystemLeftMessage
{
    public string SystemId = "";
}

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

/// <summary>Server relay of one player's last ship state to the same-system group.</summary>
public sealed class RemoteShipState
{
    public string SessionId = "";
    public string Name = "";
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
