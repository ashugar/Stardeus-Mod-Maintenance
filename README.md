# Stardeus Mod Maintenance

Source, configuration, artwork, documentation, tests, and current mod assemblies for Stardeus mods created or maintained by ashugar. Each mod has its own folder at the repository root, using its existing installation folder name.

This repository replaces the retired Workshop maintenance scripts. The old scripts and root changelog remain available in Git history.

## Mods

The versions below describe the initial import on October 18, 2026. Each mod's `ModInfo.json` is the authority for its current version; see its own README and documentation for features, requirements, and validation status.

| Mod | Imported version | Steam Workshop |
| --- | --- | --- |
| [Aggressive Auto Connect Upgrade](AggressiveAutoConnectUpgrade/) | 1.2.2 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3770671842) |
| [Auto Factory](AutoFactory/) | 1.5.2 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3784267361) |
| [Auto Hauling](AutoHauling/) | 1.1.5 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3783767356) |
| [Auto Utilities Mod Framework](AutoUtilitiesModFramework/) | 1.4.0 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3776751765) |
| [Better Storage (v0.15 Update)](BetterStorageMod%28v0.15Update%29/) | 2.0.4 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3748324441) |
| [Bot Auto Hack](BotAutoHack/) | 1.0.7 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3788363114) |
| [Chromanite Material (v0.15 Update)](Chromanite%28v0.15Update%29/) | 2.0.1 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3748921886) |
| [Chromanite Doors (v0.15 Update)](ChromaniteDoors%28v0.15Update%29/) | 2.0.1 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3748937744) |
| [Chromanite Power (v0.15 Update)](ChromaniteGlitterPower%28v0.15Update%29/) | 2.0.1 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3748932401) |
| [Chromanite Walls (v0.15 Update)](ChromaniteWalls%28v0.15Update%29/) | 2.0.1 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3748937799) |
| [Colonist And Bots Auto Upgrade](ColonistAndBotsAutoUpgrade/) | 1.1.3 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3754689644) |
| [Expanded Traits](ExpandedTraitsMod/) | 0.1.4 | - |
| [Improved Ship Pathing](ImprovedShipPathing/) | 0.1.1 | - |
| [Intelligent Recycling](IntelligentRecycling/) | 1.3.2 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3775577154) |
| [Local Log Mod](LocalLogMod/) | 0.1.8 | - |
| [Market Balancing](MarketBalancing/) | 0.1.0 | - |
| [Metal Husks Revisited (v0.15 Update)](MetalHuskRevisited%28v0.15Update%29/) | 2.0.1 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3749399478) |
| [Missile Overhaul](MissileOverhaul/) | 0.4.0 | - |
| [MolecularAssembler Fixed (v0.15 Update)](MolecularAssemblerFixed%28v0.15Update%29/) | 2.0.1 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3748810380) |
| [Optimization Mod](OptimizationMod/) | 0.1.34 | - |
| [Orbotron Upgrades (v0.15 Update)](OrbotronUpgrade%28v0.15Update%29/) | 2.1.0 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3748954299) |
| [Part Restoration Unit](PartRestorationUnit/) | 1.1.2 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3792407697) |
| [Planning Overhaul](PlanningOverhaul/) | 1.1.0 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3805289692) |
| [Prosthetics & Androids Expanded (v0.15 Update)](Prosthetics%26AndroidsExpanded%28v0.15Update%29/) | 2.3.3 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3746773189) |
| [Quantum Storage](QuantumStorage/) | 1.3.5 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3794784281) |
| [Quest Request Preview](QuestRequestPreview/) | 1.0.2 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3783715878) |
| [Reconnect Teleporter Connections](ReconnectTeleporterConnections/) | 1.1.2 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3785027459) |
| [Remember Facility Upgrades](RememberFacilityUpgrades/) | 1.1.0 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3791306246) |
| [Skill Cap Mod](SkillCapMod/) | 0.1.2 | - |
| [Stock Market Auto-Investor](StockMarketAutoInvestor/) | 0.1.0 | - |
| [Super Cleaning](SuperCleaning/) | 1.0.3 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3787822873) |
| [Tractor Beam Auto Connect](TractorBeamAutoConnect/) | 1.2.2 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3776012239) |
| [Tractor Beam Overhaul](TractorBeamOverhaul/) | 1.1.1 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3791894872) |
| [Trait and Clothing Compatibility Mod (TACC)](TraitAndClothingCompatibility/) | 1.1.3 | [Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3796663540) |

## Repository contents

- Mod manifests, definitions, translations, graphics, Workshop IDs, and existing author credits are preserved.
- Source, project files, test source, and mod-specific build tools are included. Some projects keep their source or project file inside `.vscode`; those files are intentional.
- Each compiled mod's current DLL is included in its `Libraries` folder. Stardeus, Unity, Harmony, and other game-provided dependency assemblies are not bundled.
- Build caches, editor databases, release backups, logs, private diagnostic evidence, and temporary Core decompilation output are excluded. Per-mod changelogs and useful research/testing documentation are retained.

## Development and installation

Keep this checkout separate from the live Stardeus `Mods` directory. The repository import leaves existing installations in place. Deploy selected mod folders deliberately; do not install the repository root as a single mod.

Use the relevant mod's project and [shared build instructions](BUILDING.md). Configure game/dependency paths through local environment variables or explicit build properties; source filenames in new debug symbols are mapped to a neutral repository root. Auto Utilities Mod Framework remains a separate mod and dependency where required.

The initial import preserved the selected files byte-for-byte. Repository privacy maintenance removes workstation paths from build inputs and diagnostic/image metadata while preserving runtime code and image pixels. These checks do not establish compatibility with a new game version. Consult each mod's validation notes and test against your installed Stardeus build before distributing changes.

## License and attribution

The existing root [LICENSE](LICENSE) is retained. Original author credits, Workshop identities, and any mod-specific notices remain with each mod. Upstream content remains subject to its existing license terms.
