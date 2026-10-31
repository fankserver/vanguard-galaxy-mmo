using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using Newtonsoft.Json;
using VGMMO.Protocol;
using VGMMO.Server.Core;
using VGMMO.Server.Transport;
using VGMMO.Server.Web;

namespace VGMMO.Server;

/// <summary>
/// In-process integration test: real UDP transport, real sessions/worlds/ledger/planner,
/// real web endpoints — two scripted clients over loopback. Prints PASS/FAIL per check.
/// </summary>
public static class SelfTest
{
    private sealed class TestClient : IDisposable
    {
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _manager;
        private readonly List<Envelope> _inbox = new();
        private NetPeer? _peer;
        public NetPeer? Peer => _peer;

        public TestClient()
        {
            _manager = new NetManager(_listener) { UpdateTime = 15 };
            _listener.PeerConnectedEvent += p => _peer = p;
            _listener.NetworkReceiveEvent += (p, r, ch, del) =>
            {
                var frame = GameServer.TryReadFrame(r);
                if (frame != null) lock (_inbox) _inbox.Add(Wire.Decode(frame));
            };
        }

        public void Connect(int port) { _manager.Start(); _manager.Connect(new IPEndPoint(IPAddress.Loopback, port), "vgmmo"); }

        public void Poll(int ms)
        {
            var until = Environment.TickCount64 + ms;
            do { _manager.PollEvents(); Thread.Sleep(2); } while (Environment.TickCount64 < until);
        }

        public Envelope? Receive(string type, int timeoutMs)
        {
            var until = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < until)
            {
                _manager.PollEvents();
                Thread.Sleep(2);
                // Scan the whole queue: never discard messages of other types while
                // waiting for this one (e.g. timeline events may arrive mid-check).
                lock (_inbox)
                {
                    var idx = _inbox.FindIndex(e => e.Type == type);
                    if (idx >= 0) { var match = _inbox[idx]; _inbox.RemoveAt(idx); return match; }
                }
            }
            return null;
        }

        public void Send(string type, object msg, bool reliable = true)
        {
            var frame = Wire.Encode(type, msg);
            var w = new NetDataWriter();
            w.Put(frame.Length); w.Put(frame);
            _peer?.Send(w, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
        }

        public void Dispose() { _manager.Stop(); }
    }

    private static readonly List<string> Failures = new();
    private static int _checks;

