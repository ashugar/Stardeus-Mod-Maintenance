# Batch recovery regression checks

`BatchRecoveryTests.csproj` source-links the production snapshot, coordinator, shared invocation guard, result types and bounded text formatter. It needs only .NET 10 and no game assemblies. Run it with `dotnet run --project "OrbotronUpgrade(v0.15Update)/Tests/BatchRecoveryTests.csproj"`; the repository's isolated-logic CI also runs it.

Fixtures supply eligibility and per-target outcomes through the same narrow callbacks used by the native adapter. They verify selection copying/filtering/deduplication, conflicting identity rejection, fixed consent, cancellation/reentrancy, preflight and per-target revalidation, all service outcomes, partial/unknown failure accounting and bounded summaries. They do **not** establish native Orbotron ownership eligibility, Unity popup/pooling behavior, actual Harmony integration or real save/load.

Current installed-Core compiled preservation checks remain separate: they exercise the actual DLL and native synthetic job insertion/persistence/action index, with optional compiled Skill Cap setter semantics and without that dependency. Private scratch hashing/direct-setter adapters are not gameplay. Fresh mixed Worker Robots selection, preview/cancel/restore/repeat, old skill/priority/order preservation, responsiveness and separate-save reload remain runtime acceptance checks.
