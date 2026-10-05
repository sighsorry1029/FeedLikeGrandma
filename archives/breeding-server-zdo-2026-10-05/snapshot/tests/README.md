# Regression checks

These checks call the built plugin's YAML parser and Orb serializer under .NET Framework 4.8. They do not initialize Unity, install Harmony patches, or run a game server. The test project is separate from the plugin solution and is never included in the mod DLL or release packages.

From PowerShell at the repository root, with the .NET SDK and .NET Framework 4.8 targeting pack installed:

```powershell
dotnet build FeedLikeGrandma.csproj -c Debug -p:DeployToGame=true
dotnet build tests/RegressionChecks.csproj -c Debug
./tests/bin/RegressionChecks.exe ./bin/Debug/FeedLikeGrandma.dll
```

Keep the built DLL beside its copied dependencies. The runner never resolves publicized copies. A failed check returns a nonzero exit code.

The 37 checks cover omitted versus empty overrides, item quality, current-level foods, invalid input, duplicate foods, culture-independent numbers, non-finite values, and a populated v3 Orb save fixture. The fixture verifies all fields and exact serialized bytes, including inventory, growth, saddle, CL&LC and OdinHorse data. Additional checks preserve opaque inventory bytes through the v3 base64 envelope and reject damaged base64 without changing the stored payload; they do not parse the game's item schema.

## Valheim 1.0.7 API checks

Use PowerShell 7 and Mono.Cecil 0.11.6 to run `Check-GameApi.ps1` with `-CecilPath`, `-PluginDll`, `-ManagedPath` and `-BepInExCore`. Run it separately for the original client and dedicated-server Managed directories. It examines the final merged DLL, including ServerSync, and fails on unresolved direct game references, direct non-public access, runtime constant-field loads, mismatched explicit Harmony targets/arguments/results, or mismatched cached private bindings. It also checks the hotbar's private nested fields and selected dynamic/reflection contracts. Optional mod patch targets and runtime IL detours are not executed.

This is metadata/IL validation, not a Unity/Mono execution test. The .NET Framework runner cannot instantiate the new game types that use default interface implementations, and the installed Harmony build does not initialize under the .NET 9 desktop runtime. Those standalone runtimes are not substitutes for the game's Mono runtime.

## Game regression scenarios

Use a disposable world and character. Repeat the networking scenarios on a host and on a dedicated server with two clients; for the latter, make the creature owner different from the requesting player. Record food stacks (including quality/custom data), Orb contents and creature counts before and after each transaction.

1. Feed ordinary and level-only foods, with the exact amount that crosses a level or growth boundary. Repeat after a YAML reload, including removing an override and applying an empty override.
2. For both tameable interaction and hotbar use, check empty, valid v3, malformed, truncated, empty-string and unsupported-version Orb payloads. Invalid data must retain its original text and must not capture a creature.
3. Change an Orb's payload while capture is still preparing. The request must fail without restoring an older item snapshot over the new data. Force a failure after FLG stages the item and verify rollback still restores the pre-staging item.
4. Delay a release result/acknowledgement after spawning, disable the recipe, and allow retries. The existing transaction must finish once; a new request while disabled must fail. A late failed approval after spawning must not remove the pending transaction or leave the released creature and a reusable filled Orb.
5. Retry and duplicate approvals/results, including immediate host responses. Check ownership migration, concurrent requests, inventory changes, death and disconnection. The Orb patch described below persists pending operations and server receipts; ambiguous crash/world rollback outcomes stay quarantined.
6. Check icons in inventory/hotbar and after dropping/picking up Orbs, then leave and re-enter the world. Check registered Orbs/barrels survive a dedicated-server restart with recipes disabled.
7. With the supported Monstrum and DeepNorth builds, test keyboard/controller mount jumps, ordinary dismount, mount death, and the saddle fallback. For supported BetterRiding builds, cross a restricted biome, change encumbrance and reload rules while holding/releasing the flight key.
8. On 1.0.7, capture/release a creature with a populated container, wait for its container to load, and verify every item's amount, quality and custom data after reconnecting and restarting the server. Repeat with damaged inventory base64: release must fail before spawning and retain the filled Orb. A creature whose inventory is still stored only in the obsolete ZDO string field must remain uncaptured. No mod-level legacy conversion is performed.
9. Restart the game, verify initialization and `flg:reference`, change language, and check inventory/hotbar/appended tooltips. Connect an ordinary client and an admin client to confirm ServerSync initial values, locking, admin-list updates, disconnect cleanup and reconnect behavior.

