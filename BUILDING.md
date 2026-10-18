# Historical build configuration

This historical revision uses process-local configuration instead of workstation-specific paths. Build and test in a repository checkout; keep installed game, Core, Workshop, and live mod folders read-only during validation.

Set STARDEUS_GAME_ROOT to the verified local game installation, or set STARDEUS_MANAGED_PATH to its Stardeus_Data/Managed directory. Command-line StardeusManagedPath and the older GameManagedPath property are also supported. Set the values locally; never commit actual PC paths.

Framework references default to AutoUtilitiesModFramework/Libraries/AutoUtilitiesModFramework.dll in the checkout. STARDEUS_AUMF_DLL or AutoUtilitiesModFrameworkPath, AumfDll, AumfPath, or InstalledAumfPath can supply an explicit alternative. Missing explicit references fail without selecting a live or Workshop copy.

Projects retain their declared SDK/framework, compiler flags, source/reference lists, versions, and Libraries output layout. Numeric harnesses need no game path; the investor harness needs the managed Newtonsoft.Json reference. Planning test props import the shared defaults. Redirect compiled and intermediate outputs to private temporary directories when preserving curated Libraries DLLs. Do not replace tracked runtime DLLs merely because the installed Core has changed; existing compatibility failures need their own scoped repair.

Development scripts use build/LocalPaths.ps1 and retain explicit parameters. Configure the game/managed/Core inputs they require. The source-art helper also needs its original generated-art directory supplied explicitly. Historical maintenance scripts require STARDEUS_GAME_ROOT or STARDEUS_CORE_ROOT, plus STARDEUS_WORKSHOP_ROOT; the game and Workshop may be in different libraries. These scripts modify their targets when actually run, so parsing their configuration is separate from authorizing repairs.

Preserve original credits, public identities, and established compatibility identifiers. Shared source mapping keeps future repository debug paths neutral. Historical artifact sanitation preserves executable content and image pixels; existing sanitized DLLs may lack source-line debug diagnostics. Personal-path checks are bounded and do not certify new gameplay compatibility.
