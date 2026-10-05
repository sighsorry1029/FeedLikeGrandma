# Valheim 1.0.7 compatibility patch — 2026-09-09

Baseline: FeedLikeGrandma 1.1.2, commit `28152bd`, previously compiled against publicized 0.221.12 references. Release target: FeedLikeGrandma 1.1.3 on the installed Windows x64 client 1.0.7 (Steam build 25185596) and dedicated server 1.0.7 (25185644). This does not claim support for other 1.0 builds or Linux.

## Findings and changes

| Finding and evidence | Minimal change and preserved behavior |
| --- | --- |
| Provided LogOutput.log, lines 226–235: FeedLikeGrandma.Awake fails while constructing CustomSyncedValue because the old ServerSync uses `ldsfld ZRoutedRpc.Everybody`. Original 1.0.7 metadata declares `public const long Everybody = 0`. | Vendor the reviewed `valheim-1.0.7-r1` common baseline compiled from source against the original game DLLs. It also preserves 1.0.7 login-list message ordering. Settings, locking, RPC formats and peer validation remain ServerSync contracts. See `Libs/ServerSync-1.0.7.md`. |
| Static ItemData.GetTooltip now has a sixth `bool appending` argument. | Target the exact six-argument overload. Keep the existing Orb tooltip policy and item/crafting/result forwarding. The new flag skips parts of the game's base description; it does not replace the Orb's contents display. |
| Player.AddKnownBiome now receives BiomeSector, whose `Biome` field is the base biome enum used by existing flight restrictions. | Pass `biome.Biome` to the existing restriction code. Keep optional BetterRiding preparation and its biome policy; do not invent alternate-biome rules. |
| Container.Save/Load now use `ZDO.Set(int, byte[])` / `GetByteArray(int)`. CapturePayload/RestorePayload previously accessed only the string slot. This is a source-confirmed data-loss risk, not a reproduced loss from the supplied startup log. | Capture current inventory bytes as base64 inside the unchanged Orb v3 string field; restore them to the byte-array ZDO slot. Decode base64 before spawning. Invalid base64 follows the existing failed-release transaction and retains the Orb. String-only creature inventories are rejected before capture instead of discarded or migrated. The underlying Inventory save/load is still owned by the game. |
| Original DLL compilation reveals reliance on non-public game fields/methods. The existing publicized references no longer establish either target API or runtime access. | Compile against original assembly_valheim/assembly_utils/assembly_guiutils. Use GetBaseAI/GetCollider/GetZAnim/GetInventory/GetDoodadController/GetBuildTool/GetPosition where they expose the same state. Put remaining field references and method delegates in GameAccess, resolved once. Preserve root-overridden Container network views, RPC handler registries, owner checks and mutable growth/prefab state. |
| Localizer's manually installed FejdStartup.SetupGui postfix declared `Localization __instance`, although the patched instance is FejdStartup. This signature mismatch also existed before 1.0. | Install a parameterless adapter using Localization.instance and its selected language, retaining the existing public helper signature. This is a scoped startup correctness fix, separate from the game's API changes. |
| Manifest used BepInExPack 5.4.2333. | Set the package source manifest dependency to 5.4.2350 as required by the project instructions. No BepInEx installation or other mod was changed. |

## Reviewed boundaries

