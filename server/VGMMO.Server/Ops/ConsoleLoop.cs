using VGMMO.Server.Core;

namespace VGMMO.Server.Ops;

/// <summary>Interactive console: status, players, worlds, reload, snapshot, kick, help, quit.</summary>
public static class ConsoleLoop
{
    public static async Task Run(ServerCore core)
    {
        while (true)
        {
            var line = await Task.Run(() => Console.ReadLine());
            if (line == null)
            {
                // stdin closed (daemon/docker/systemd or detached run): stay up, only
                // SIGINT/SIGTERM or an explicit quit (from a tty) stops the server.
                await Task.Delay(TimeSpan.FromMinutes(5));
                continue;
            }
            line = line.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            try
            {
                switch (parts[0].ToLowerInvariant())
                {
                    case "help":
                        Console.WriteLine("commands: status | players | worlds | activity [n] | reload | snapshot | kick <name> | quit");
                        break;
                    case "status":
                        Console.WriteLine($"{core.ActivePlayerCount()} players · {core.Worlds().Count} worlds · {core.Ledger.Loot.Count} loot · {core.Ledger.Cargo.Count} cargo owners");
                        break;
                    case "players":
                        foreach (var s in core.Sessions())
                            Console.WriteLine($"{s.Name,-24} {s.SystemId ?? "-",-12} ping {s.PingMs,5}ms  game {s.GameVersion}");
                        break;
                    case "worlds":
                        foreach (var w in core.Worlds().Values)
                            Console.WriteLine($"{w.Id,-12} {w.Name}  systems {w.Systems.Count}  timeline {w.Timeline.Count}");
                        break;
                    case "activity":
                        foreach (var e in core.Activity.Tail(parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 20))
                            Console.WriteLine($"{e.Id,6} {e.Kind,-6} {e.Text}");
                        break;
                    case "reload":
                        // worlds are immutable per process; reload re-reads config data dir state
                        core.SaveState();
                        Console.WriteLine("state saved; note: world content is loaded at startup (restart to pick up new worlds/*.json)");
                        break;
                    case "snapshot":
                        Console.WriteLine(core.SaveState() ? "state saved" : "save FAILED");
                        break;
                    case "kick":
                        if (parts.Length < 2) { Console.WriteLine("usage: kick <name>"); break; }
                        var victim = core.Sessions().FirstOrDefault(s => s.Name.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
                        if (victim == null) { Console.WriteLine("no such player"); break; }
                        core.Kick(victim.Id);
                        Console.WriteLine($"kicked {victim.Name}");
                        break;
                    case "quit":
                    case "exit":
                    case "shutdown":
                        return;
                    default:
                        Console.WriteLine("unknown command (help)");
                        break;
                }
            }
            catch (Exception e) { Console.Error.WriteLine("command error: " + e.Message); }
        }
    }
}
