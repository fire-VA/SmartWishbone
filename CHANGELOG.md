## 1.0.15

### Fixed
- Players who aren't admins get the server's settings again when they ask for them; changing settings still needs an admin.

## 1.0.14
- Security: synced settings are size-checked and capped, clients take settings only from the server, and the lock check fails closed. (security)
- The "Everyone" option for toggling what the wishbone tracks now only accepts known item names, with a rate limit and a total cap for non-admins. (security)
- Boss-locked trackables now unlock from your own personal boss progress as well as world progress, and use Fires Unified Core's boss progress when it is installed. (tracking)
- Faster main menu load: the mod's files are found in one pass instead of three. (performance)

## 1.0.7
- Cleaner settings screen: tuning options are under Show Advanced.
- Debug logging is off by default.

## 1.0.6
- Updated for Valheim 1.0, with fixes and optimizations (a fork of Goldenrevolver's Smart Wishbone).
- Fixed the config data file path.
