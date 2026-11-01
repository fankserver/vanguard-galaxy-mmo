# VGMMO — Vanguard Galaxy MMO (server + client)

The MMO mod split: a headless **world authority + relay** server and a BepInEx **client**
mod (in development). Server never runs game code and never loads game files; it owns
identity/layout, event timelines, the loot/trade ledger and presence.

- Design docs live in `../docs/`: `mmo-mod-design.md`, `mmo-sync-design.md`, `mmo-server-design.md`.

## Layout

```
server/  VGMMO.Protocol (shared contracts) · VGMMO.Server (UDP relay + Kestrel web map UI)
         worlds/ (authored content)
client/  VGMMO.Client (BepInEx 5 plugin: connect panel, session, ship-state send, remote markers)
probe/   VGMMO.Probe (headless test client for replication checks)
```

## Quick start

```bash
make build         # server build
make selftest      # server integration test over real UDP+HTTP (21 checks)
make run           # server: UDP 34567, web http://127.0.0.1:8080 (map + activity feed)
make client-build  # BepInEx client plugin
make client-deploy # deploy to BepInEx/plugins/VGMMO (game must be closed)
```

Client smoke test (with the game closed, server running):

```bash
# copy a disposable save slot for in-world testing (keeps the real save untouched)
cp "$SAVES/Fank-5.save" "$SAVES/VGMMO-Test.save"
# set [test] autoLoadSlot = VGMMO-Test and [session] autoConnect = true in
# BepInEx/config/vgmmo.client.cfg, launch the game, then:
dotnet run --project probe/VGMMO.Probe -c Release -- 127.0.0.1 34567 Probe hub-1
# expect: probe logs 'remote Player @ (x,y)' and the game log logs
# 'remote player Probe appeared' — two-way replication via the server.
```

## Configuration

Server: `server/server.json` (or `--port`, `--web-port`, `--data`, `--worlds`).
`acceptedHashes: []` accepts any client build (dev); set catalog hashes to gate by mod
build (game version is never part of the gate). Client: `BepInEx/config/vgmmo.client.cfg`.

## Status / security

Server is publicly reachable, open join (build-gated only, optional password). Abuse
protection is operational (rate limits, caps, clamped relay). No anti-cheat; coop-trust
model per the design docs.

## M5 — Universe mode (shared world)

Two join modes against the same server (chosen by `Hello.mode` / the client panel
selector):

- **cluster** (default): the MMO world is baked as a hot-added sector in the
  player's own galaxy (travel via gates). Level 1 of the mod.
- **universe** (M5): joining enters a *completely different galaxy*. The player's
  home galaxy is snapshotted to a sidecar (`BepInEx/plugins/VGMMO/home-<slot>.json.gz`)
  before the swap; player-level state (ships, crew, credits, XP, empire) carries;
  the server owns the shared world; the home save is NEVER written while connected
  (`GamePlayer.isEphemeral` — vanilla no-ops every save path). Leaving restores the
  home galaxy from the sidecar on the live player (missions, routes, waypoints,
  current system/POI/home station all re-pointed) and removes the sidecar.

### Config (`server.json`, see `server.json.example`)

- `defaultWorld`: preferred world (id or name). Without it, joins pick the
  **alphabetically-first** world matching the requested mode — set it explicitly
  (e.g. `"demo"`) if the operator wants a specific world as the default.
- `shadowGhost` (default **false**): test scaffolding — a clone of the most recent
  in-cluster player. Enable only in test configs.
- `ghostPilots` (default 0): simulated patrol pilots (test scaffolding).

### Verification

Diagnostic (in-game debug console, `vgmmo sc …`): `roundtrip` (capture → corrupt →
restore → map equality + resolved mission POIs), `verify` (sidecar-vs-save deep
compare excluding the documented volatile whitelist: elapsedTime,
dynamicEventTimer, extraMercenarySpawnTimer, abilityCooldowns, activeEffects,
activeToggles, bossSpawnLevels), `ue on|off` (ephemeral guard), `join`/`leave`.
Headless: `VGMMO.Probe … --mode universe --scenario roundtrip` (join → state →
LeaveUniverse → FlushAck → exit 0).

**Owner acceptance bundle** (defines the invariants closed by M5):

1. `vgmmo sc roundtrip` → map equality, `bounty=poi-resolved`.
2. `vgmmo sc ue on` → `asv` + manual save → **save mtimes unchanged** + suppression logs.
3. `vgmmo sc join` (or panel universe mode) → loading stages → arrival at the shared
   world → replicas spawn → autosave writes nothing.
4. **File-hash protocol**: sha256 of `<slot>.save` before join vs after leave → identical.
5. Hard-kill mid-session → home save untouched + `STALE SIDE CAR` orphan log at next start.
6. Gate travel between universe systems → presence re-joins the new system group.
