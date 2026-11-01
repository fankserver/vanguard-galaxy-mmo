using System.Net;
using LiteNetLib;
using LiteNetLib.Utils;
using VGMMO.Protocol;

namespace VGMMO.Probe;

/// <summary>
/// Headless client for replication testing: connects, joins a system, broadcasts a
/// circular ship state and prints everything the server relays back.
/// Usage: VGMMO.Probe [host] [port] [name] [systemId] [--mode cluster|universe]
/// </summary>
public static class Program
{
    private const int MaxFrame = 64 * 1024;

    public static async Task<int> Main(string[] args)
    {
        var host = args.Length > 0 ? args[0] : "127.0.0.1";
        var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 34567;
        var name = args.Length > 2 ? args[2] : "Probe";
        var system = args.Length > 3 ? args[3] : "hub-1";
        var mode = "cluster";
        var scenario = "loop";   // loop | roundtrip (bounded universe join->state->leave)
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--mode" && i + 1 < args.Length) mode = args[i + 1];
            if (args[i] == "--scenario" && i + 1 < args.Length) scenario = args[i + 1];
        }

        var listener = new EventBasedNetListener();
        var net = new NetManager(listener) { UpdateTime = 15 };
        NetPeer? peer = null;
        var joined = false;
        var helloSent = false;
        var leaveSent = false;
        var seq = 0u;
        var inbox = new List<Envelope>();
        var lockObj = new object();

        listener.PeerConnectedEvent += p => peer = p;
        listener.NetworkReceiveEvent += (p, r, ch, del) =>
        {
            var frame = TryReadFrame(r);
            if (frame != null) lock (lockObj) inbox.Add(Wire.Decode(frame));
        };
        listener.PeerDisconnectedEvent += (p, info) => Console.WriteLine($"[probe] disconnected: {info.Reason}");

        net.Start();
        net.Connect(new IPEndPoint(IPAddress.Loopback, port), "vgmmo");
        Console.WriteLine($"[probe] {name} connecting {host}:{port}...");

        var t0 = DateTime.UtcNow;
        float x = 0f, y = 0f;
        uint lastLoggedSeq = 0;

        try
        {
            while (true)
            {
                net.PollEvents();
                lock (lockObj)
                {
                    foreach (var env in inbox) Handle(env, ref joined, ref peer, name, system, ref seq);
                    inbox.Clear();
                }

                if (joined && scenario == "roundtrip" && (DateTime.UtcNow - t0).TotalSeconds > 15)
                {
                    Console.Error.WriteLine("[probe] ROUNDTRIP FAIL (no flush-ack within 15s of join)");
                    return 2;
                }
                if (peer != null && peer.ConnectionState == ConnectionState.Connected)
                {
                    if (!helloSent)
                    {
                        helloSent = true;
                        var hw = new NetDataWriter();
                        var hf = Wire.Encode(MsgTypes.Hello, new HelloRequest
                        {
                            Protocol = ProtocolInfo.Version,
                            GameVersion = "probe",
                            ClientBuildHash = "vgmmo-night-1",
                            Name = name,
                            Mode = mode,
                        });
                        hw.Put(hf.Length); hw.Put(hf);
                        peer.Send(hw, DeliveryMethod.ReliableOrdered);
                    }
                    if (joined && scenario == "roundtrip")
                    {
                        var t = (float)(DateTime.UtcNow - t0).TotalSeconds;
                        x = 4f * (float)Math.Sin(t * 0.5f);
                        y = 3f * (float)Math.Cos(t * 0.5f);
                        var w = new NetDataWriter();
                        var frame = Wire.Encode(MsgTypes.ShipState, new ShipStateMessage
                        {
                            SystemId = system, X = x, Y = y, Rot = t * 40f % 360f, Vx = 0f, Vy = 0f, Seq = ++seq,
                        });
                        w.Put(frame.Length); w.Put(frame);
                        peer.Send(w, DeliveryMethod.Unreliable);
                        if (seq - lastLoggedSeq >= 10)
                        {
                            lastLoggedSeq = seq;
                            Console.WriteLine($"[probe] {name} @ ({x:F1},{y:F1}) seq {seq}");
                        }
                        // After two seconds of states: request the graceful leave and wait
                        // for the FlushAck (bounded headless round trip, exits 0 on ack).
                        if (seq >= 25 && !leaveSent)
                        {
                            leaveSent = true;
                            var lw = new NetDataWriter();
                            var lf = Wire.Encode(MsgTypes.LeaveUniverse, new LeaveUniverseRequest());
                            lw.Put(lf.Length); lw.Put(lf);
                            peer.Send(lw, DeliveryMethod.ReliableOrdered);
                            Console.WriteLine("[probe] LeaveUniverse sent, awaiting flush-ack");
                        }
                    }
                }
                await Task.Delay(20);
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[probe] error: {e.Message}");
            return 1;
        }
        finally
        {
            peer?.Disconnect();
            net.Stop();
        }
    }

    private static void Handle(Envelope env, ref bool joined, ref NetPeer? peer, string name, string system, ref uint seq)
    {
        switch (env.Type)
        {
            case MsgTypes.Welcome:
                var w = Wire.Read<WelcomeResponse>(env);
                Console.WriteLine($"[probe] welcome: session {w?.SessionId} world {w?.WorldId} mode={w?.Mode}");
                var ww = new NetDataWriter();
                var f = Wire.Encode(MsgTypes.JoinWorld, new JoinWorldRequest { SystemId = system });
                ww.Put(f.Length); ww.Put(f);
                peer?.Send(ww, DeliveryMethod.ReliableOrdered);
                break;
            case MsgTypes.SystemEntered:
                var se = Wire.Read<SystemEnteredMessage>(env);
                Console.WriteLine($"[probe] entered {se?.SystemId}");
                joined = true;
                break;
            case MsgTypes.FlushAck:
                Console.WriteLine("[probe] flush-ack received");
                Console.WriteLine("[probe] ROUNDTRIP PASS");
                Environment.Exit(0);
                break;
            case MsgTypes.Refused:
                Console.WriteLine($"[probe] REFUSED: {Wire.Read<RefusedResponse>(env)?.Reason}");
                break;
            case MsgTypes.RemoteShipState:
                var rs = Wire.Read<RemoteShipState>(env);
                Console.WriteLine($"[probe] remote {rs?.Name} @ ({rs?.X:F1},{rs?.Y:F1}) ({rs?.SessionId?[..6]})");
                break;
            case MsgTypes.Event:
                var ev = Wire.Read<EventMessage>(env);
                Console.WriteLine($"[probe] EVENT [{ev?.SystemId}] {ev?.Command}");
                break;
            case MsgTypes.LootSpawn:
                var ls = Wire.Read<LootSpawnMessage>(env);
                Console.WriteLine($"[probe] loot {ls?.Amount}x {ls?.ItemId} in {ls?.SystemId}");
                break;
            case MsgTypes.LootGone:
                Console.WriteLine($"[probe] loot gone {Wire.Read<LootGoneMessage>(env)?.InstanceId?[..6]}");
                break;
            case MsgTypes.TradeOffer:
                Console.WriteLine($"[probe] trade offer from {Wire.Read<TradeOfferMessage>(env)?.From}");
                break;
        }
    }

    private static byte[]? TryReadFrame(NetDataReader reader)
    {
        if (reader.AvailableBytes < 4) return null;
        var len = reader.GetInt();
        if (len <= 0 || len > MaxFrame) return null;
        if (reader.AvailableBytes < len) return null;
        return reader.GetRemainingBytes();
    }
}
