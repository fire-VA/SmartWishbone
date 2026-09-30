# Changelog

## 1.0.6
- First release of the continued fork (Smart_Wishbone_Continued, by fire-VA). It keeps the original's plugin id, so settings and the data file carry over; remove the original package first
- Contains the Valheim 1.0 fixes listed under 1.0.3 to 1.0.5 below, which were never released as the original package
- The release build leaves out the test command the fork's own test rig uses

## 1.0.5
- A failed background save of the data file is now logged, at the next save or when the game quits (fix notes by fire-VA)

## 1.0.4
- Adding or removing a trackable no longer stalls the server (fix notes by fire-VA): the data file is written in the background, the file found at load is reused instead of searching the whole BepInEx folder on every save, the list is converted to yaml once instead of twice, and a dedicated server no longer searches the scene for beacons it has no player to use

## 1.0.3
- Updated for Valheim 1.0 (fix notes by fire-VA)
- Fixed an error on every spawn and on every target switch: the mod was built against the old status effect method, which Valheim 1.0 changed
- Fixed the silver ore target also finding gold veins and frozen troll corpses: since 1.0 they use the same beacon name as silver veins. Objects are now matched by their own prefab name first (old data files that list 'Becon' still work)
- Registering an object with the hotkey now registers the object itself, not its beacon
- The default data file now tracks gold veins and frozen troll corpses (Gold Ore, after Fader), the new buried, combat and Deep North chests and buried skeletal remains, like the base game wishbone does in 1.0
- The default data file now uses the base game's 1.0 beacon ranges (silver veins 50, buried chests 40, muddy scrap piles 25)
- Items without an ItemDrop in the ObjectDB no longer stop the wishbone from being replaced
- Fixed an admin logging out of a server emptying the trackable list for every player on it: the logout reset of the synced list was sent to the server like an admin's config change. It now stays local
- Fixed clients on a dedicated server getting no trackables: the bundled ServerSync.dll was built before Valheim 1.0 made ZRoutedRpc.Everybody a constant, so its config-change handler failed every time and the synced trackable list was never read. ServerSync is now a standalone copy of Fires Unified Core's ConfigSync (a fork of blaxxun-boop's ServerSync, MIT-0), compiled into the mod. It has no version check, so a client with a different version of the mod is no longer refused at login

## 1.0.2
- Reverted accidental change to the config data file path in 1.0.1
## 1.0.1
- Updated for Valheim version 0.217.24
## 1.0.0
- Initial release