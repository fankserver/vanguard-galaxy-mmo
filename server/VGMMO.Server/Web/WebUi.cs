using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using VGMMO.Server.Core;

namespace VGMMO.Server.Web;

/// <summary>
/// Lightweight HTTP side channel: status/layout/activity JSON + an embedded map page
/// that draws the world (systems, links, POIs, live player dots) and the activity feed.
/// </summary>
public sealed class WebUi
{
    private readonly ServerCore _core;
    private WebApplication? _app;
    public int Port { get; private set; }

    public WebUi(ServerCore core) => _core = core;

    public async Task StartAsync(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();

        app.MapGet("/", () => Results.Content(Page, "text/html; charset=utf-8"));
        app.MapGet("/index.html", () => Results.Content(Page, "text/html; charset=utf-8"));
        app.MapGet("/v1/status", () => Json(Status()));
        app.MapGet("/v1/layout", () => Json(Layout()));
        app.MapGet("/v1/activity", (long? since) => Json(new { entries = _core.Activity.Since(since ?? 0, 200) }));
        // In-game POI icons (exported from the game by the client's IconExporter).
        app.MapGet("/icons/{name}", (string name) =>
        {
            var safe = Path.GetFileName(name);
            var path = Path.Combine(_core.Config.AssetsDir, "icons", safe);
            if (!File.Exists(path)) return Results.NotFound();
            return Results.File(File.ReadAllBytes(path), "image/png");
        });

        await app.StartAsync();
        _app = app;

        // Resolve the actually-bound port (0 = ephemeral).
        var server = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        var feature = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        var addr = feature?.Addresses?.FirstOrDefault();
        if (addr != null && Uri.TryCreate(addr, UriKind.Absolute, out var uri)) Port = uri.Port;
    }

    public Task StopAsync() => _app?.StopAsync() ?? Task.CompletedTask;

    private static IResult Json(object obj) => Results.Content(JsonConvert.SerializeObject(obj, VGMMO.Protocol.Wire.Settings), "application/json");

    private object Status()
    {
        var groups = _core.GroupMembers();
        var worlds = _core.Worlds().Values.Select(w => new
        {
            id = w.Id,
            name = w.Name,
            hash = w.ComputeLayoutHash(),
            systems = w.Systems.Select(s => new
            {
                id = s.Id,
                name = s.Name,
                players = groups.GetValueOrDefault(s.Id) ?? new List<string>(),
            }),
        });
        var players = _core.Sessions()
            .Where(s => s.SystemId != null)
            .Select(s => new
        {
            name = s.Name,
            system = s.SystemId ?? "",
            pos = s.LastPos == null ? null : new { x = s.LastPos.X, y = s.LastPos.Y, rot = s.LastPos.Rot },
            pingMs = s.PingMs,
            game = s.GameVersion,
        });
        return new
        {
            uptimeSec = (long)(DateTime.UtcNow - _core.StartedAt).TotalSeconds,
            nowMs = ServerCore.NowMs(),
            players = _core.ActivePlayerCount(),
            connected = _core.ConnectedCount(),
            worlds,
            playerList = players,
            activity = _core.Activity.Tail(30),
        };
    }

    private object Layout()
    {
        return new
        {
            worlds = _core.Worlds().Values.Select(w => new
            {
                id = w.Id,
                name = w.Name,
                hash = w.ComputeLayoutHash(),
                systems = w.Systems.Select(s => new
                {
                    id = s.Id,
                    storyId = s.StoryId,
                    name = s.Name,
                    x = s.X, y = s.Y, level = s.Level, faction = s.Faction,
                    pois = s.Pois.Select(p => new { id = p.Id, kind = p.Kind, name = p.Name, x = p.X, y = p.Y, faction = p.Faction, level = p.Level }),
                    payloads = s.Payloads,
                }),
                links = w.Links,
                timeline = w.Timeline,
            }),
        };
    }

