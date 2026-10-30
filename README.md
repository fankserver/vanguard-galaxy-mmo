# VGMMO — Vanguard Galaxy MMO (server)

The server half of the MMO idea: a headless **world authority + relay** that a BepInEx client
mod (not built yet) connects to. It owns identity/layout, event timelines, the loot/trade
ledger and presence; it never runs game code and never loads game files.

- Design docs live in `../docs/`:
  - `mmo-mod-design.md` — client mod, hosted systems, save lifecycle (design)
  - `mmo-sync-design.md` — sync architecture, sim-host model (design)
  - `mmo-server-design.md` — this server's design (design)

## Layout

```
server/
  VGMMO.Protocol/  shared message contracts (netstandard2.1, Newtonsoft.Json; no Unity, no game types)
  VGMMO.Server/    .NET console: UDP transport (LiteNetLib), sessions/worlds/groups,
                   event planner, loot/trade ledger, state persistence, console ops,
                   and a Kestrel web UI (map + activity feed)
  worlds/          authored world content (*.json)
```

## Quick start

```bash
make build      # dotnet build -c Release
make selftest   # in-process integration test over real UDP + HTTP (prints PASS/FAIL)
make run        # start server (UDP 34567, web http://127.0.0.1:8080)
```

Then open `http://127.0.0.1:8080` — the map page shows the world (systems, gates, POIs),
live player dots and the activity feed. Console commands: `status`, `players`, `worlds`,
`activity`, `snapshot`, `kick <name>`, `quit`.

## Configuration

`server.json` (see `server.json.example`) — or CLI flags `--port`, `--web-port`, `--data`,
`--worlds`. `acceptedHashes: []` accepts any client build (dev); set a list of catalog
hashes to gate clients by mod build (game version is never part of the gate).

## World content

`server/worlds/<name>.json` — author one file per world. Systems have ids, positions,
factions, POIs and payload specs; `links` join systems; `timeline` entries fire once at
(server start + `at` seconds) and are broadcast to the system's players.
`VGMMO.Server --self-test` uses a generated world; run `make selftest` to see a working
example end-to-end.

## Status / security

Freely reachable, open join (build-gated only). Abuse protection is operational: per-peer
rate limits, caps and authoritative clamping of relayed values. No anti-cheat; coop-trust
model per the design docs.