- Main plugin, configuration/YAML reloads, FeedPatches, manual feeding, barrel registration/feeding, growth/procreation, riding/flight/Monstrum adapters, Orb capture/release/icon systems, localization, build settings, manifest and test runner. Resource names and registration call paths were reviewed; Unity asset contents were not dumped or rendered.
- Original Growup and Procreation C# are unchanged between the snapshots. Tameable's feeding/saddle APIs used here retain their behavior; its diff adds interaction statistics, changes a summon-limit text field and removes death logging. EggGrow adds a hover offset. No new growth/procreation replacement was needed.
- GameAccess preserves cached game state rather than substituting GetComponent searches. Per-frame paths add no reflection lookup, object-array Invoke or UI recreation. Hotbar nested-type fields use cached FieldRef delegates; field enum access uses its verified Int32 representation. Actual allocation/frame timing was not measured.
- Existing Harmony priorities and state forwarding, Unity initialization/destruction and watcher/event cleanup remain in place. The patch does not replace the transaction state machine, timeouts, item reservations, ownership checks, sender authentication, transaction IDs or duplicate handling.
- Setting keys, custom data keys, recipe identities, translations, Orb v3 field order and network message formats remain unchanged. Current byte-array inventory data passes through the v3 base64 envelope. No 0.221.x runtime fallback or mod-level legacy save migration was added.
- ServerSync's explicit type/method-level patches and selected reflected admin/socket/peer members were checked against both target DLLs. Existing connection-status enum values are unchanged. Dynamic status-effect targets and localization startup methods were inspected as well.
- Optional BetterRiding, Monstrum/DeepNorth, CreatureManager, CL&LC, OdinHorse and external biome APIs retain their adapters. Their installed runtime behavior and their own 1.0 compatibility were not established. FeedLikeGrandma has no direct Jotunn dependency; no Jotunn replacement or unrelated mod patch was performed.

## Verification

| Category | Result and limits |
| --- | --- |
| Build | `dotnet build FeedLikeGrandma.csproj -c Debug -p:DeployToGame=true`: succeeded, zero warnings/errors, ServerSync/YamlDotNet merged. Final plugin copied to the game plugins folder; source/destination SHA-256 checked. No Release, ZIP, version bump, commit or push performed for this patch. |
| Data regression tests | .NET Framework runner: 37 checks pass, including unchanged v3 golden bytes, inventory byte/base64 round trips and malformed base64 rejection. This does not execute the game's Inventory parser or native code. |
| API/IL tests | On client and dedicated original DLLs separately: direct game-reference instructions, explicit Harmony targets with argument/result checks, private bindings, hotbar nested fields and additional dynamic/reflection contracts are checked in the final merged DLL. The common ServerSync baseline has its own source-build, static and isolated transport tests; neither result is a real game run. |
| Reference integrity | Installed client assembly_valheim/assembly_utils/assembly_guiutils and UnityEngine.CoreModule SHA-256 match the preserved 1.0.7 originals. Game assembly_valheim SHA-256: `A5130F5A957AB51CB6538F5412CBE57B43F927F4A679918BFF199B5C905D01BC`. No game DLL was publicized or overwritten. |
| Actual execution | Only the supplied pre-patch 1.0.7 startup log was examined. The 1.1.3 plugin has not been executed in Unity/Mono, a client world, host or dedicated server. Desktop .NET tests are not equivalent to that runtime. |

Before declaring runtime compatibility, restart the game and perform the scenarios in `tests/README.md`, especially populated creature inventories, capture/release retries and owner migration with two clients, configuration/admin synchronization, language changes, tooltips and optional riding mods. Preserve disposable test worlds and compare exact inventory/creature counts before and after reconnect and server restart. Other mods' startup errors in the supplied log remain outside this patch.

## Original evidence

The preserved snapshots under `C:/Users/blizz/.codex/references/valheim/snapshots/` are:

- Client baseline: `client-b21981559-windows-x64-20260909T123305Z`.
- Client target: `client-b25185596-windows-x64-20260909T131109Z`.
- Dedicated baseline: `dedicated-server-b21981590-windows-x64-20260909T123305Z`.
- Dedicated target: `dedicated-server-b25185644-windows-x64-20260909T131109Z`.

For each, analysis uses `derived/ilspy-9.1.0.7988-r1/assemblies/<assembly>/csharp`, `metadata.jsonl` and `assembly.il`; originals are under `original`. Existing extraction and integrity reports were reused, not overwritten or regenerated. Official 1.0 notes were used only to identify inventory, biome, UI and item-data review areas; the patch decisions above use target DLL evidence. Generated bin/obj/ZIPs, unrelated mods, full third-party source audits, native Unity code and asset internals are excluded.
