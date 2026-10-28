# Prism Launcher and SubReel comparison

This comparison uses Prism Launcher's public feature overview and wiki as a functional reference. SubReel remains its own launcher: this is a feature gap review, not a plan to copy Prism's branding, assets, or source code.

## Product-level comparison

| Area | Prism Launcher reference | SubReel status | Direction for SubReel |
|---|---|---|---|
| Isolated instances | Each instance has separate mods, resource packs, settings, and game data. | Isolated instance directories, per-instance loader, memory, Java and JVM arguments are implemented. | Preserve the isolation model and keep file operations scoped to the selected instance. |
| Create/copy instances | Copy flow offers choices for saves, game options, resource packs, shaders, screenshots, servers, and mods. | Selective copying is implemented with a new ID and directory. | Preserve category-scoped copies and add more categories only when needed. |
| Modpacks | Import and browse packs from multiple providers. | Modrinth and CurseForge pack search/install and manifest import/export are implemented. | Add more providers only when a concrete demand and API path are available. |
| Individual mods | Browse, install, and update mods for a selected instance and compatible loader/version. | A per-instance Modrinth manager supports compatible search, installation with required dependencies and SHA-1 checks, enable/disable, removal, update detection and replacement. CurseForge individual mods and in-place modpack updates are not implemented. | Live UI/API validation and CurseForge support remain. |
| Worlds and resource content | Manage worlds, resource packs, and shader packs from the launcher. | Users can access the instance folder; there are no dedicated content-management pages yet. | Add scoped managers after the mod manager, reusing safe file-operation contracts. |
| Logs and crash support | View logs, terminate a frozen game, and upload logs to a paste service. | Process exit/crash state, crash-report path, launcher logs, and a redacted support archive exist; no per-instance console or upload workflow. | Add a per-instance log viewer and an explicit user-controlled export/upload flow with secret redaction. |
| Settings and appearance | Broad launcher/instance settings and theming. | RU/EN, theme/accent, reduced motion, Java, RAM, and basic update configuration exist. | Continue on scoped missing options; keep user-facing flows localized and validated. |

## Changes made in this comparison pass

- Instance duplication now accepts a typed allowlist of optional data: `saves/`, `options.txt`, `resourcepacks/`, `shaderpacks/`, `screenshots/`, `servers.dat`, and `mods/`.
- The UI asks which categories to copy. Every copy gets a new instance ID and game directory; unselected categories remain empty/not copied.
- Copying is cancellable between files, asynchronous, preserves timestamps, rejects reparse points, and removes the partially created duplicate if copying fails.
- Automated tests cover selection behavior and the isolation of the resulting instance directory.
- The selected-instance UI has a Modrinth mod manager. Search is scoped to the instance's Minecraft version and loader; installation verifies the HTTPS CDN URL, safe JAR name and SHA-1 and includes required dependencies. Existing JARs can be toggled or removed after confirmation; file operations stay under the selected instance's `mods/` folder.
- Automated tests cover verified install, cleanup after a SHA-1 mismatch, vanilla-loader rejection, and enable/disable/remove.
- Update detection asks Modrinth for the latest release compatible with the instance, based on each local JAR's SHA-1; updates stage and verify the replacement before swapping it into place and restore the old file if committing the update fails.
- Automated tests cover finding an update and replacing the selected mod while removing its old JAR.

## References

- [Prism Launcher features](https://prismlauncher.org/about/)
- [Creating and editing instances](https://www.prismlauncher.org/wiki/getting-started/create-instance/)
- [Instance copy options](https://www.prismlauncher.org/wiki/help-pages/instance-copy/)
- [Downloading individual mods](https://www.prismlauncher.org/wiki/getting-started/download-mods/)
- [Launcher settings](https://prismlauncher.org/wiki/help-pages/launcher-settings/)
