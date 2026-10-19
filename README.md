# Stardeus Mod Maintenance

Source, configuration, artwork, documentation, tests, and current mod assemblies for Stardeus mods created or maintained by ashugar. Only Workshop mods with a tracked `SteamWorkshopItemId` file in their root folder belong here. Each mod has its own folder at the repository root, using its existing installation folder name. Keep local-only mods and their development source outside this public checkout.

This repository replaces the retired Workshop maintenance scripts. The old scripts and root changelog remain available in Git history.

## Mods

See the [generated mod catalog](MODS.md) for current manifest versions, dependencies, package type, and Workshop links. Each mod's own documentation describes its features and validation status.

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


## Repository workflow

See [AUTOMATION.md](AUTOMATION.md) for local privacy prevention, required PR checks, reviewed auto-merge, and merged-branch cleanup. Builds and live deployment remain governed by [BUILDING.md](BUILDING.md) and [AGENTS.md](AGENTS.md).
