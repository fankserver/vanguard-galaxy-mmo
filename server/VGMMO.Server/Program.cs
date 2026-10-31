using VGMMO.Server.Core;
using VGMMO.Server.Transport;
using VGMMO.Server.Web;

namespace VGMMO.Server;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Contains("--self-test")) return await SelfTest.RunAsync();

        var config = ServerConfig.Load(FindArg(args, "--config") ?? "server.json");
        if (TryInt(args, "--port", out var p)) config.Port = p;
        if (TryInt(args, "--web-port", out var wp)) config.WebPort = wp;
        if (TryInt(args, "--ghosts", out var g)) config.GhostPilots = g;
        if (FindArg(args, "--data") is { } data) config.DataDir = data;
        if (FindArg(args, "--worlds") is { } worlds) config.WorldsDir = worlds;

        Console.WriteLine($"VGMMO server — UDP {config.Port} · web http://127.0.0.1:{config.WebPort}");
        Console.WriteLine($"worlds: {Path.GetFullPath(config.WorldsDir)} · data: {Path.GetFullPath(config.DataDir)}");

        var core = new ServerCore(config);
        core.Init();
        var transport = new GameServer(core);
        core.AttachTransport(transport);
        transport.Start(config.Port);
        var web = new WebUi(core);
        try
        {
            await web.StartAsync(config.WebPort);
            Console.WriteLine($"listening (UDP {transport.Port} / web http://127.0.0.1:{web.Port}) — type 'help' for commands");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"web UI unavailable ({e.Message}); continuing without it");
            Console.WriteLine($"listening (UDP {transport.Port}, web disabled) — type 'help' for commands");
        }

        var shutdown = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown = true; };
        _ = Task.Run(async () => { await Ops.ConsoleLoop.Run(core); shutdown = true; });

        try
        {
            while (!shutdown)
            {
                transport.Poll();
                core.Tick();
                await Task.Delay(10);
            }
        }
        finally
        {
            core.SaveState();
            await web.StopAsync();
            transport.Dispose();
        }
        Console.WriteLine("shutdown complete");
        return 0;
    }

    private static string? FindArg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool TryInt(string[] args, string name, out int value)
    {
        value = 0;
        var s = FindArg(args, name);
        return s != null && int.TryParse(s, out value);
    }
}
