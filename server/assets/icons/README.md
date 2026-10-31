# Map icon sources

`client/VGMMO.Client/IconExporter.cs` exports the game's sprite meshes and UVs.

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
`tinySize ?? fullSize`. For example, Gold uses `LuminateSmall`. These insignias
belong underneath station icons, not baked into the POI image. Player, Amalgam,
HolyRadicals and Independent have no map emblem.

`PoiIconLibrary` supplies separate tooltip/UI art, not the five map icons above.
`Poi_SpaceStation.v2.png` is an alternative transparent station export; the web
UI uses `Poi_SpaceStation.png`.

## Rendering and validation

The exporter uses an isolated scene and an explicit camera culling mask to
exclude live geometry. Temporary objects are deactivated before deferred
`Object.Destroy` so successive renders in the same frame remain independent.
Render targets are restored and released in `finally`.

PNG bytes are staged before writing. The batch requires all five main icons,
transparent-black corners, and byte-identical repeat exports in reverse order.
Validation failures leave existing files untouched.

## Re-export

With authorization to restart/deploy the game: stop it, run `make client-deploy`,
set `[test] exportIconsPath` in `vgmmo.client.cfg`, and launch. Require the
`transparent corners and render-order independence passed for 5 POI icons; done.`
log before copying assets. Reset `exportIconsPath` to empty afterward.

Inspect and copy only the intended assets. A successful export validates its
output batch, not every PNG already present in this directory.
