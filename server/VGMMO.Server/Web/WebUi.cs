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
        // No cache: the export set changes during development; always serve fresh.
        app.MapGet("/icons/{name}", (string name) =>
        {
            var safe = Path.GetFileName(name);
            var path = Path.Combine(_core.Config.AssetsDir, "icons", safe);
            if (!File.Exists(path)) return Results.NotFound();
            return Results.File(File.ReadAllBytes(path), "image/png");
        });
        app.Use(async (context, next) =>
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            await next();
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
                entry = w.Entry,
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
  #systems { padding:8px 12px; border-bottom:1px solid #1e2635; font-size:12px; max-height:180px; overflow-y:auto; }
  #systems b { color:#7fd4ff; }
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
  <span id="legend" style="font-size:11px;color:#5f6d85">systems = stars · insignia = factions with stations · green line = gate · <span style="color:#19c8ff">●</span> players</span>
</header>
<div id="layout">
  <div id="mapwrap"><canvas id="map"></canvas></div>
  <div id="side">
    <div id="players">—</div>
    <div id="systems">—</div>
    <div id="feed"></div>
  </div>
</div>
<script>
const $ = id => document.getElementById(id);
const canvas = $('map'), ctx = canvas.getContext('2d');
let worlds = [], worldIdx = 0, lastActivityId = 0, status = null;
// AUTHORITATIVE DATA RULE: /v1/layout (the server's world definition) is the single
// source for ALL map content — systems, links, POIs, factions, doors. /v1/status is
// transient live state (player positions, feed) only. The map NEVER reads from status,
// so an unreachable server can never shrink or freeze the map.
const iconImgs = {};
function iconFor(file){
  if (!iconImgs[file]){ const i = new Image(); i.src = '/icons/' + file + '.png'; iconImgs[file] = i; }
  return iconImgs[file];
}
function loaded(file){ const i = iconImgs[file]; return !!i && i.complete && i.naturalWidth > 0; }
function preloadLayoutIcons(worldsData){
  // Galaxy map draws ONLY faction insignias (one per distinct faction with a station
  // in the system — FactionIconsHandler.SetSystemFactionIcons). POI plates
  // (Map_Poi_Location_*) are SYSTEM-VIEW objects; the vanilla galaxy map never
  // draws them (DrawSectorSystems -> DrawSystemIcon draws only the star marker).
  const files = new Set();
  for (const w of worldsData){
    for (const s of w.systems||[]){
      if (s.faction) files.add('Faction_' + s.faction);
      for (const p of s.pois||[]){ if (p.faction) files.add('Faction_' + p.faction); }
    }
  }
  const imgs = [...files].map(f => iconFor(f));
  return Promise.all(imgs.map(i => new Promise(res => { if (i.complete) res(); else { i.onload = () => res(); i.onerror = () => res(); } })));
}
const PALETTE = ['#7fd4ff','#ffd27f','#7ce38b','#ff9d9d','#c9a2ff','#9ad9c0','#f0a06a','#8fb7ff'];

function hashColor(name){ let h=0; for (const c of name) h = (h*31 + c.codePointAt(0)) >>> 0; return PALETTE[h % PALETTE.length]; }

function hashCode(name){ let h=0; for (const c of name) h = (h*31 + c.codePointAt(0)) >>> 0; return h; }

// Star-class palette approximation (BackdropManager.starColors gradient runs
// blue-white -> white -> yellow -> orange -> red giants). The exact gradient is art
// data; this palette is sampled by a stable per-system hash.
const STAR_PALETTE = ['#9fc6ff','#e8efff','#fff3c2','#ffb066','#ff7a5c'];

function fit(world){
  // Bounding box over systems only — POIs are system-local and not drawn on the
  // galaxy map (see drawMap).
  const xs = [], ys = [];
  for (const s of world.systems){ xs.push(s.x); ys.push(s.y); }
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
  // A draw failure must never kill refresh() (that faked 'server unreachable' and froze
  // partial frames) — log it and keep the page consistent.
  try { drawMap(); } catch(err){ console.error('map draw failed:', err); }
}
function drawMap(){
  // GALAXY-MAP PARITY with the decompiled game (GalaxyMapManager):
  // - per system: a STAR marker (colored glow dot, StarSpriteSource -> 'System'
  //   prefab sprite tinted star.GetColor(), scaled by star intensity x2)
  // - per system: faction insignia row BELOW the marker (FactionIconsHandler,
  //   one <Shipset>Small mapIcon per distinct faction with a station)
  // - gates: LINES only (CreateJumpGateLine): greenish in-sector,
  //   sectorGateBlue for sector gates, reddish when unusable
  // - NO POI plates: Map_Poi_Location_* are system-view objects (DrawSystemPois);
  //   the composite plates ('3 icons in one') simply do not exist at this layer.
  // POI listings live in the side panel instead.
  const world = worlds[worldIdx]; if (!world) return;
  const f = fit(world);
  ctx.imageSmoothingEnabled = true;
  ctx.fillStyle = '#0b0d12'; ctx.fillRect(0,0,canvas.width,canvas.height);
  renderSystemsPanel(world);
  // Gate lines (greenish = ColorHelper.greenish; sector gates would be blue)
  for (const l of world.links){
    const a = world.systems.find(s=>s.id===l.a), b = world.systems.find(s=>s.id===l.b);
    if (!a || !b) continue;
    const [ax,ay]=f.px(a.x,a.y), [bx,by]=f.px(b.x,b.y);
    ctx.strokeStyle = 'rgb(66,210,66)'; ctx.lineWidth = 2;
    ctx.beginPath(); ctx.moveTo(ax,ay); ctx.lineTo(bx,by); ctx.stroke();
  }
  const labels = [];
  for (const s of world.systems){
    const [x,y]=f.px(s.x,s.y);
    // Star marker: glow + core, color from a stable star-class palette.
    const star = STAR_PALETTE[hashCode(s.id) % STAR_PALETTE.length];
    const glow = ctx.createRadialGradient(x,y,2,x,y,26);
    glow.addColorStop(0, star); glow.addColorStop(0.35, star);
    glow.addColorStop(1, 'rgba(0,0,0,0)');
    ctx.globalAlpha = 0.55; ctx.fillStyle = glow;
    ctx.beginPath(); ctx.arc(x,y,26,0,Math.PI*2); ctx.fill();
    ctx.globalAlpha = 1;
    ctx.fillStyle = star;
    ctx.beginPath(); ctx.arc(x,y,6,0,Math.PI*2); ctx.fill();
    ctx.fillStyle = '#ffffff';
    ctx.beginPath(); ctx.arc(x,y,2.5,0,Math.PI*2); ctx.fill();
    // Faction insignia row below (distinct factions with a station here).
    const factions = [...new Set((s.pois||[]).filter(p => p.faction).map(p => p.faction))];
    if (s.faction) factions.unshift(s.faction);
    const uniq = [...new Set(factions)];
    uniq.forEach((fac, i) => {
      const file = 'Faction_' + fac;
      if (!loaded(file)) return;
      const badge = iconFor(file);
      const bs = 12, bx = x - (uniq.length - 1) * 8 + i * 16, by = y + 22;
      const bsx = bs / Math.max(badge.naturalWidth, badge.naturalHeight);
      ctx.drawImage(badge, bx - badge.naturalWidth*bsx/2, by - badge.naturalHeight*bsx/2,
                    badge.naturalWidth*bsx, badge.naturalHeight*bsx);
    });
    labels.push({ x, y, text: 'L' + s.level + ' · ' + s.id, name: s.name });
    // Player dots (MMO layer): real positions, one dot per player.
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
        const bx=x+24, by=y-24;
        ctx.beginPath(); ctx.arc(bx,by,10,0,Math.PI*2);
        ctx.fillStyle='#19c8ff'; ctx.fill();
        ctx.strokeStyle='#0b0d12'; ctx.lineWidth=2.5; ctx.stroke();
        ctx.fillStyle='#0b0d12'; ctx.font='bold 12px monospace'; ctx.textAlign='center';
        ctx.fillText(String(playersHere.length), bx, by+4); ctx.textAlign='left';
      }
    }
  }
  // Labels LAST: system text always paints on top.
  for (const l of labels){
    ctx.fillStyle = '#5f6d85'; ctx.font = '10px monospace';
    ctx.fillText(l.text, l.x-26, l.y-40);
    ctx.fillStyle = '#e8eefc'; ctx.font = 'bold 12px monospace';
    ctx.lineWidth = 3; ctx.strokeStyle = '#0b0d12';
    ctx.strokeText(l.name, l.x+16, l.y+4); ctx.fillText(l.name, l.x+16, l.y+4);
  }
}