    private static void Check(string name, bool ok, string? detail = null)
    {
        _checks++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(ok ? "" : " — " + detail)}");
        if (!ok) Failures.Add(name + (detail is null ? "" : " (" + detail + ")"));
    }

    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "vgmmo-st-" + Guid.NewGuid().ToString("N")[..8]);
        var worldsDir = Path.Combine(root, "worlds");
        var dataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(worldsDir);
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(worldsDir, "demo.json"), TestWorldJson);

        var cfg = new ServerConfig
        {
            Port = 0, WebPort = FreePort(), WorldsDir = worldsDir, DataDir = dataDir,
            AcceptedHashes = new List<string> { "test-build-1" },
        };
        var core = new ServerCore(cfg);
        core.Init();
        var transport = new GameServer(core);
        core.AttachTransport(transport);
        transport.Start(0);
        var web = new WebUi(core);
        await web.StartAsync(cfg.WebPort);

        // The real server loop: poll transport + tick continuously (mirrors Program.Main).
        using var keepAlive = new CancellationTokenSource();
        var serverLoop = Task.Run(async () =>
        {
            while (!keepAlive.IsCancellationRequested)
            {
                transport.Poll();
                core.Tick();
                await Task.Delay(5);
            }
        });

        Console.WriteLine($"self-test: udp {transport.Port}, web {web.Port}");
        try
        {
            using var alice = new TestClient();
            using var bob = new TestClient();
            using var bad = new TestClient();
            var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{web.Port}") };

            // C1: good hello -> Welcome
            alice.Connect(transport.Port);
            alice.Poll(600);
            alice.Send(MsgTypes.Hello, new HelloRequest { Name = "Alice", GameVersion = "0.9.x-test", ClientBuildHash = "test-build-1" });
            var welcome = alice.Receive(MsgTypes.Welcome, 3000);
            Check("hello/welcome", welcome != null && welcome.Msg?["sessionId"] != null);
            core.Tick(); transport.Poll();

            // C2: wrong build -> Refused
            bad.Connect(transport.Port);
            bad.Poll(300);
            bad.Send(MsgTypes.Hello, new HelloRequest { Name = "Bad", ClientBuildHash = "other-build" });
            var refused = bad.Receive(MsgTypes.Refused, 3000);
            Check("hello/build-gate", refused != null && (refused.Msg?["reason"]?.ToString() ?? "").Contains("build"));
            bad.Dispose();

            // C3: join hub -> SystemEntered; server knows player
            alice.Send(MsgTypes.JoinWorld, new JoinWorldRequest { SystemId = "hub-1" });
            Check("join/system-entered", alice.Receive(MsgTypes.SystemEntered, 2000) != null);
            Check("core-session", core.ActivePlayerCount() == 1);
            // Presence gate: a connected-but-not-in-cluster client is NOT "available".
            Check("presence-gate", core.ActivePlayerCount() == 1 && core.ConnectedCount() == 1);

            // C4: bob joins same system
            bob.Connect(transport.Port);
            bob.Poll(300);
            bob.Send(MsgTypes.Hello, new HelloRequest { Name = "Bob", GameVersion = "0.9.x-test", ClientBuildHash = "test-build-1" });
            Check("bob/welcome", bob.Receive(MsgTypes.Welcome, 3000) != null);
            bob.Send(MsgTypes.JoinWorld, new JoinWorldRequest { SystemId = "hub-1" });
            Check("bob/system-entered", bob.Receive(MsgTypes.SystemEntered, 2000) != null);

            // C5: alice ship state -> bob receives relay
            alice.Send(MsgTypes.ShipState, new ShipStateMessage { SystemId = "hub-1", X = 1.5f, Y = -2f, Rot = 0.25f, Vx = 0.1f, Vy = 0f, Seq = 7 }, reliable: false);
            var relayed = bob.Receive(MsgTypes.RemoteShipState, 2500);
            Check("ship-state relay", relayed != null && (relayed.Msg?["name"]?.ToString() ?? "") == "Alice"
                && Math.Abs((float)(relayed.Msg?["x"] ?? 0f) - 1.5f) < 0.001f);

            // C6: loot spawn broadcast
            alice.Send(MsgTypes.LootSpawn, new LootSpawnMessage { InstanceId = "L1", SystemId = "hub-1", ItemId = "Ore", Amount = 5, X = 3f, Y = 1f, OwnerFaction = "Gold" });
            var lootSpawn = bob.Receive(MsgTypes.LootSpawn, 2000);
            Check("loot-spawn broadcast", lootSpawn != null && (lootSpawn.Msg?["instanceId"]?.ToString() ?? "") == "L1");

            // C7: loot take -> cargo + LootGone
            alice.Send(MsgTypes.LootTaken, new LootTakenMessage { InstanceId = "L1", Taker = "Alice" });
            Check("loot-gone broadcast", bob.Receive(MsgTypes.LootGone, 2000) != null);
            Check("ledger cargo", core.Ledger.Cargo.TryGetValue("Alice", out var inv) && inv.GetValueOrDefault("Ore") == 5);

            // C8: trade Alice -> Bob
            alice.Send(MsgTypes.TradeOffer, new TradeOfferMessage { TradeId = "T1", To = "Bob", Offer = new List<ItemQty> { new() { ItemId = "Ore", Count = 2 } }, Want = new List<ItemQty>() });
            Check("trade-offer received", bob.Receive(MsgTypes.TradeOffer, 2000) != null);
            bob.Send(MsgTypes.TradeAnswer, new TradeAnswerMessage { TradeId = "T1", Accepted = true });
            var r1 = alice.Receive(MsgTypes.TradeResult, 2000);
            var r2 = bob.Receive(MsgTypes.TradeResult, 2000);
            Check("trade-result both", r1 != null && r2 != null && (r1.Msg?["ok"]?.ToObject<bool>() ?? false));
            Check("ledger transfer", core.Ledger.Cargo.TryGetValue("Alice", out var ai) && ai.GetValueOrDefault("Ore") == 3
                && core.Ledger.Cargo.TryGetValue("Bob", out var bi) && bi.GetValueOrDefault("Ore") == 2);

            // C9: timeline event fires (world demo has announce at 1s in hub-1)
            var evt = alice.Receive(MsgTypes.Event, 9000);
            Check("planner timeline event", evt != null && (evt.Msg?["command"]?.ToString() ?? "") == "announce");

            // C10: web endpoints
            var page = await http.GetStringAsync("/");
            Check("web map page", page.Contains("VGMMO server map"));
            var statusStr = await http.GetStringAsync("/v1/status");
            Check("web status has player", statusStr.Contains("Alice"));
            var layoutStr = await http.GetStringAsync("/v1/layout");
            Check("web layout has system", layoutStr.Contains("hub-1"));
            var actStr = await http.GetStringAsync("/v1/activity?since=0");
            // Schema assertion: activity entries are camelCase (id/tMs/kind/text) — the
            // page's dedupe guard depends on it. A PascalCase payload spams the feed.
            Check("web activity feed", actStr.Contains("\"entries\"") && actStr.Contains("\"tMs\"") && actStr.Contains("\"kind\"") && actStr.Contains("\"text\"") && actStr.Contains("join"));

            // C11: persistence
            alice.Send(MsgTypes.LeaveWorld, new object());
            alice.Poll(300);
            Check("state saved", core.SaveState() && File.Exists(Path.Combine(dataDir, "state.json")));
            var saved = File.ReadAllText(Path.Combine(dataDir, "state.json"));
            Check("state has cargo", saved.Contains("Alice") && saved.Contains("Ore"));

            // C11b: kick path must also produce an activity entry (Drop bypassed logging).
            var bobSession = core.Sessions().FirstOrDefault(s => s.Name == "Bob");
            if (bobSession != null)
            {
                core.Kick(bobSession.Id);
                bob.Poll(500);
                Check("kick logs activity", core.Activity.Tail(5).Any(e => e.Kind == "leave" && e.Text.Contains("Bob") && e.Text.Contains("kicked")));
                await Task.Delay(200);
            }

            // C12: disconnect cleanup
            alice.Send(MsgTypes.Ping, new PingMessage { T = Core.ServerCore.NowMs() });
            alice.Dispose();
            bob.Dispose();
            for (var i = 0; i < 160 && core.ActivePlayerCount() > 0; i++) await Task.Delay(50);
            Check("disconnect cleanup", core.ActivePlayerCount() == 0);
        }
        catch (Exception e)
        {
            Check("self-test run", false, e.ToString());
        }
        finally
        {
            keepAlive.Cancel();
            try { await serverLoop; } catch { /* cancelled */ }
            core.SaveState();
            await web.StopAsync();
            transport.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { /* temp leftover is fine */ }
        }

        Console.WriteLine();
        Console.WriteLine($"self-test: {_checks - Failures.Count}/{_checks} passed" + (Failures.Count > 0 ? " — FAILURES: " + string.Join("; ", Failures) : " — ALL GREEN"));
        return Failures.Count == 0 ? 0 : 1;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private const string TestWorldJson = """
{
  "id": "demo",
  "name": "Demo Hub",
  "systems": [
    { "id": "hub-1", "storyId": "mmo-test-hub", "name": "Hub Transit", "x": 0, "y": 0, "level": 8, "faction": "Gold",
      "pois": [ { "id": "hub-station", "kind": "station", "name": "Hub Starport", "x": 0, "y": 6 } ] },
    { "id": "mine-1", "storyId": "mmo-test-mine", "name": "Empty Veins", "x": -31, "y": 0, "level": 7, "faction": "MiningGuild",
      "pois": [ { "id": "mine-field", "kind": "mining", "name": "Ore Field", "x": -18, "y": 0 } ] }
  ],
  "links": [ { "a": "hub-1", "b": "mine-1" } ],
  "timeline": [
    { "at": 6, "systemId": "hub-1", "command": "announce", "args": { "text": "test" } },
    { "at": 12, "systemId": "mine-1", "command": "wave", "args": { "count": 2 } }
  ]
}
""";
}
