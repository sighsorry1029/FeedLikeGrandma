# Changelog

## 1.1.8

- Recover empty Taming Orbs that still carry an already-released identity after an older item state is restored. On the next capture attempt, the server verifies the completed release and the client assigns a fresh identity to the matching empty Orb. Use the Orb again after the recovery message to capture normally.
- Keep filled, changed, duplicate-ID, and actively committing Orbs blocked. Recovery preserves the old transaction receipts and does not create creatures or items, restore contents, or automatically retry capture. Lost replies and reconnects use the existing recovery path.
- Preserve existing Orb payloads, pending-state and journal formats, and the exact bytes of ordinary capture replies. Added an optional server-only recovery permission; update the server and every client together.
- Added regression checks for item custody, stale responses, packet compatibility and completed-release proof. Checks passed on the original Valheim 1.0.20 client and dedicated-server Mono runtimes; actual multiplayer verification remains pending. Separate character, world and journal saves are still not atomic.

## 1.1.7

- Fixed feeding and Taming Orb hooks continuing after another Harmony patch had blocked item use. Existing ward restrictions now stop these actions before their side effects.
- Added optional STUWard 1.4.0 integration. Add `FLG_TamingOrb` to STUWard's `item_prefab_policy.blocked_item_prefabs` to check both the player and capture target or release position, including use from outside a ward.
- Validate new Orb requests on the server and recheck access before capture commit or local creature spawning. Ward denials use existing rollback and transaction recovery paths; completed actions can still resolve. Orb payloads, RPC formats, and saved transaction records are unchanged.
- Added English and Korean ward-denial messages and focused compatibility and recovery checks. Rewrote the README with a separate detailed user guide. Actual game and multiplayer verification remains pending.
- Update FeedLikeGrandma on the server and every client together. For ward integration, also install STUWard 1.4.0 or later on all of them. If STUWard is installed but its required API is unavailable, new Orb actions are blocked; STUWard remains optional.

## 1.1.6

- Preserved ValheimCuisine Lox milking cooldowns and breeding history through Taming Orb capture and release. Remaining cooldown pauses inside an Orb and continues in world time outside it, including zone unloads.
- Added server-validated milking with owner-persisted cooldowns, mutual exclusion with Orb capture, and duplicate/reconnect protection. Ambiguous owner writes remain blocked until confirmed or the network session restarts; uncertain milk delivery is not granted again.
- Fixed excess milk drops when the inventory accepts only part of a stack; only the remaining amount is dropped.
- Detect ValheimCuisine compatibility from its actual milking methods, settings and installed Harmony patches instead of a fixed version number. Missing cooldown history, including old Orbs, adds no initial waiting period; normal milking starts the configured cooldown. Existing cooldowns are preserved and corrupt records are rejected.
- Added Orb payload v4 while retaining byte-for-byte v3 compatibility on read/reserialization. Orb networking now uses protocol v3: install 1.1.6 on the server and every client. Newly captured v4 Orbs require this update.
- Added managed cooldown, compatibility and network regression coverage. Actual host/dedicated multiplayer behavior still requires in-game verification; separate world and character saves are not made atomic.

## 1.1.5

- Fixed Feed Barrel food placement on raised building floors. Food candidates now use nearby terrain or building surfaces instead of being projected onto terrain underneath the structure.
- Limited surface searches to 1 metre above or below the creature's feet, preserving the existing food-search range, path checks, and normal eating behavior. Unsupported points, steep or Rigidbody-backed surfaces, and the barrel itself are skipped before removing food; trigger volumes are ignored.
- Added 40 managed regression checks for floor selection, roof and lower-floor separation, obstacles, and inventory preservation. Actual Unity physics, pathfinding, and multiplayer feeding still require in-game verification.

## 1.1.4

- Fixed Taming Orb transaction tracking across inventory item copies and transfers. Successful release updates the current matching Orb item, clearing its creature data and icon before allowing another capture.
- Added persistent pending Orb state and per-world server transaction receipts. Pending Orbs can reconcile confirmed outcomes when used again; uncertain capture or release outcomes stay blocked instead of blindly retrying creature creation or restoring a filled Orb.
- Verified releases using stable transaction markers rather than the creature's current position, and delayed capture confirmation until the server observes the target's deletion. Hardened duplicate, late-reply, ownership-change, and reconnect handling.
- Kept the existing v3 creature payload while moving Orb networking to protocol v2. Install 1.1.4 on the server and every client, and back up the Orb transaction journal together with the world. This does not repair duplicates created before the update or make separate world and character saves atomic.
- Updated the BepInEx package dependency to 5.4.2351 and added managed Orb persistence and item-state regression checks.
- Retained the previous local-cache horizontal breeding limit. The experimental server population query is archived in the source repository and is not included in this release; the known unloaded-zone boundary limitation remains.

## 1.1.3

- Updated FeedLikeGrandma for Valheim 1.0.7 using the original client and dedicated-server game assemblies instead of stale publicized references.
- Updated the embedded ServerSync baseline for Valheim 1.0.7 while preserving configuration keys, locking behavior, RPC identifiers, and wire formats.
- Updated Harmony targets for the six-argument item tooltip and the new `BiomeSector` input used when a player discovers a biome.
- Preserved creature inventory data in Taming Orbs using Valheim 1.0's byte-array ZDO storage. Invalid or obsolete string-only inventory data is rejected before capture or spawning to prevent item loss or duplication.
- Replaced direct access to non-public game members with public accessors where equivalent and cached Harmony accessors where necessary.
- Fixed the localization startup postfix target while retaining the existing localization helper API.
- Added client and dedicated-server API/IL checks and expanded Orb regression coverage for inventory byte preservation and malformed payload rejection.

## 1.1.2

- Prevented damaged or unsupported Taming Orb data from being overwritten by a new capture. Capture failures now restore item state only after the transaction has staged its own changes.
- Kept existing Orb release transactions valid when the recipe is disabled while waiting for confirmation, and ignored late failed approvals after spawning to avoid leaving both a released creature and a reusable filled Orb.
- Fixed Player argument forwarding in the Monstrum and MonstrumDeepNorth mount-jump compatibility patches.
- Rejected non-finite YAML numbers such as NaN and Infinity while keeping the previous feed rules active after invalid configuration changes.
- Removed redundant food-rule searches and simplified flight restrictions and Orb retries. Grouped manual-feed and icon code without changing Orb v3 serialization or RPC formats.
- Added regression checks for YAML configuration and Orb v3 saves, and supported Debug deployment through DeployToGame with independently controlled release packaging.

## 1.1.1

- Reworked manual feeding into a server-coordinated two-phase transaction with exact inventory reservations, target locks, idempotent retries, and owner-migration handling for reliable dedicated-server multiplayer.
- Made Taming Orb capture and release server-approved, preserving owner-authored creature state and verifying released network objects before clearing an Orb to prevent duplication and stale state.
- Blocked player-spawned creatures from Taming Orb capture and hardened YAML validation, runtime cleanup, riding checks, and the separated Orb icon pipeline.

## 1.1.0

- Kept synchronous Taming Orb and Feed Barrel prefab registration for dedicated-server persistence while deferring recipe and build-cost binding until the full `ObjectDB` is available, eliminating false missing-vanilla-ingredient warnings during startup.

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