function renderSystemsPanel(world){
  const el = $('systems');
  el.innerHTML = '<b>Systems</b><br>' + (world.systems||[]).map(s =>
    `<b>${s.name}</b> (${s.id}) — ` + ((s.pois||[]).map(p => `${p.name} [${p.kind}]`).join(', ') || 'no POIs'))
    .join('<br>');
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
  // Fetch failure and draw failure are now SEPARATE concerns: only a fetch failure
  // may print 'unreachable', and the map always redraws from the authoritative layout.
  let data = null;
  try { const r = await fetch('/v1/status'); if (r.ok) data = await r.json(); } catch(e) {}
  if (data){
    status = data;
    const w = status.worlds[worldIdx];
    $('meta').textContent = `${status.uptimeSec}s up · players ${status.players} · system ${w ? w.systems.length : 0}`;
    $('players').innerHTML = '<b>Players</b><br>' + (status.playerList.map(p =>
      `${p.name} — ${p.system || 'warping'}${p.pos ? ` (x ${p.pos.x.toFixed(1)}, y ${p.pos.y.toFixed(1)})` : ''} · ${p.pingMs}ms`).join('<br>') || 'none');
    feed(status.activity);
  } else {
    status = null;
    $('meta').textContent = 'server unreachable — full map from layout (no live players)';
    $('players').innerHTML = '<b>Players</b><br>n/a — server unreachable';
  }
  draw();
}

async function init(){
  preloadLayoutIcons(worlds);
  const r = await fetch('/v1/layout');
  const data = await r.json(); worlds = data.worlds || [];
  if (!worlds.length) { $('meta').textContent = 'no worlds'; return; }
  // Wait for every icon the layout needs (faction tiles) before the first paint —
  // no flash of fallback glyphs/circles while PNGs load.
  await preloadLayoutIcons(worlds);
  setInterval(refresh, 2000);
  refresh();
}
init();
</script>
</body>
</html>
""";
}
