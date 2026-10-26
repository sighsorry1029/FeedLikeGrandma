# Regression checks

## Feed Barrel / AzuAutoStore protection (2026-10-26)

The barrel still removes one food, creates a real `ItemDrop`, and returns it to
the animal's normal walking/consumption flow. Its owner adds only the world-ZDO
boolean `flg.feedbarrel.food.v1`; inventory custom data and shared prefab data are
unchanged. A prefix on Azu's ground-store entry skips marked drops before storage
or ownership requests. Inventory hotkeys and destination-container rules are
unchanged. The existing vanilla stack loop skips mixed marked/unmarked pairs in
both directions, retaining normal rules for pairs with the same protection state.
No additional world scan, physics query, polling loop, or RPC was added.

The original inspected AzuAutoStore DLL is 3.1.7, SHA-256
`A609E7209832FB96AF11FE3A0F6C499D4ED54F599289D23DF0826A51BDE640EE`.
The original Valheim 1.0.20 client (25909357) and dedicated server (25909431)
assemblies are the game validation inputs. None were publicized or changed.
The Azu version/hash identify the sample, not an activation whitelist. Startup
checks the relevant collection routes and original stack-loop shape; each new
dispense checks the installed patch set before ownership or stock removal. A
matching structure is not proof that an arbitrary future implementation has
identical semantics. Unknown patches on these boundaries stop new local barrel
dispensing with a logged reason; normal manual feeding/storage remains available.
If marking an already-created drop fails, it is retained without refunding or
spawning a replacement, and subsequent dispensing stops.

Reproduce the focused checks with:

```powershell
dotnet build FeedLikeGrandma.csproj -c Debug -p:DeployToGame=true
dotnet build tests/FeedBarrelSurfaceChecks.csproj -c Debug
./tests/bin/feed-barrel-surface/FeedBarrelSurfaceChecks.exe
dotnet build tests/AzuDropProtectionChecks.csproj -c Debug
./tests/bin/azu-drop-protection/AzuDropProtectionChecks.exe
dotnet build tests/AzuCompatibilityChecks.csproj -c Debug
python tests/run_azu_mono.py <original-game-directory> ./bin/Debug/FeedLikeGrandma.dll <AzuAutoStore.dll> <BepInEx-core-directory>
```

The surface and marker runners use synthetic world/geometry/inventory objects.
The original-Mono runner loads actual assemblies and uses explicitly modified
in-memory Azu copies only to test compatibility acceptance/rejection. These are
not physical feeding, ZDO replication, or multiplayer tests.

Validation results:

- Pre-release Debug build/deploy and all three test-runner builds: zero
  warnings/errors. At that validation point, the final merged DLL and the local
  Steam `BepInEx/plugins/FeedLikeGrandma.dll` both had SHA-256
  `6BB50DF16EDE912D81793DEC2EF3091923E073E15DF7D9E22B0CE808348C0F45`.
- Surface/dispense runner: 48 checks passed; marker/merge policy runner: 24 passed.
- Original client and dedicated-server Mono: 26 checks passed on each. These
  cover actual Azu binding, harmless version/MVID/instruction-offset changes,
  rejected retry/RPC/partial-patch changes, and actual installation/readback/
  removal of the production transpiler on original `ItemDrop.AutoStackItems`.
  Removal ownership is checked with static cleanup IL and isolated Harmony
  targets; complete plugin `Start`/`Initialize`/`Shutdown` was not executed.
  Standalone Mono lacks the initialized BepInEx/ServerSync/Unity lifecycle.
- Final merged DLL API/IL checks passed against both original client and server:
  1,763 direct game references, 58 explicit Harmony contracts, 47 existing
  cached private bindings, hotbar fields, and 13 dynamic/reflection contracts.
  The new `ItemDrop.m_nview` private binding is additionally checked by the
  original-Mono runner, not included in the 47 existing bindings above.

Release 1.1.9 validation on 2026-10-26:

- `dotnet build FeedLikeGrandma.csproj -c Release` succeeded with zero
  warnings/errors. The assembly version is `1.1.9.0`, the package version is
  `1.1.9`, and the manifest retains BepInExPack Valheim `5.4.2351`.
- The Thunderstore ZIP contains exactly the six intended files, and the Nexus
  ZIP contains only the plugin DLL. Every packaged file matches its source bytes.
  The final Release DLL and the installed local Steam DLL match SHA-256
  `B40DAF38A4000E9DAEAC1B15F61BEE15986BE5E5419080E27F75C0A8AF2D5E85`.
