# Launcher updates

The WPF app uses Velopack for install hooks, update discovery, package download, and restart. Update discovery is enabled only for Velopack-installed builds. The launcher is configured to use the public `SBUnivers/SubReel.Launcher` GitHub Releases feed.

## Configure the release feed

Set either `Updates:FeedUrl` in `src/SubReel.Launcher.App/appsettings.json` to the base URL hosting the Velopack release feed, or set `Updates:GitHubRepository` to `https://github.com/owner/repository`. GitHub releases must contain the Velopack assets produced by the packaging script. `Updates:IncludePrereleases` opts into beta/pre-release GitHub releases. Keep the selected source consistent for initial installs and subsequent releases.

## Package a Windows release

Install the Velopack CLI matching the `Velopack` NuGet version (`1.2.161`):

```powershell
dotnet tool install -g vpk --version 1.2.161
```

Then package a release from the repository root:

```powershell
./eng/pack-release.ps1 -Version 0.1.0
```

The script publishes a self-contained Windows build and creates the installer and update packages under `artifacts/velopack/releases`. Upload those files to the configured feed host. The first release must be installed through the generated Velopack installer; running from `bin/Debug` or `bin/Release` cannot apply updates.

Pushing a tag such as `v0.1.0` runs `.github/workflows/release.yml`, packages Windows x64 and creates a **draft** GitHub Release with generated notes and Velopack assets. Review the draft and publish it from GitHub when the package is ready. The launcher checks published releases on the Settings page. When an update is available, the user can download it and restart into the new version.