Do not interpret passing the console checks as proof of these runtime scenarios. In particular, the console runner does not measure allocations, validate RPC transport or execute optional mods.

## Orb transaction patch — 2026-10-05

Production changes preserve the v3 creature payload. The new `flg.tame.pending` item value follows vanilla ItemData copies; operations resolve the current item by OrbId and transaction ID. Old object-reference locks and the duplicate capture snapshot were removed. Missing or ambiguous item identities remain unresolved rather than modifying another item. A transferred orb reconciles on use in its original world.

Release completion verifies the transaction, OrbId, prefab, immutable creation origin and a ready marker written after creature restoration. It does not inspect the creature's current position or require its initial owner to remain unchanged. Capture success waits for server-observed ZDO deletion, and the server refuses capture of an uncommitted release. Durable capture markers preserve unresolved target identity across world loads; runtime ZDOIDs are not stable across server restarts.

The per-world journal is flushed before authorizing an operation and before reporting its result. Completed receipts do not expire with the RPC cache. Damaged/truncated files, write failures and uncertain outcomes block further execution. Previous-session release recovery finds the stable transaction marker in the world; previous-session capture success cannot be inferred from an old ZDOID being absent. That uncertain capture remains pending. An intact journal restored to an earlier state or deleted externally cannot be detected by its checksum; world and journal backups must be kept together. This does not make separate vanilla character/world saves atomic during a crash, and does not repair duplicates created before the patch.

### Automated checks performed

```powershell
dotnet build FeedLikeGrandma.csproj -c Debug -p:DeployToGame=true
dotnet build tests/OrbTransactionChecks.csproj -c Debug
dotnet build tests/OrbItemStateChecks.csproj -c Debug
./tests/bin/orb-transactions/OrbTransactionChecks.exe ./bin/Debug/FeedLikeGrandma.dll
./tests/bin/orb-item-state/OrbItemStateChecks.exe
python tests/run_orb_mono.py <original-game-directory> ./bin/Debug/FeedLikeGrandma.dll <BepInEx-core-directory>
```

- Debug build: zero warnings/errors; final merged DLL copied to local game plugins and SHA-256 matched.
- Existing YAML/payload runner: 37 checks passed.
- New persistence runner: 102 checks passed against the actual plugin DLL, including both original 1.0.16 client (25527674) and dedicated server (25527701) Mono runtimes. Covers saved pending fields, journal reopen, world isolation, checksum/truncation, failed writes, immutable completion, conflicting/replayed operations and non-expiring consumed OrbIds.
- New item-state runner: 33 checks passed using the production state-helper source with plain ItemData/Inventory fixtures. Covers slot-copy lookup, result/rollback, transfer, duplicate IDs, invalid state and late/different-transaction protection. It does not execute the game's actual inventory events or cloning.
- Original client/server API checks: 1,499 direct references, 58 Harmony targets/arguments, 47 cached private bindings, hotbar fields and 13 dynamic/reflection contracts passed.

### Still required in the game

Use a disposable world on a host and a dedicated server with two clients, all using this build. Record creature count, OrbId, pending transaction and contents before/after each case. Unity/Harmony detours, transport, owner migration, inventory events and optional-mod restoration were not executed by the automated checks.

1. Deliver the release result before its ZDO, then move/fall more than 2 m before the retry. Expect one creature, one empty reusable orb and an updated icon.
2. Move the orb to another slot between spawn/staging and acknowledgement. Repeat with drop/pickup, container transfer and death. Use transferred pending orbs to reconcile; only the matching operation may change their contents.
3. Have the other client capture the released animal before its release is confirmed. It must be rejected until confirmation, then succeed normally.
4. Delay acknowledgements beyond 2 and 5 minutes, duplicate/reorder replies, disable the recipe during a commit, and change the creature owner. No second spawn or premature rollback is allowed.
5. Disconnect before spawn, after spawn, after server completion and before item confirmation; repeat with server restart and zone unload/reload. Confirm saved outcome reconciliation or explicit quarantine, never blind replay. A server-restarted pending capture without deletion evidence is expected to remain quarantined.
6. Inject failures during creature restoration, after the ready marker, during inventory notifications and during journal writes. A post-ready UI failure must not destroy the released creature. A failed operation must not leave both a reusable filled orb and its creature.
7. Restore mismatched world/character/journal backups in a disposable copy to verify conservative handling. Never use deleting the journal as an unlock operation.

