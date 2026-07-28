# Changelog

## 1.0.9

- Registered Taming Orb and Feed Barrel prefabs synchronously with `ObjectDB` and `ZNetScene`, preventing saved custom items and pieces from being skipped during dedicated-server startup.
- Preserved CreatureManager modifier powers, cooldowns, and accumulated state through Taming Orb capture and release using its versioned public API, with transactional failure handling and compatibility for existing v3 Orbs.

## 1.0.8

- Kept the initial 5-meter Taming Orb capture requirement while allowing capture to finish if the player or creature moves away during the 4.5-second effect delay.
- Allowed Taming Orbs to capture tamed growing creatures such as `Lox_Calf` that have a `Character` but no `Tameable` component, while preserving their growth progress without adding components.

## 1.0.7

- Kept captured creature icons visible on the hotbar by refreshing Taming Orb icons after other hotbar updates.
- Added the server-synced `Egg Hatch Time Multiplier`, while preserving explicit prefab `eggGrow` values from `feed.yml`.
- Added remaining hatch time to warm egg hover text.
- Added `Needs more cover` to cold eggs that specifically fail their required roof or cover conditions.

## 1.0.6

- Fixed captured Taming Orbs becoming impossible to pick up after being dropped on dedicated servers.
- Kept creature icons client-local while persisting the network-safe base item variant.
- Recovered previously dropped Taming Orbs with invalid dynamic icon variants when they are loaded.

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
