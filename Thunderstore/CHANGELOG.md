# Changelog

## 1.0.5

- Hardened dedicated-server manual feeding with owner-side distance and TamingBoost validation, plus bounded duplicate-request protection.
- Made explicit empty `consumeItems` overrides distinct from omitted fields and consolidated food-entry parsing.
- Guaranteed mount stamina regeneration settings are restored even when another riding patch throws.
- Made Taming Orb capture prepare payloads and icons before committing, with item-state rollback on capture failure.
- Reduced core-system complexity by separating hover HUD, horizontal procreation tracking, and creature-modifier compatibility while centralizing runtime refreshes.
- Reused Taming Orb creature icon caches across mod updates and removed redundant per-item icon metadata.

## 1.0.4

- Preserved CL&LC infusion and extra effects when tamed creatures level up or grow, preventing effect rerolls.
- Inherited CreatureManager modifiers when tamed creatures grow into their adult prefabs.

## 1.0.3

- Kept the Taming Orb prefab registered when its recipe is disabled, preserving stored orbs across dedicated-server restarts.

## 1.0.2

- Fixed dedicated-server manual feeding so nearby players can feed creatures regardless of creature network ownership.
- Added pending food reservations and requester-side MeadTamer detection for reliable multiplayer feeding.
- Added the server-synced `Show Food Even If Alerted` option, enabled by default.
- Added localized hotbar feeding guidance with a client-only `Show Feeding Help` option.
- Made feeding guidance update immediately with crouch and food icon visibility settings.
- Streamlined the README.

## 1.0.1

- Fixed repeated hover food icon errors after the game HUD is recreated during world loading or transitions.
- Added the GitHub project link to the README.

## 1.0.0

- Initial release