- The final Release DLL passed the same client/server API checks and 26 original
  Mono checks on each platform. The existing source-linked runners passed
  48 surface/dispense checks and 24 marker/merge policy checks again.

Still required in a disposable host and dedicated world with two clients, all
using this patched FLG build:

1. With Azu absent and present, feed a hungry calm animal from a closed barrel on
   terrain and a building floor. Count barrel, ground, consumed, and nearby-chest
   food. Azu must leave new barrel food available to the normal animal AI while
   still collecting ordinary dropped food under its existing settings.
2. Test both creature/barrel ownership arrangements, Azu on only one client,
   ownership transfer, zone unload/reload, and world save/server restart. Check
   retained markers and unchanged item totals; a server-only update is insufficient.
3. With enough loaded drops to trigger vanilla auto-stacking (more than 200),
   test both receiver directions for marked/unmarked food, marked/marked food,
   and ordinary/ordinary food. Mixed stacks must remain separate. Other pairs
   must preserve vanilla quality/capacity/ownership rules and total counts.
4. Pick up marked food and drop it again: the new world item must be ordinary.
   Check partial pickup, inventory hotkeys, and barrel resupply. Food already on
   the ground before the patch remains unmarked; use a newly dispensed item.
5. Verify startup compatibility logs and unchanged behavior with the surrounding
   mod set. Test a rejected route/patch contract in a disposable configuration:
   no new barrel stock may be removed, and normal storage/manual feeding must
   continue. Compare initialization order; runtime DLL replacement is unsupported.

No actual game/host/dedicated multiplayer execution or performance profiling was
performed for this patch. Build and automated results are recorded separately
from these remaining runtime checks.

## Empty consumed Orb identity recovery (2026-10-21)

An empty Orb restored with an already-released identity can recover on its next
capture attempt. The server requires an intact, matching successful release
request/result and no conflicting active transaction. The client renews only the
unique item currently in its inventory, with the same rejected capture request,
an unchanged empty snapshot, and no staged/committing operation. It changes the
identity and clears that request's marker, then asks the user to use the Orb again.
No automatic capture, spawn, item creation, payload restoration, or journal rewrite
occurs. The old identity remains blocked. A delayed response or reconnect uses the
existing recovery RPC and binds permission to the exact request bytes.

The new server-only permission is an optional true trailer on a failed capture
reply. Normal replies and old saved prepared data retain their exact bytes. Old
clients retain the rejection; old servers do not grant the new permission. Both
server and client need this patch to recover an affected Orb. A previously released
animal need not still exist to retire an empty identity; the stronger world-evidence
requirements for filled/pending Orb outcomes are unchanged.

Validation performed for this patch:

- Debug build with `DeployToGame=true`: zero warnings/errors; final merged plugin
  copied to the local Steam game's plugins directory and SHA-256 matched.
- `OrbItemStateChecks`: 88 source-linked fixture checks passed, including slot moves,
  duplicate IDs, transfers, stale responses, modified contents and committing states.
- `OrbTransactionChecks`: 143 checks passed on each original Valheim 1.0.20 client
  and dedicated-server Mono runtime. Includes legacy packet byte preservation,
  malformed/contradictory permissions, release proof and unchanged journal/replay blocks.
  Use `run_orb_mono.py` for this runner because packet checks now use game types.
- `Check-GameApi.ps1`: passed against both original client and dedicated-server DLLs
  (1,753 direct references, 58 Harmony contracts, 47 private bindings, hotbar fields
  and 13 dynamic/reflection contracts).

Still required: a disposable host/dedicated world with two clients, testing the
reported empty stale ID before/after world saves, forced disconnect/reconnect,
server restart, dropped/duplicated responses, slot/container/drop transfers and
simultaneous use. Confirm unchanged animal/item counts, one recovery notification,
and successful explicit recapture with the new ID. This patch does not make
ServerManager character saves, world saves and the Orb journal atomic, nor recover
ambiguous filled Orbs. No real multiplayer execution was performed for this patch.

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

## Cuisine Lox cooldown / Orb patch — 2026-10-10