No Release package, version bump, commit, push or live dedicated-server deployment was performed for this patch.

## Breeding zone-boundary patch — 2026-10-05

`FeedProcreationSystem` requests a server ZDO population check at the existing Procreate tick. It no longer maintains a local ZNetView population cache or patches every ZNetView Awake/OnDestroy. Positive radii up to 256 m use surrounding sectors and an exact X/Z filter. Larger and unlimited radii use the game's incremental prefab iterator, yield between batches and deduplicate repeated ZDOIDs. The existing same-adult-and-offspring-prefab counting policy remains weighted twice.

Checks authenticate the connected parent owner, tamed/startsTamed state, server prefab and offspring, and a nearby request position. The live owner's radius/limit are preserved; this is not server enforcement against modified clients. Replies must come from the server and match one pending nonce, live instance, owner revision, rules, position (within 1 m) and five-second lifetime. Reset/disconnect invalidates outstanding work. A missing response skips the attempt and permits a new request on a later normal tick. No pregnancy, love-point or saved-data format is replaced.

Source-linked tests exercise the production implementation with controlled ZDO, RPC and coroutine fixtures:

```powershell
dotnet build tests/BreedingChecks.csproj -c Debug
./tests/bin/breeding/BreedingChecks.exe
dotnet build tests/BreedingHarmonyChecks.csproj -c Debug
python tests/run_orb_mono.py <original-game-directory> ./bin/Debug/FeedLikeGrandma.dll <BepInEx-core-directory> ./tests/bin/breeding-harmony/BreedingHarmonyChecks.exe
```

The separate original-Mono check patches the original game's private Procreate with a stop prefix and invokes the built plugin's cached delegate twice. It tests private access and Harmony dispatch, including a delegate cached before patching. It deliberately does not execute the original method body. Embedded Mono without Unity can print unresolved Unity internal-call diagnostics while JIT-compiling that body; the reported result covers only interception. This is not a Unity or multiplayer run.

Verified for this patch: Debug plugin build with `DeployToGame=true` had zero warnings/errors and the final merged DLL matched the local plugins copy by SHA-256 (`603BDF4CFB55A9A60F4273484A8DDAC59BF81C5663969F54112B5780304EDC9E`). All 178 source-linked breeding checks and the 37 existing YAML/payload checks passed. Original 1.0.16 client and dedicated-server metadata checks passed (1,588 direct references, 56 explicit Harmony targets, 48 private bindings and the existing dynamic contracts). The cached-delegate interception check passed on both original Mono runtimes. Real host/dedicated-server gameplay remains untested.

Required runtime scenarios (server and every client must use this build):

1. Use a disposable world, server default simulation distance and a client with minimum simulation distance. Relative to that client's zone center, place parents around X=95 and population animals around X=97. Keep a second client near the latter; verify the first client still owns the parents. Repeat at equal height, with offspring below the parents, and with negative coordinates/zone corners. Verify the actual prefab radius includes both groups.
2. Compare below-limit and at-limit populations, remove an offspring, and move the whole setup away from the boundary. Verify regular feeding/chance/partner rules and existing pregnancies still behave normally. Repeat on a host and in single player.
3. Delay/drop/duplicate responses, transfer ownership (including away and back), unload the parent, disconnect/reconnect and reload YAML while a check is pending. Verify no repeated Procreate advancement or stale approval.
4. Test ordinary offspring, startsTamed mod creatures and identical parent/offspring prefab policies with CreatureManager. The initial blocked Harmony call and approved resumed call both traverse other mods' patches; check any additional Procreate patches installed on the server. The existing CreatureManager birth path is bypassed by neither call.
5. Test zero and large radii separately. Zero now counts matching server-world creatures globally. Incremental large-range queries are not atomic snapshots of movement/births during the scan; ordinary small-range queries finish in one frame. Delayed world updates may briefly produce conservative or stale counts, and already pregnant animals are intentionally not reserved against the limit.
