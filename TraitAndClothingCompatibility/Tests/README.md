# Optional reference privacy checks

This game-independent harness links the production fingerprint and optional-comparison code. Its behavior fixtures are synthetic; it also checks that all string leaves in the shipped optional reference are SHA-256 fingerprints, both exact reviewed Expanded Traits DLL hashes are accepted, and an unknown hash is rejected. The current suite contains 187 assertions. It does not load the game or certify game compatibility.

Hosted checks use the exact public Newtonsoft.Json 13.0.2 package. Build the project, then pass the resource to the resulting executable:

```powershell
dotnet run --project TraitAndClothingCompatibility/Tests/OptionalTraitFingerprint.Tests.csproj -c Release -- TraitAndClothingCompatibility/.vscode/Resources/BaselineExpandedTraits.json
```

For a local comparison using the installed library, provide its full path as `NewtonsoftJsonPath` when building and as the first executable argument, followed by the resource path. An invalid explicit library path fails instead of selecting a fallback. This reference is non-copying and does not enter the mod payload. Use private output and intermediate directories when preserving the checkout's generated artifacts matters.
