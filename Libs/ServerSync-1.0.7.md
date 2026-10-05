# Embedded ServerSync baseline

`ServerSync.dll` is vendored from the reviewed common baseline `valheim-1.0.7-r1` under `C:/Users/blizz/.codex/references/valheim/integrations/serversync/versions/valheim-1.0.7-r1`.

- Upstream commit: `c57c2aa54e07cdcc7630d6068699ea781622323e`.
- Assembly identity: `ServerSync, Version=1.0.0.0`.
- File version: `1.0.0.1`.
- Vendored DLL SHA-256: `B4DD786997F4E90D770F09EF3E9D64154754FE7E8EDFB4841795751895B35846`.
- The baseline was compiled from source against unmodified Valheim 1.0.7 assemblies. It replaces the obsolete `ZRoutedRpc.Everybody` field loads, uses the equivalent public administrator check, and preserves the new player/history/admin list and network-time message order during buffered login.

Normal builds merge this pinned DLL into FeedLikeGrandma. They do not patch ServerSync or game DLLs at build time, require the global reference directory at build time, or install ServerSync as a separate BepInEx plugin. Configuration keys, CustomSyncedValue identifiers, locking policy, version checks, and wire formats remain owned by the embedded library.

The final plugin is checked against original client and dedicated-server assemblies. Configuration transport, login buffering, administrator permissions, disconnect cleanup, Steam/PlayFab behavior, and multiple embedded ServerSync copies still require real game/Mono multiplayer testing.
