# FeedLikeGrandma user guide

[Back to overview](../README.md)

[Feeding](#core-progression) · [Configuration](#yaml-configuration) · [Feed Barrels](#automatic-feeding-and-the-feed-barrel) · [Taming Orbs](#taming-orb) · [Integrations](#optional-compatibility)

## Core progression

Each food has a `foodNumber`. The active feed constants convert that number into five effects:

| Effect | Calculation | Result |
| --- | --- | --- |
| Level | `foodNumber * levelupconstant` | Level progress |
| Taming | `foodNumber * tamingconstant` | Seconds removed from taming time |
| Growth | `foodNumber * growthconstant` | Seconds added to growth progress |
| Healing | `foodNumber * healconstant` | Percent of maximum health restored |
| Fed | `foodNumber * fedconstant` | Seconds added to the fed timer |

The current creature state decides which progression effect is relevant:

- A wild tameable gains taming progress.
- A tamed creature with `Growup` gains growth progress.
- A fully grown, tamed creature gains level progress.
- Every accepted daily or level-up food can restore health and add fed time.

Manual feeding can consume several matching items from the selected stack, up to `Max Items Per Feed`. When the amount would cross a level boundary, FeedLikeGrandma consumes only enough items for that boundary and grants at most one level per feed action.

## Quick start

1. Install BepInEx for Valheim.
2. Put `FeedLikeGrandma.dll` in `BepInEx/plugins/FeedLikeGrandma/`.
3. In multiplayer, install the same mod version on the server and every client.
4. Start the game and enter a world once.
5. Edit `sighsorry.FeedLikeGrandma.cfg` in `BepInEx/config/` and `feed.yml` in `BepInEx/config/FeedLikeGrandma/`.
6. Use a valid food item on a creature, or build a Feed Barrel for automatic feeding.

The mod creates these paths under `BepInEx/config/`:

| File | Purpose |
| --- | --- |
| `sighsorry.FeedLikeGrandma.cfg` | General, feeding, leveling, riding, and compatibility settings |
| `FeedLikeGrandma/feed.yml` | Your active feed and prefab overrides |
| `FeedLikeGrandma/feed.reference.yml` | Generated read-only reference for loaded vanilla and modded prefabs |
| `FeedLikeGrandma/cache/icons/` | Reusable Taming Orb creature icon cache |

Both the BepInEx config and `feed.yml` reload automatically after a short debounce. When connected to a server, the server copy is authoritative and local client YAML changes are ignored.

## Feeding without prefab overrides

A prefab block is not required for every creature.

If a tameable already has a native or inherited `consumeItems` list, FeedLikeGrandma uses it with the global `defaults.feed` constants. A food uses its per-creature value, then its global `foodNumbers` value, or **1** if neither is set. This lets vanilla and many modded creatures work without a custom block.

Offspring such as cubs and calves also inherit consume items from their grown prefab when they have no consume items of their own. Existing child values are never overwritten by this inheritance.

## Hover information

Looking at a supported creature displays the progress that matters for its current state:

- `Tame: current/total`
- `Growth: current/total`
- `Fed: remaining/maximum`
- `Level current->next: progress/required`
- Locked or maximum-level states

Food icons are split into two rows:

- Daily food appears directly above the creature text.
- Current-level `levelUpItems` appear on the row above it with a `Lv current->next` label.

The top-right badge shows required item quality, when applicable. The bottom-right badge shows its food number. By default, icons are shown while crouching so the normal hover display stays clean.

## YAML configuration

`feed.yml` is an override file. Omitted prefab fields keep the value supplied by Valheim or the creature's source mod. Copy only the blocks you need from `feed.reference.yml`.

### Basic example

```yaml
defaults:
  feed: [20, 15, 30, 25, 120]

foodNumbers:
  - Mushroom
  - Raspberry
  - Honey, 2
  - NeckTail, 1.5
  - NeckTailGrilled, 2

Wolf:
  consumeItems:
    - RawMeat
    - Sausages, 5

Asksvin:
  levelUpItems:
    2:
      - Fish11, 300

DModer_Ygg4_Elder:
  levelUpItems:
    4:
      - Fish4_cave@2, 500
  mountStamina: [150, 80, 0.75]
  restrictedBiome: [Ashlands]
```

In `foodNumbers`, an item without an explicit value uses `1`, so `- Mushroom` is equivalent to `- Mushroom, 1`.

### Food syntax

```yaml
consumeItems:
  - RawMeat              # Uses the global value, or 1 if unset.
  - Sausages, 5          # Uses a local food number of 5.
  - Fish1@5, 500         # Requires item quality 5 and uses value 500.
```

A non-empty `consumeItems` block also replaces the prefab's native MonsterAI consume list when that component exists. Creatures without MonsterAI can still receive these foods manually, but cannot walk to and consume them automatically.

### Level-up-only food

`levelUpItems` is keyed by the creature's current Character level, not its displayed star count.

```yaml
DragonPrefab:
  levelUpItems:
    3:                   # Character level 3 -> 4; stars 2 -> 3.
      - Fish2, 300
    4:                   # Character level 4 -> 5; stars 3 -> 4.
      - Fish1@5, 500
```

When a row exists for the current level, only food in that row grants level progress. Daily `consumeItems` remain valid for healing and fed time, and are not replaced. A level-up-only food also restores health and fed time even when it is not listed as daily food.

If no row exists for the current level, daily food can grant level progress normally.

### Supported prefab fields

| Field | Values | Purpose |
| --- | --- | --- |
| `feed` | `[level, tame, growth, heal, fed]` | Per-prefab replacement for `defaults.feed` |
| `consumeItems` | Food list | Daily food and optional local food numbers or quality requirements |
| `levelUpItems` | Current level to food-list mapping | Full override of level-progress food for selected levels |
| `tame` | `[fedDuration, tamingTime, startsTamed, commandable]` | Tameable component values |
| `procreation` | `[maxCreatures, totalCheckRange]` | Breeding population limit and range |
| `grow` | `[growTime]` | Growup duration in seconds |
| `eggGrow` | `[growTime]` | EggGrow duration in seconds |
| `mountStamina` | `[attackCost, jumpCost, attackCooldown]` | Per-prefab BetterRiding action settings |
| `restrictedBiome` | Biome list | Per-dragon BetterRiding flight restrictions |

Use `restrictedBiome: []` to opt a specific dragon out of the global restricted-biome list.

## Level progression

FeedLikeGrandma uses `Tameable.m_tamingTime` as the base requirement for both taming and later level progression.

With `Level Taming Time Multiplier = M`, the requirement for target Character level `L` is:

```text
required = tamingTime * (1 + (L - 1) * (M - 1))
```

Character level 1 is zero stars, level 2 is one star, and so on.

The default `Vanilla` level-limit mode keeps `Lox` and `Hen` at level 1 and allows other supported creatures to reach level 3. Select `Level` mode and set `Level Up Max Level` to use a custom cap. Star HUD rendering remains the responsibility of Valheim or an installed level-display mod.

Fed time stacks instead of being reset to one fixed duration. Its maximum also scales by creature level using `Level Fed Duration Multiplier`, and the remaining time is shown on hover.

## Automatic feeding and the Feed Barrel

Automatic feeding applies the same taming, growth, level, healing, fed-time, quality, and level-specific food rules as manual feeding.

This includes food consumed naturally from the ground. The Feed Barrel extends that flow without bypassing MonsterAI:

1. A hungry, calm creature runs its normal food search.
2. If no suitable dropped item is found, FeedLikeGrandma checks nearby Feed Barrels inside that search range.
3. It selects valid food for the creature's current state.
4. It removes one item and creates a real dropped item at a reachable point beside the barrel.
5. The creature paths to the food and consumes it normally.

This approach keeps the visible eating animation and normal pathfinding behavior. Place the barrel inside the creature's own food-search range and leave a clear walkable space around it. The barrel is ignored while open, when it contains no valid food, or when the creature is alerted.

Food can be placed on terrain or building floors. Nearby drop points must have a solid surface within 1 metre above or below the creature's feet and pass its normal path check. Keep the barrel and animal near the same floor height; unsupported edges and larger height differences are skipped instead of dropping food onto distant terrain below.

When AzuAutoStore is installed, food dispensed by the Feed Barrel is excluded from its automatic ground collection. The animal still walks to the actual food and consumes it through its normal AI. Ordinary dropped items, Azu's inventory-storage hotkeys, and automatic supplies put into the barrel follow the existing Azu settings. No Azu DLL or YAML change is required.

Protection belongs to that world item, not its inventory data. It survives world saves, zone reloads and ownership changes; after a player picks the food up, dropping it again creates an ordinary item. Newly dispensed food is marked on every FLG peer, including a server without Azu, because another client can perform automatic collection. Protected and ordinary world stacks are kept separate; two protected stacks or two ordinary stacks still use the game's normal merging rules. Existing unmarked food already lying on the ground is not retroactively classified as barrel food.

Install the same patched FLG build on the server and every client. Azu ground collection runs on clients, so updating only the dedicated server is insufficient. Compatibility activates automatically; no server protection setting is required. The inspected Azu DLL is 3.1.7; activation checks the required collection methods and installed patches rather than requiring that version or DLL hash.

Other mods can extend ground-stack merging while FLG keeps protected and ordinary food separate. FLG checks the combined merge path instead of rejecting every other merge transpiler or maintaining a list of allowed mod names. If the required collection or merge contract cannot be established, FLG logs the reason and stops new local barrel dispensing before taking stock; merge-validation failures also identify the relevant patch owners and methods. Manual feeding and ordinary storage remain available. Runtime DLL replacement is unsupported.

The Feed Barrel appears in the Hammer's Misc category. Its recipe, material, columns, and rows are configurable. The default inventory is 6 by 2 and the default material uses `ectoplasm_mat`.

Default recipe:

```text
BarrelRings:1, Ectoplasm:3, RoundLog:6, ElderBark:9
```

Leave `Feed Barrel Recipe` empty to remove the piece and disable container auto-feeding.

## Taming Orb

The reusable Taming Orb stores one tamed creature for travel.

### Capture and release

1. Craft an empty Taming Orb.
2. Use it on a living, tamed creature within 5 meters.
3. Keep the orb in your inventory during the 4.5-second capture effect.
4. Put the filled orb on the hotbar and use it to release the creature.

Release aims at the surface under the crosshair up to 5 meters away and spawns the creature 1 meter above that point. If no surface is hit, it uses a close aim-based fallback in front of the player.

The orb preserves:

- Prefab, custom name, level, current and maximum health
- Saddle state and saddle stamina
- Creature inventory
- Fed age, spawn age, level progress, and growth progress
- Creature Level and Loot Control modifier, infusion, and extra effect when available
- OdinHorse armor state when available

A filled orb overlays the captured creature on the green orb icon. Rendered icons are cached under `FeedLikeGrandma/cache/icons/` and reused on later captures.

Orb operations retain a saved pending state until the server confirms their result. Moving an orb between inventory slots does not lose this state. If a pending orb is dropped, transferred, or recovered after reconnecting, use it to check the saved result before attempting another capture or release. An uncertain result keeps the contents protected instead of spawning another creature.

The server and every client must use the same build. The server stores operation receipts under `BepInEx/config/FeedLikeGrandma/orb-transactions/`, separately for each world. Keep these files with world backups; do not delete them to reset a pending orb. Recovery after a server restart requires matching world evidence. In particular, an unacknowledged capture across a server restart is kept pending when durable deletion cannot be proven. Already duplicated creatures and inconsistent orbs from earlier builds are not automatically repaired.

Default recipe and station:

```text
AncientSeed:3, Ectoplasm:5, SurtlingCore:7, Chitin:9
piece_workbench:1
```

Leave `Taming Orb Recipe` empty to disable the item.

## MeadTamer support

The Valheim `TamingBoost` status effect is integrated with all FeedLikeGrandma progression:

- Passive taming speed multiplier, default `x2`
- Passive growth speed multiplier, default `x1.5`
- Manual and automatic feed-effect multiplier, default `x1.5`

For passive growth and automatic feeding, any player with TamingBoost within 60 meters is enough; bonuses do not stack per player. Manual feeding checks the player performing the feed. The MeadTamer item and `Potion_tamer` status-effect tooltips show the active multipliers.

## Riding improvements

The riding section works with Valheim saddles and adds:

- Stable camera behavior while mounted
- Global saddle stamina drain and regeneration multipliers
- Riding-skill-based stamina regeneration
- Encumbered mount speed reduction and jump/run blocking
- Stamina drain while walking encumbered
- Separate encumbered drain and regeneration multipliers

Saddle regeneration uses:

```text
vanilla regen * normal-or-encumbered regen multiplier * riding skill multiplier
```

The riding skill multiplier scales linearly from `x1` at Riding 0 to the configured value at Riding 100. With the defaults, normal regeneration is `x3`, and the skill portion reaches `x5`, for a combined `x15` at Riding 100. Encumbered regeneration defaults to `x0`.

## Optional compatibility

- **STUWard 1.4.0+:** add `FLG_TamingOrb` to STUWard's `item_prefab_policy.blocked_item_prefabs` to restrict it in wards where the user lacks access. Checks cover the player and the capture or release position, so standing outside the ward does not bypass target protection. Use FeedLikeGrandma 1.1.7+ and update both mods on the server and every client. If STUWard is installed but its required API is unavailable, new Orb actions are blocked; with STUWard absent, normal Orb behavior remains.
- **ValheimCuisine:** Compatibility is detected from the required milking methods, typed settings and installed Harmony patches, without restricting the version number. Lox milking uses an owner-written persistent cooldown with server validation. Taming Orbs preserve the remaining cooldown and breeding eligibility. Install the same FeedLikeGrandma patch on the server and all clients; this Orb protocol cannot transact with older clients. Existing v3 Orbs remain readable, but newly captured v4 Orbs require this patch.
- **BetterRiding:** Per-prefab attack cost, jump cost, and attack cooldown; held-attack stamina protection; mount attack safety; feed/heal ordering; and water dismount support.
- **DragonRiders:** Water dismount support and BetterRiding dragon-flight integration.
- **Expand World Data:** Custom biome names can be used in `restrictedBiome`.
- **Monstrum / MonstrumDeepNorth:** Prevents Bear, Pridetusk, and supported mounts from dismounting the rider when the mount jumps.
- **Creature Level and Loot Control:** Taming Orb preserves modifier, infusion, and extra-effect data.
- **OdinHorse:** Taming Orb preserves equipped horse armor.
- **CreatureManager template and Jotunn creature registrations:** Loaded prefab and consume information is included in generated references when discoverable.

Dragon flight is blocked when the rider is encumbered if `Block Encumbered Dragon Flight` is enabled. The global restricted-biome list defaults to `Ashlands`; a dragon entering a restricted biome is forced out of flight, and new flight attempts are rejected with a center-screen message.

Cuisine cooldowns count down in world time while a Lox is outside an Orb, including while its zone is unloaded, and pause inside an Orb. Missing cooldown history, including an old Orb, adds no waiting time; the first successful milking records the normal configured cooldown. Existing saved cooldowns are preserved, and corrupt records are rejected. The normal breeding and interaction requirements still apply; old Orbs cannot recover breeding history that was never saved. Previously migrated Cuisine state survives Orb transfers even if Cuisine is temporarily absent. If a required integration member or patch is incompatible, Cuisine's original behavior is restored and Lox Orb transactions are blocked with a compatibility error. The inspected DLL is 2.3.2; matching API structure allows other versions to load, but does not guarantee unchanged behavior in every future version.

Milking and Orb capture are mutually exclusive. The owner records the cooldown before milk is granted, and a repeated network response cannot grant twice. An uncertain owner write keeps that Lox locked until the write is confirmed or the network session restarts. A disconnect or failure during delivery can consume the cooldown without delivering milk; it is intentionally not retried as a second grant. World and player saves are still separate. The game needs to be restarted after installing this patch.

The recent STUWard integration has build and automated-check coverage, with actual game and multiplayer acceptance still pending. See [ward compatibility checks](../tests/WardCompatibility.md) for validation details and recovery boundaries.

## Breeding limit protection

With `Horizontal Procreation Limit` enabled, adult and offspring population limits use horizontal X/Z distance instead of full 3D distance. This prevents vertical breeder designs from bypassing the configured `maxCreatures` and `totalCheckRange` while preserving ordinary horizontal farm layouts.

## Reference workflow

`feed.reference.yml` is generated from the currently loaded game data and grouped into Valheim, individual mod, and unknown-owner sections. It includes relevant prefabs with Tameable, Procreation, Growup, EggGrow, or consume data.

Recommended workflow:

1. Enter a world with all creature mods loaded.
2. Open `feed.reference.yml` and find the prefab you want.
3. Copy its block into `feed.yml`.
4. Remove fields you do not intend to override.
5. Add food numbers or `levelUpItems` as needed.
6. Save the file and let live reload apply it.

## More examples

Click an image to view the original.

| Taming Orb recipe | Taming boost |
| --- | --- |
| <a href="https://i.ibb.co/7x9t9pMq/tamingorb-craft.png"><img src="https://i.ibb.co/7x9t9pMq/tamingorb-craft.png" alt="Taming Orb crafting recipe" width="320"></a> | <a href="https://i.ibb.co/svRdn0Lx/tamerpotion.png"><img src="https://i.ibb.co/svRdn0Lx/tamerpotion.png" alt="Brew of Animal Whispers taming and growth bonuses" width="320"></a> |

| Daily foods | Growth and fed time |
| --- | --- |
| <a href="https://i.ibb.co/93S2BvXr/wolffood.png"><img src="https://i.ibb.co/93S2BvXr/wolffood.png" alt="Wolf food choices and food values" width="320"></a> | <a href="https://i.ibb.co/LznxtgjL/dragonfood.png"><img src="https://i.ibb.co/LznxtgjL/dragonfood.png" alt="A growing dragon's food and progress display" width="320"></a> |

| Separate food rows | Food quality requirements |
| --- | --- |
| <a href="https://i.ibb.co/VcybBWFC/asksvinfood.png"><img src="https://i.ibb.co/VcybBWFC/asksvinfood.png" alt="Asksvin daily food and level-up food rows" width="320"></a> | <a href="https://i.ibb.co/VcGLqfzg/levelupcanneedspecialfood.png"><img src="https://i.ibb.co/VcGLqfzg/levelupcanneedspecialfood.png" alt="Dragon level-up food with quality and value requirements" width="320"></a> |

<a href="https://i.ibb.co/Pz6NvhNR/levelupcanneedspecialfood2.png"><img src="https://i.ibb.co/Pz6NvhNR/levelupcanneedspecialfood2.png" alt="Asksvin with a special food for its next level" width="480"></a>

Assign special foods for selected levels without changing the creature's everyday diet.

## Building and checking changes

Build with the .NET SDK, the .NET Framework 4.8 targeting pack, and the Valheim/BepInEx references configured in `environment.props`. Builds merge ServerSync and YamlDotNet into the plugin DLL.

The [Valheim 1.0.7 adaptation notes](Valheim-1.0.7.md) describe that earlier compatibility update and its validation limits. They are historical notes, not a claim that every later game build has been tested.

For development and verification, use Debug and automatically copy the final merged plugin DLL to the configured game folder:

```powershell
dotnet build FeedLikeGrandma.csproj -c Debug -p:DeployToGame=true
```

Use `DeployToGame=false` to skip the game-folder copy. If omitted, it falls back to the legacy `DeployOnBuild` setting. `PackageOnBuild=true` remains the default for Release builds, which update the manifest and create release ZIPs. Run Release only for an intended release: a registered Mod Release Manager watch folder can automatically publish the generated ZIP. Packaging and game-folder deployment are independent.

See [regression checks](https://github.com/sighsorry1029/FeedLikeGrandma/tree/main/tests) for the .NET Framework test runner and the separate in-game scenarios.
