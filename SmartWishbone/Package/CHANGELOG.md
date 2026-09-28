# Changelog

## 1.0.3
- Updated for Valheim 1.0 (fix notes by fire-VA)
- Fixed an error on every spawn and on every target switch: the mod was built against the old status effect method, which Valheim 1.0 changed
- Fixed the silver ore target also finding gold veins and frozen troll corpses: since 1.0 they use the same beacon name as silver veins. Objects are now matched by their own prefab name first (old data files that list 'Becon' still work)
- Registering an object with the hotkey now registers the object itself, not its beacon
- The default data file now tracks gold veins and frozen troll corpses (Gold Ore, after Fader), the new buried, combat and Deep North chests and buried skeletal remains, like the base game wishbone does in 1.0
- The default data file now uses the base game's 1.0 beacon ranges (silver veins 50, buried chests 40, muddy scrap piles 25)
- Items without an ItemDrop in the ObjectDB no longer stop the wishbone from being replaced

## 1.0.2
- Reverted accidental change to the config data file path in 1.0.1
## 1.0.1
- Updated for Valheim version 0.217.24
## 1.0.0
- Initial release