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