The inspected optional DLL is ValheimCuisine 2.3.2, SHA-256 `0C2FBCB367F0803E0CAE6368BDEA8CF1A1E06359F0DF5F6874CC55876E1F146F`. It keeps milking cooldown only in a local StatusEffect instance; creature destruction clears it. Its instance-based SE addition also does not forward the cooldown to the animal owner. This patch replaces only the inspected milking input/hover postfixes, preserving their Harmony owner/order constraints, and routes direct `MilkLox` calls to the same server protocol. Compatibility is based on the actual methods, static typed settings and installed Harmony targets; the version is logged for diagnosis and is not an activation condition. Methods must have the expected parameters, return void, be static and have no open generic parameters. All checks precede removal of Cuisine's original patches. The supplied 2.3.2 DLL is the real-version verification sample; matching metadata does not prove that every future version keeps the same behavior.

The owner persists `flg.vc.milk.v1` before the server can approve milk delivery. New Orb payload v4 contains remaining time and the existing Cuisine breeding flags. Remaining time pauses inside the Orb and is rebased on the destination world's clock on release. World time continues outside the Orb, including zone unload. Existing payload v3 remains byte-for-byte stable when loaded/reserialized. The Orb RPC namespace advances to v3 so older peers cannot silently discard v4 data; all server/client installations must be updated together. Pending/journal formats are unchanged.

Missing history means zero remaining cooldown: an untracked live Lox or an old Orb gets no artificial waiting period. The first successful milking grants milk and persists the normal cooldown. Saved cooldowns remain unchanged and corrupt nonempty records are still rejected. Normal breeding eligibility, bucket, range, ownership and server checks still apply. Breeding history missing from an old Orb cannot be reconstructed. If Cuisine was removed before migration and only its old birth flags remain (no FLG record), an Orb captured then does not preserve those flags. Once a FLG record exists, capture/release preserves it even while Cuisine is absent. This bridge does not copy arbitrary ZDO fields or every status effect.

```powershell
dotnet build FeedLikeGrandma.csproj -c Debug -p:DeployToGame=true
dotnet build tests/RegressionChecks.csproj -c Debug
./tests/bin/RegressionChecks.exe ./bin/Debug/FeedLikeGrandma.dll
dotnet build tests/CuisineNetworkChecks.csproj -c Debug
./tests/bin/cuisine-network/CuisineNetworkChecks.exe
pwsh -NoProfile -File tests/Check-CuisineApi.ps1 -CecilPath <Mono.Cecil.dll> -CuisineDll <original-ValheimCuisine.dll>
```

Validation performed: Debug build with no warnings/errors; 68 actual-DLL YAML/payload checks, 40 source-linked fake-transport Cuisine checks, 102 Orb persistence checks, 33 Orb item-state checks and 40 Feed Barrel checks passed (283 total). The original 1.0.16 client/server API checks and supplied Cuisine metadata checks passed. The final merged DLL was deployed to local Steam Valheim plugins and its SHA-256 matched. No Release ZIP was generated.

Version-independent activation follow-up: the exact 2.3.2 gate was removed from both runtime activation and the metadata checker. Debug build passed with no warnings/errors, and the actual-DLL regression runner passed 75 checks, including seven hook-binding checks for a compatible static method and rejection of changed instance/return/parameter/generic/missing methods. The supplied original 2.3.2 DLL passed the updated metadata checker. No other real Cuisine version or in-game Harmony initialization was executed; method fixtures validate the structural gate only. The deployed DLL's SHA-256 matched the final build.

Missing-history simplification follow-up: removed the one-time full cooldown, owner initialization write, special first-check hover action and unused legacy-payload argument. Debug build passed with no warnings/errors and the deployed DLL's SHA-256 matched. The actual-DLL regression runner passed 83 checks, including absent/saved/corrupt record parsing and zero cooldown for old Orb state. The source-linked network runner passed 42 checks, including immediate first grant, normal cooldown persistence, duplicate/early request rejection and corrupt records denied before owner dispatch. No game session was executed for this change.

Release 1.1.6 verification: `dotnet build FeedLikeGrandma.csproj -c Release` succeeded with zero warnings/errors and generated the standard Thunderstore and Nexus ZIPs. All 300 checks passed: 83 YAML/payload/compatibility and 102 persistence checks against the final Release DLL, plus 33 item-state, 40 barrel and 42 network source-linked checks. Original 1.0.16 client/server metadata checks passed (1,721 direct references, 58 explicit Harmony targets, 47 cached private bindings and 13 dynamic/reflection contracts). Assembly/plugin/manifest versions, package entry lists, packaged README/changelog/localization and DLL hashes were verified. Both ZIPs contain the final DLL, and the deployed local game DLL has the same SHA-256: `7D21FE0527A83FAAB2B0EB41E49A32B12D69088A68EB0274A625ECCCD8C90718`. Actual Unity/Harmony startup and multiplayer execution remain unverified.

