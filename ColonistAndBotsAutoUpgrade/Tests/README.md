# CARAU focused checks

Run `dotnet run --project EnemyConversionSettings.Tests.csproj` from this
folder. The harness checks the Enemy Conversion opt-in defaults, schema-5
migration, schema-6 round trip, route-state transitions, final-route master
shutdown, Obey-only restoration after an explicit re-enable, invalid no-route
state normalization and save projection, stable appended settings and tracking
MessagePack keys, and invalid-priority fallback without requiring Stardeus to
run.
