# Smart Wishbone Updated

**All credit for Smart Wishbone goes to its original author, Goldenrevolver.** This repository is an updated fork of
[Goldenrevolver/SmartWishbone](https://github.com/Goldenrevolver/SmartWishbone), kept working on Valheim 1.0 because the
original is no longer updated. The mod's design and features are Goldenrevolver's work; this fork fixes it for Valheim 1.0
and fixes a few multiplayer issues.

- Thunderstore package: `VerdantsAscent-SmartWishboneUpdated`
- What changed in the fork: [CHANGELOG](SmartWishbone/Package/CHANGELOG.md)
- How to use the mod: [README](SmartWishbone/Package/README.md)
- License: MIT. Goldenrevolver's copyright notice is kept in [LICENSE](LICENSE). Bundled libraries are listed in
  [THIRDPARTY-NOTICES](SmartWishbone/Package/THIRDPARTY-NOTICES.md).

It keeps the original's plugin id (`goldenrevolver.SmartWishbone`), so it replaces the original: remove the original
package before installing this one.

## Building

The project expects `GameDir` (see `SmartWishbone/GameDir.targets`) to point at a Valheim install with BepInEx and the
publicized game assemblies. No game files are included in this repository.
