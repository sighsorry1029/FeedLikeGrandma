# FeedLikeGrandma

Feed your creatures to tame, grow, heal, and level them up. Carry tames in reusable Taming Orbs, supply food with Feed Barrels, and see what each creature needs at a glance.

[User guide](https://github.com/sighsorry1029/FeedLikeGrandma/blob/main/docs/user-guide.md) · [Configuration](https://github.com/sighsorry1029/FeedLikeGrandma/blob/main/docs/user-guide.md#yaml-configuration)

Click any image or animation to view the original.

## See it in action

### Feed and grow

| Tame a creature | Raise its offspring |
| --- | --- |
| <a href="https://i.ibb.co/bMwcTLQP/feedtotame.gif"><img src="https://i.ibb.co/bMwcTLQP/feedtotame.gif" alt="Feeding a wild creature until it is tamed" width="320"></a> | <a href="https://i.ibb.co/sddwcmmd/feedtogrow.gif"><img src="https://i.ibb.co/sddwcmmd/feedtogrow.gif" alt="Feeding a juvenile to speed up growth" width="320"></a> |

Use suitable food to advance taming or growth while restoring health and adding fed time.

| Level up a tame | Check food and progress |
| --- | --- |
| <a href="https://i.ibb.co/XfLWnd4q/levelup.gif"><img src="https://i.ibb.co/XfLWnd4q/levelup.gif" alt="Feeding an adult tame to increase its level" width="320"></a> | <a href="https://i.ibb.co/mLRtDGb/feedwithhand.png"><img src="https://i.ibb.co/mLRtDGb/feedwithhand.png" alt="Manual feeding with food choices and progress" width="320"></a> |

Adults can gain levels through feeding. Set level limits, food values, and special foods for selected levels.

### Feed Barrels

| Automatic feeding | Build a Feed Barrel |
| --- | --- |
| <a href="https://i.ibb.co/nq2MNX2m/autofeed.gif"><img src="https://i.ibb.co/nq2MNX2m/autofeed.gif" alt="A hungry creature collecting food from a Feed Barrel" width="320"></a> | <a href="https://i.ibb.co/9mjyJdDP/feedbarrel-craft.png"><img src="https://i.ibb.co/9mjyJdDP/feedbarrel-craft.png" alt="Feed Barrel in the Hammer build menu" width="320"></a> |

Fill a nearby barrel with suitable food. Hungry, calm creatures collect and eat it using their normal movement and eating behavior.

### Taming Orbs

| Capture and release | Find the right creature |
| --- | --- |
| <a href="https://i.ibb.co/MxZKWv63/tamingorb.gif"><img src="https://i.ibb.co/MxZKWv63/tamingorb.gif" alt="Capturing and releasing a tame with a Taming Orb" width="320"></a> | <a href="https://i.ibb.co/7dfbBWQb/tamingorbs.png"><img src="https://i.ibb.co/7dfbBWQb/tamingorbs.png" alt="Filled Taming Orbs with creature icons" width="320"></a> |

Store one tamed creature in each reusable orb. Filled orbs show the creature on their icon.

### Riding

| Dragon flight restrictions | Monstrum mount jumps |
| --- | --- |
| <a href="https://i.ibb.co/RpXMjN1x/dragonbiomerestriction.gif"><img src="https://i.ibb.co/RpXMjN1x/dragonbiomerestriction.gif" alt="Dragon flight blocked in a configured biome" width="320"></a> | <a href="https://i.ibb.co/yFYKng0D/monstrumtamejump.gif"><img src="https://i.ibb.co/yFYKng0D/monstrumtamejump.gif" alt="A supported Monstrum mount jumping with its rider" width="320"></a> |

Tune saddle stamina and encumbrance behavior, with additional options for supported riding mods.

## What you get

| Feature | What it does |
| --- | --- |
| Feeding progression | Food advances taming, growth, or adult levels, and restores health and fed time. |
| Food display | Shows accepted foods, quality requirements, and current progress. |
| Feed Barrels | Supply nearby hungry creatures with food they can walk to and eat. |
| Taming Orbs | Carry a tame with its name, level, health, inventory, saddle, and supported mod data. |
| Custom rules | Set food values, special level-up foods, growth times, and breeding limits. |
| Riding and taming boosts | Adjust saddle stamina, riding-skill bonuses, and TamingBoost effects. |

## Start here

| To… | Do this |
| --- | --- |
| See accepted foods | Crouch and look at a creature. Food icons require crouching by default. |
| Feed a creature | Put suitable food on your hotbar, look at the creature, and use it. |
| Automate feeding | Build a **Feed Barrel** from the Hammer's Misc category and fill it with suitable food. |
| Capture a tame | Use an empty **Taming Orb** on a living, tamed creature within 5 metres. |
| Release a tame | Use a filled orb from the hotbar while aiming at a nearby surface. |
| Customize a creature | Copy its block from `feed.reference.yml` into `feed.yml` and keep only the fields you want to change. |

A feed action can use **up to 10 food items by default**. Change **Max Items Per Feed** to suit your needs.

Keep the orb in your inventory during the 4.5-second effect. For Feed Barrels, leave a clear walking route and keep the barrel and creature near the same floor height.

## Installation

Install with a Valheim mod manager, or place `FeedLikeGrandma.dll` in `BepInEx/plugins/FeedLikeGrandma/`. Requires **BepInExPack Valheim 5.4.2360**.

In multiplayer, install the **same FeedLikeGrandma version on the server and every client**. Start the game and enter a world once to generate the configuration files.

Orb transaction journals belong to the host/server's Valheim save root (`FeedLikeGrandma/orb-transactions/<world UID>.journal`), so profiles using the same world/save root share the history. A valid journal in the current profile's old config folder is automatically copied on the first Orb transaction/recovery; the original is preserved. Conflicting histories are not overwritten or merged. Back up the active shared journal with your world. See the [Taming Orb guide](https://github.com/sighsorry1029/FeedLikeGrandma/blob/main/docs/user-guide.md#taming-orb) for migration boundaries.

## Configuration

Paths are relative to `BepInEx/config/`:

| File | Use it for |
| --- | --- |
| `sighsorry.FeedLikeGrandma.cfg` | Recipes, progression, riding, display settings, and optional integrations. |
| `FeedLikeGrandma/feed.yml` | Your food values and creature overrides. |
| `FeedLikeGrandma/feed.reference.yml` | Generated reference for loaded vanilla and modded creatures. |

Edit `feed.yml`, not the generated reference. Both the config and YAML reload automatically; server-synced settings and YAML follow the server in multiplayer.

For an Orb problem, set `[Debug] OrbDiagnostics = On`, reproduce the problem, then set it `Off`. No hotkeys are needed: Orb use or related traffic automatically starts a trace with before/after inventory reports. Traces save after 60 seconds or when disabled/disconnected. Reports are in `BepInEx/config/FeedLikeGrandma/diagnostics` in the running profile. The setting defaults to Off and is local; enable it separately on a remote server for server evidence. See [Orb diagnostics](https://github.com/sighsorry1029/FeedLikeGrandma/blob/main/docs/user-guide.md#orb-diagnostics) for collection, storage limits and removal of the old standalone diagnostic DLLs.

Many creatures work with their usual foods without a custom block. Foods use a value of **1** unless overridden. See the [configuration guide](https://github.com/sighsorry1029/FeedLikeGrandma/blob/main/docs/user-guide.md#yaml-configuration) for examples.

## Integrations and support

- **BetterRiding, DragonRiders, and Monstrum:** additional handling for supported mount actions and flight rules.
- **Creature Level and Loot Control / OdinHorse:** supported modifier and armor data preserved in Taming Orbs.
- **ValheimCuisine:** Lox milking cooldown handling and Orb state preservation.
- **STUWard:** Taming Orbs follow configured ward item restrictions. Update both mods on the server and clients.
- **Expand World Data:** custom biome names in flight restrictions.

These mods are optional. See the [integration guide](https://github.com/sighsorry1029/FeedLikeGrandma/blob/main/docs/user-guide.md#optional-compatibility) for requirements and limits.

If an orb has an unfinished action, use it again to check its saved result. Keep `FeedLikeGrandma/orb-transactions/` with world backups; do not delete these files to reset an orb. [Orb recovery details](https://github.com/sighsorry1029/FeedLikeGrandma/blob/main/docs/user-guide.md#taming-orb).

[GitHub](https://github.com/sighsorry1029/FeedLikeGrandma) · [Report an issue](https://github.com/sighsorry1029/FeedLikeGrandma/issues) · [Discord](https://discord.gg/jkcJCq2sK5) · [Changelog](https://github.com/sighsorry1029/FeedLikeGrandma/blob/main/Thunderstore/CHANGELOG.md)
