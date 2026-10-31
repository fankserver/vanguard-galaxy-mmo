# Map icon sources

`client/VGMMO.Client/IconExporter.cs` exports the game's own sprite meshes/UVs;
these are not hand-drawn approximations or crops of a multi-icon sheet.

| Web asset | `WorldMapPOI.GetPrefab` | Main sprite |
| --- | --- | --- |
| `Poi_SpaceStation.png` | SpaceStation | Map_Poi_Location_Spacestation |
| `Poi_Mining.png` | Mining | Map_Poi_Location_AsteroidField |
| `Poi_Salvage.png` | Salvage | Map_Poi_Location_Salvage |
| `Poi_JumpGate.png` | JumpGate | Map_Poi_Location_Jumpgate |
| `Poi_Wormhole.png` | Wormhole | Map_Poi_Location_Wormhole_0 |

The prefabs come from `Resources/GalaxyMap/POI`. Their `spriteRenderer` is the
icon; `backgroundRenderer` is a separate radial glow and `missionLocation` a
separate mission marker. All five main renderers use `drawMode=Simple` and
96×96 sprites. The wormhole PNG is the prefab's static first frame.

`Faction_*.png` comes from `FactionIconSet.Get(faction).mapIcon`, which selects
`tinySize ?? fullSize`. For example Gold uses `LuminateSmall`; the `Small`
suffix does **not** indicate a sprite sheet. These insignias belong underneath
station icons, not baked into the POI image.

## Why the previous exports were collages

- `Object.Destroy` is deferred until the end of the frame. Rendering many
  temporary sprites at the same origin without deactivating them first captured
  earlier exports in later images.
- `Camera.scene` alone did not exclude the live menu backdrop in the player.
  A dedicated sprite layer and explicit camera culling mask are also necessary.
- The claim that `PoiIconLibrary` contained only red-X placeholders was another
  consequence of contaminated rendering. Its UI art is real, but is not the
  source for the five map icons above.

The exporter now deactivates temporary objects immediately, restores/releases
render targets in `finally`, and stages PNG bytes before writing. It requires
all five main icons, checks their corners are transparent black, and compares
repeat exports in reverse order byte-for-byte before publishing the batch.

## Verification (2026-10-31)

- `make client-build` and the authorized in-game export completed successfully.
- All five POIs passed transparent-corner and render-order checks. Sixteen
  faction emblems were exported; Player, Amalgam, HolyRadicals and Independent
  have no map emblem in this build.
- Station, mining, salvage and gate artwork visually match the supplied
  in-game Ashen Drift system-view screenshot (`/tmp/vg_latest_a.jpg` in the
  extraction session). No supplied screenshot shows a wormhole: its identity
  was verified against the actual prefab/sprite, not a claimed screenshot match.
- `Poi_SpaceStation.png` is deliberately unchanged: its owner-approved MD5 is
  `5e3c2ef2aa462ef2a8ff5c9bf076c877`. `Poi_SpaceStation.v2.png` provides the clean,
  transparent re-export for comparison; the web UI still uses the original.
- Other legacy `Poi_*.png` and unprefixed PNGs in this directory were not part of
  this repair. Do not assume they have been re-exported or validated.

## Re-export

Only with authorization to restart/deploy the game: stop it, run
`make client-deploy`, set `[test] exportIconsPath` in `vgmmo.client.cfg`, launch,
and require the final `transparent corners and render-order independence
passed for 5 POI icons; done.` log. Reset `exportIconsPath` to empty afterward.
Copy only the intended assets after inspecting them; failed exports leave the
previous files in place. Do not replace the approved station without approval.
