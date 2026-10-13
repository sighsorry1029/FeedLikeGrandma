# STUWard Orb compatibility checks (2026-10-13)

The three item-use Prefixes respect an earlier Harmony `__runOriginal = false`
without consuming food, starting an Orb action or overwriting the prior result.
The optional STUWard API checks the blocked-prefab policy at player and target
positions. If STUWard is absent, normal behavior remains; if installed but its
API is unavailable, new Orb actions fail closed with a diagnostic warning.
Install both patched DLLs on the server and every client.

Capture is checked locally, on the authenticated server request, and before the
first owner commit dispatch. A denial after staging uses the existing durable
failure result and rollback. Once dispatched, retries/results are still allowed.
Release is checked locally, on initial server approval, and locally immediately
before spawning at the fixed requested position. A pre-spawn denial completes
the reservation with a failure result and preserves the filled Orb. It is not a
final server-authoritative spawn handshake. No wire or saved-state format changed.

The initial release RPC remains immediate. Delaying it until the visual effect
ended would create a larger disconnect window without a server recovery receipt.
Completed actions and their recovery/replay paths do not recheck ward permission.

## Checks performed

- Debug build/deploy and Release 1.1.7 build: zero warnings/errors. Deployed DLL
  hashes matched; Release package DLLs and staged documents matched their sources.
- 7 checks against the compiled Prefixes: prior denial exits before accessing
  even null game objects, prior results remain unchanged, no hard STUWard reference.
- 106 real-DLL persistence checks, including durable failure after a staged capture.
- 37 source-linked item-state checks, including a denied release after a slot move
  and protection against a late success consuming that completed failure.
- Installed original Valheim 1.0.17 client (Steam 25730771) and preserved 1.0.16
  server (25527701) metadata: 1,731 direct references,
  58 explicit Harmony targets/arguments, 47 private bindings, hotbar fields and
  13 dynamic/reflection contracts passed for each role.

```powershell
dotnet build FeedLikeGrandma.sln -c Debug -p:DeployToGame=true
dotnet build tests/WardCompatibilityChecks.csproj -c Debug
& '<Unity Mono>/mono.exe' tests/bin/ward-compatibility/net48/WardCompatibilityChecks.exe `
  bin/Debug/FeedLikeGrandma.dll '<BepInEx core directory>'
dotnet build tests/OrbTransactionChecks.csproj -c Debug
& tests/bin/orb-transactions/OrbTransactionChecks.exe bin/Debug/FeedLikeGrandma.dll
dotnet build tests/OrbItemStateChecks.csproj -c Debug
& tests/bin/orb-item-state/OrbItemStateChecks.exe
```

The Prefix, persistence and metadata checks also passed against the final merged
Release 1.1.7 DLL. Manifest, assembly, plugin and file versions were verified;
both Release ZIPs contain the matching DLL and expected package files.

The final Prefix runner invokes methods directly and exits normally. An earlier
experimental surrogate detour run reached its assertions but crashed during Mono
shutdown; it is not counted as a successful runtime test. Windows CLR cannot load
the game's default-interface methods. None of these checks runs Unity or RPCs.

In game, test both capture and release across ward boundaries, trusted/outsider
users, list changes and access revocation during the delay. Test duplicate/late
results, reconnects, inventory-slot moves, server restarts and owner migration;
count live creatures and filled Orbs together. Confirm STUWard absence leaves
ordinary Orb use available. These actual game/multiplayer checks remain unrun.