    private const string Page = """
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<title>VGMMO — server map</title>
<style>
  body { margin:0; font-family: ui-monospace, "Cascadia Mono", Consolas, monospace; background:#0b0d12; color:#c9d4e6; }
  header { padding:10px 16px; border-bottom:1px solid #1e2635; display:flex; gap:24px; align-items:center; }
  header h1 { font-size:16px; margin:0; color:#7fd4ff; }
  #meta { color:#8b98ad; font-size:12px; }
  #layout { display:flex; gap:0; height:calc(100vh - 46px); }
  #mapwrap { flex:1; position:relative; }
  canvas { position:absolute; inset:0; width:100%; height:100%; }
  #side { width:340px; border-left:1px solid #1e2635; display:flex; flex-direction:column; }
  #players { padding:8px 12px; border-bottom:1px solid #1e2635; font-size:12px; }
  #players b { color:#ffd27f; }
  #feed { flex:1; overflow-y:auto; padding:8px 12px; font-size:12px; }
  .ev { padding:3px 0; border-bottom:1px dotted #1c2333; }
  .t { color:#5f6d85; margin-right:6px; }
  .k { display:inline-block; min-width:52px; color:#7fd4ff; }
  .k.join { color:#7ce38b; } .k.leave { color:#ff9d9d; } .k.event { color:#c9a2ff; }
  .k.loot { color:#ffd27f; } .k.trade { color:#7ce38b; }
</style>
</head>
<body>
<header>
  <h1>VGMMO server map</h1>
  <span id="meta">loading…</span>
  <span id="legend" style="font-size:11px;color:#5f6d85">⬢ station · ◆ mining · ☠ wreck · ⇄ gate · <span style="color:#19c8ff">●</span> players(count)</span>
</header>
<div id="layout">
  <div id="mapwrap"><canvas id="map"></canvas></div>
  <div id="side">
    <div id="players">—</div>
    <div id="feed"></div>
  </div>
</div>
<script>
const $ = id => document.getElementById(id);
const canvas = $('map'), ctx = canvas.getContext('2d');
let worlds = [], worldIdx = 0, lastActivityId = 0, status = null;
const ICON_FILES = {station:'SpaceStation', mining:'Mining', salvage:'Salvage', gate:'JumpGate', wormhole:'Wormhole'};
const iconImgs = {};
function loadIcons(){ for (const k in ICON_FILES){ const img = new Image(); img.src = '/icons/' + ICON_FILES[k] + '.png'; iconImgs[ICON_FILES[k]] = img; } }
const PALETTE = ['#7fd4ff','#ffd27f','#7ce38b','#ff9d9d','#c9a2ff','#9ad9c0','#f0a06a','#8fb7ff'];

function hashColor(name){ let h=0; for (const c of name) h = (h*31 + c.codePointAt(0)) >>> 0; return PALETTE[h % PALETTE.length]; }

function fit(world){
  const xs = [], ys = [];
  for (const s of world.systems){ xs.push(s.x); ys.push(s.y); for (const p of s.pois||[]){ xs.push(p.x); ys.push(p.y); } }
  const minX = Math.min(...xs), maxX = Math.max(...xs);
  const minY = Math.min(...ys), maxY = Math.max(...ys);
  const pad = 70;
  const w = canvas.width = canvas.clientWidth, h = canvas.height = canvas.clientHeight;
  const sc = Math.min((w - pad*2) / Math.max(1, maxX - minX), (h - pad*2) / Math.max(1, maxY - minY));
  const ox = w/2 - (minX + maxX)/2 * sc, oy = h/2 + (minY + maxY)/2 * sc;
  // Game convention: +Y = north (up). Canvas y grows down, so invert: py = oy - y*sc.
  return { sc, px: (x,y) => [x*sc + ox, oy - y*sc] };
}

function draw(){
  const world = worlds[worldIdx]; if (!world) return;
  const f = fit(world);
  ctx.fillStyle = '#0b0d12'; ctx.fillRect(0,0,canvas.width,canvas.height);
  for (const l of world.links){
    const a = world.systems.find(s=>s.id===l.a), b = world.systems.find(s=>s.id===l.b);
    if (!a || !b) continue;
    const [ax,ay]=f.px(a.x,a.y), [bx,by]=f.px(b.x,b.y);
    ctx.strokeStyle = '#2c3a55'; ctx.lineWidth = 2; ctx.beginPath(); ctx.moveTo(ax,ay); ctx.lineTo(bx,by); ctx.stroke();
    const mx=(ax+bx)/2, my=(ay+by)/2;
    ctx.fillStyle='#5f6d85'; ctx.font='10px monospace'; ctx.fillText('gate', mx+4, my-4);
  }
  for (const s of world.systems){
    const [x,y]=f.px(s.x,s.y);
    ctx.beginPath(); ctx.arc(x,y,16,0,Math.PI*2);
    ctx.fillStyle = '#141b28'; ctx.fill();
    ctx.strokeStyle = hashColor(s.faction || s.id); ctx.lineWidth = 2.5; ctx.stroke();
    ctx.fillStyle = '#5f6d85'; ctx.font = '10px monospace';
    ctx.fillText('L' + s.level + ' · ' + s.id, x-26, y-34);
    ctx.fillStyle = '#e8eefc'; ctx.font = 'bold 12px monospace'; ctx.textAlign='center';
    ctx.fillText(s.name, x, y+34); ctx.textAlign='left';
    // Icons scale with the map fit so they stay legible at any layout size.
    const isz = Math.max(24, Math.min(44, f.sc * 1.6));
    for (const p of s.pois){
      const [px,py]=f.px(p.x,p.y);
      const file = ICON_FILES[p.kind] || 'SpaceStation';
      const img = iconImgs[file];
      // In-game icon sprite, with the faction-color ring behind it (like the game map).
      ctx.beginPath(); ctx.arc(px,py,isz/2+2,0,Math.PI*2);
      ctx.strokeStyle = hashColor(p.faction || p.kind); ctx.lineWidth = 2.5; ctx.stroke();
      if (img && img.naturalWidth > 0){
        ctx.drawImage(img, px-isz/2, py-isz/2, isz, isz);
      } else {
        ctx.fillStyle = '#aab6cc'; ctx.font = Math.round(isz*0.55)+'px monospace'; ctx.textAlign='center';
        ctx.fillText(({station:'⬢', mining:'◆', salvage:'☠', gate:'⇄'}[p.kind] || '?'), px, py+isz*0.2);
        ctx.textAlign='left';
      }
    }
    // Aggregated player marker: real positions in layout coords (client sends world/250),
    // one dot per player; a count badge appears when several share the system. The old
    // system-center dots are gone — you see actual movement now.
    const playersHere = (status && status.playerList || []).filter(p => p.system === s.id && p.pos);
    if (playersHere.length){
      for (const p of playersHere){
        const [px,py]=f.px(p.pos.x, p.pos.y);
        const col = hashColor(p.name);
        ctx.beginPath(); ctx.arc(px,py,9,0,Math.PI*2);
        ctx.fillStyle = col; ctx.fill();
        ctx.strokeStyle = '#0b0d12'; ctx.lineWidth = 2.5; ctx.stroke();
        ctx.fillStyle = col; ctx.font = 'bold 12px monospace';
        ctx.fillText(p.name, px+12, py+4);
      }
      if (playersHere.length > 1){
        const [px,py]=f.px(s.x,s.y);
        const bx=px+24, by=py-24;
        ctx.beginPath(); ctx.arc(bx,by,10,0,Math.PI*2);
        ctx.fillStyle='#19c8ff'; ctx.fill();
        ctx.strokeStyle='#0b0d12'; ctx.lineWidth=2.5; ctx.stroke();
        ctx.fillStyle='#0b0d12'; ctx.font='bold 12px monospace'; ctx.textAlign='center';
        ctx.fillText(String(playersHere.length), bx, by+4); ctx.textAlign='left';
      }
    }
  }
}

function feed(entries){
  const el = $('feed');
  for (const e of entries || []){
    if (!e || !e.tMs) continue;
    if (e.id <= lastActivityId) continue;
    lastActivityId = e.id;
    const div = document.createElement('div'); div.className = 'ev';
    const d = new Date(e.tMs);
    div.innerHTML = `<span class="t">${d.toLocaleTimeString()}</span><span class="k ${e.kind}">${e.kind}</span> ${e.text}`;
    el.prepend(div);
  }
  while (el.children.length > 80) el.removeChild(el.lastChild);
}

async function refresh(){
  try {
    const r = await fetch('/v1/status');
    status = await r.json();
    const w = status.worlds[worldIdx];
    $('meta').textContent = `${status.uptimeSec}s up · players ${status.players} · system ${w ? w.systems.length : 0}`;
    $('players').innerHTML = '<b>Players</b><br>' + (status.playerList.map(p =>
      `${p.name} — ${p.system || 'warping'}${p.pos ? ` (x ${p.pos.x.toFixed(1)}, y ${p.pos.y.toFixed(1)})` : ''} · ${p.pingMs}ms`).join('<br>') || 'none');
    feed(status.activity);
    draw();
  } catch(e) { $('meta').textContent = 'server unreachable'; }
}

async function init(){
  loadIcons();
  const r = await fetch('/v1/layout');
  const data = await r.json(); worlds = data.worlds || [];
  if (!worlds.length) { $('meta').textContent = 'no worlds'; return; }
  setInterval(refresh, 2000);
  refresh();
}
init();
</script>
</body>
</html>
""";
}