The network tests exercise dedicated-server validation with no instantiated animal on the server, duplicate requests/apply/results, stale sequences, reconnect cookies, spoofed senders, two-player contention, delayed ZDO evidence, owner migration, capture locks, initial migration and a grant callback that throws after partial delivery. The fixture links the production protocol but simulates transport and helper state; it does not execute Cuisine's Harmony startup, Unity, real ZDO replication or inventory delivery. Original `Inventory.AddItem` analysis confirms partial stack merges before failure, so only the remaining amount is dropped.

Still required in the game, after restarting server and clients with this DLL and Cuisine 2.3.2:

1. Confirm the compatibility initialization message and absence of Harmony errors. Check the existing configured key, modifier keys, milk amount, cooldown, RequireBreeding policy and localized hover text. Recheck with Cuisine absent.
2. Milk an eligible Lox, record inventory plus dropped milk totals, capture/release repeatedly, and verify it remains unavailable for the remaining cooldown. Repeat with only a partially fillable milk stack and a full inventory; the combined increase must equal the configured amount.
3. Leave the zone, reconnect, restart the server, and migrate animal ownership. Repeat after transferring a filled Orb to another world. Test a ready Lox, a partially elapsed cooldown, a v3 Orb and an untracked live Lox. Missing history must add no cooldown; with other requirements met, the first milking must grant once and start the normal cooldown. Saved remaining time must still be preserved.
4. On a host and dedicated server with two clients, have both clients milk simultaneously, then race milking against Orb capture/release. Make the animal owner different from the milker. There must be one owner write and one milk grant, with no additional captured/released creature.
5. Delay/drop owner acknowledgement and ZDO synchronization, disconnect, die or migrate ownership during approval, and replay a result after 30 seconds. Ambiguous delivery must never grant again. An unconfirmed owner write keeps that animal locked until evidence arrives or the network session restarts; a confirmed cooldown can be consumed without milk after a delivery failure. Character/world saves remain independent, so this is not an atomic crash-recovery guarantee.
6. Verify optional CreatureManager/CLLC/OdinHorse data and v3 pending Orb recovery still survive alongside v4 Cuisine state. Older clients must time out safely, without consuming an Orb or spawning another creature.

No actual game, host or dedicated multiplayer run was performed for this patch.

## Feed Barrel floor placement — 2026-10-06

The barrel now probes the terrain/building surface within 1 m of the animal's foot height instead of projecting food candidates onto terrain beneath a structure. A single downward ray ignores triggers and rejects the barrel itself, moving bodies and steep first-hit surfaces; it does not search through them for another floor. The existing range and path checks run before any item is removed.

```powershell
dotnet build tests/FeedBarrelSurfaceChecks.csproj -c Debug
./tests/bin/feed-barrel-surface/FeedBarrelSurfaceChecks.exe
```

These source-linked checks use a managed geometric raycast fixture and stub game dependencies. They test candidate selection and food-removal guards, not Unity collider behavior, generated NavMesh or multiplayer ownership.

On 2026-10-06, the 40 surface/feeding checks and 37 existing YAML/Orb regression checks passed. The Debug mod and surface-check runner built with no warnings or errors. The final merged mod passed the original 1.0.16 client/server API checks (1,496 direct game references, 58 Harmony targets/arguments, 47 private bindings and 13 dynamic/reflection contracts), and the installed local game DLL matched the built DLL's SHA-256.

Still verify in the game with a hungry, calm animal and a closed barrel containing valid food:

1. Compare ordinary terrain, a low wooden floor, and an elevated stone floor. Food must appear above the chosen support and be consumed through the normal AI flow.
2. Add an upper floor/roof and leave terrain far below. The food must remain near the animal's floor. An unsupported edge with no surface in the probe range must not reduce the barrel's stock.
3. Compare nearby slopes/steps within 1 m of foot height with larger height differences. The latter are deliberately skipped; a valid path alone does not override the local surface requirement. Floors separated by less than 1 m still depend on the normal path check.
4. Test blocked paths, narrow spaces, the barrel's own collider, and trigger volumes. Rejected candidates must not consume food. Check that normal dropped food still works.
5. Repeat on a host and a dedicated server with two clients, including animal/barrel ownership on different peers. Count barrel stock and dropped/consumed food, and verify normal inventory access and hunger/alert restrictions.

The Debug build, managed checks and original client/server API checks do not replace these runtime scenarios.

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
