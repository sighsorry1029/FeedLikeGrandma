# Regression checks

These checks call the built plugin's YAML parser and Orb serializer under .NET Framework 4.8. They do not initialize Unity, install Harmony patches, or run a game server. The test project is separate from the plugin solution and is never included in the mod DLL or release packages.

From PowerShell at the repository root, with the .NET SDK and .NET Framework 4.8 targeting pack installed:

```powershell
dotnet build FeedLikeGrandma.csproj -c Debug -p:DeployToGame=true
dotnet build tests/RegressionChecks.csproj -c Debug
./tests/bin/RegressionChecks.exe ./bin/Debug/FeedLikeGrandma.dll
```

Keep the built DLL beside its copied game/Unity dependencies. The runner also resolves the original assembly names of the publicized DLLs. A failed check returns a nonzero exit code.

The checks cover omitted versus empty overrides, item quality, current-level foods, invalid input, duplicate foods, culture-independent numbers, non-finite values, and a populated v3 Orb save fixture. The fixture verifies all fields and exact serialized bytes, including inventory, growth, saddle, CL&LC and OdinHorse data.

## Game regression scenarios

Use a disposable world and character. Repeat the networking scenarios on a host and on a dedicated server with two clients; for the latter, make the creature owner different from the requesting player. Record food stacks (including quality/custom data), Orb contents and creature counts before and after each transaction.

1. Feed ordinary and level-only foods, with the exact amount that crosses a level or growth boundary. Repeat after a YAML reload, including removing an override and applying an empty override.
2. For both tameable interaction and hotbar use, check empty, valid v3, malformed, truncated, empty-string and unsupported-version Orb payloads. Invalid data must retain its original text and must not capture a creature.
3. Change an Orb's payload while capture is still preparing. The request must fail without restoring an older item snapshot over the new data. Force a failure after FLG stages the item and verify rollback still restores the pre-staging item.
4. Delay a release result/acknowledgement after spawning, disable the recipe, and allow retries. The existing transaction must finish once; a new request while disabled must fail. A late failed approval after spawning must not remove the pending transaction or leave the released creature and a reusable filled Orb.
5. Retry and duplicate approvals/results, including immediate host responses. Check ownership migration, concurrent requests, inventory changes, death and disconnection. Crash/reconnect recovery is not implemented by this patch.
6. Check icons in inventory/hotbar and after dropping/picking up Orbs, then leave and re-enter the world. Check registered Orbs/barrels survive a dedicated-server restart with recipes disabled.
7. With the supported Monstrum and DeepNorth builds, test keyboard/controller mount jumps, ordinary dismount, mount death, and the saddle fallback. For supported BetterRiding builds, cross a restricted biome, change encumbrance and reload rules while holding/releasing the flight key.

Do not interpret passing the console checks as proof of these runtime scenarios. In particular, the console runner does not measure allocations, validate RPC transport or execute optional mods.
