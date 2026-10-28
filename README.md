# Boss Rematch

Fight defeated bosses again in Castlevania: Belmont’s Curse. Return to an arena and speak to its spectral **Echo**, then choose **Relive the battle**. Uses the game’s normal interaction, confirmation and controller bindings.

![Rose beside Isaac’s Echo with the normal interaction prompt](docs/screenshots/echo.png)

## Install

Requires **BepInEx 6.0.788 for Unity IL2CPP, Windows x64**.

Download the plugin ZIP from [Releases](https://github.com/ollieatkinson/castlevania-belmonts-curse-boss-rematch/releases/latest).

Close the game and extract the **whole ZIP into the main game folder**, beside `Castlevania Belmont's Curse.exe`. The plugin belongs at `BepInEx/plugins/BossRematch.dll`.

On Linux/Proton, add `WINEDLLOVERRIDES="winhttp=n,b" %command%` to the game’s Steam launch options. Keep any other launch options you need.

To uninstall, close the game and delete `BepInEx/plugins/BossRematch.dll`.

## Rematches

You must have defeated the boss and absorbed its Arcana. Speak to the Echo in its cleared arena and confirm **Relive the battle**. You keep your current equipment and abilities. The Echo disappears during the intro and fight, then returns when you return to the cleared arena.

![Isaac’s Echo offers Relive the battle or Not now](docs/screenshots/rematch.png)

Supports **Jeanne d’Arc, Leon, Medusa, Mammon, Doppelganger, Arthur, Carmilla, Talos, Isaac and Agnes**. Dracula and Saint Louis are not included.

## Arena travel

Bosses whose Arcana you have absorbed become travel destinations in visited arenas. Select their portrait on the area map, use the normal interact button when **Travel to arena** appears, then confirm. You arrive beside the Echo; speak to it to begin the rematch.

![Isaac’s portrait on the map with the Travel to arena prompt](docs/screenshots/boss-map.png)

![Arena travel confirmation using the game’s controller buttons](docs/screenshots/arena-travel.png)

Settings are in `BepInEx/config/ollieatkinson.bossrematch.cfg`:

```ini
[Map]
ShowBossIcons = true
FastTravelToArenas = true
```

Set either option to `false` to disable it, then restart the game. Mirror Fast Travel handles mirrors, landmarks and ferryman destinations separately.

## Progress and compatibility

The mod saves a checkpoint before a rematch. Repeat boss rewards and story progression are suppressed, and saving is paused during the fight. Winning reloads that checkpoint at your arena position, restoring health, resources and temporary pickups. Death or another level transition ends the replay and uses the game’s normal destination.

Isaac’s spawn and fight start have been checked in game alongside Ultrawide, Map Secrets and Mirror Fast Travel. Other bosses and full victory/return paths still need playtesting. Back up your save before using the mod.

Supports Steam build **25975867 / game version 1.1.3**. Other game binaries disable the mod at startup. If something fails, include `BepInEx/LogOutput.log` when reporting it.

## Build

Requires the .NET 8 SDK and a game installation that has run BepInEx once.

```sh
GamePath="/path/to/Castlevania Belmont's Curse" bash scripts/package.sh
```

The plugin ZIP and SHA-256 checksum are written to `dist/`. BepInEx and game assemblies are references only and are not included.

MIT. Unofficial community mod. Screenshots show game artwork belonging to its respective owners.
