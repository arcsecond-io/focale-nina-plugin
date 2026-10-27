# Building Focale.NINA.Plugin

## Targets

The plugin and its tests build for two targets:

| Target | NINA references | Purpose |
|---|---|---|
| `net8.0-windows` | NINA 3.2 (`NINA.Plugin` 3.2.0.9001) | **The build we ship.** NINA 3.3 runs on .NET 10, which loads .NET 8 assemblies, so this one build serves NINA 3.2 and 3.3. |
| `net10.0-windows` | NINA 3.3 (`NINA.Plugin` 3.3.0.x-nightly) | Never shipped. It makes the build fail as soon as the code uses an API NINA 3.3 changed or removed. |

Compiling against 3.3 catches source breaks, not binary ones (a NINA method that gained an optional parameter still compiles, but the 3.2 build's call no longer binds in 3.3). So CI also runs [`tools/NinaCompatCheck`](tools/NinaCompatCheck/Program.cs) on the shipped DLL: it resolves every NINA type and member the build references, signatures included, against NINA 3.3's assemblies, and fails on the first missing one.

```bash
dotnet run --project tools/NinaCompatCheck -c Release -- \
  src/Focale.NINA.Plugin/bin/Release/net8.0-windows/Focale.NINA.Plugin.dll \
  tests/Focale.NINA.Plugin.Tests/bin/Release/net10.0-windows
```

(On macOS, add the Windows Desktop reference assemblies as a third argument, e.g. `~/.nuget/packages/microsoft.windowsdesktop.app.ref/10.0.x/ref/net10.0`.) When a NINA API exists in 3.3 only — `IImageSaveMediator.ImageSaveFailed`, for one — the plugin cannot use it.

NINA's assemblies come from its NuGet packages. The plugin compiles against them with `ExcludeAssets="runtime"`: NINA provides them at runtime, and so does it for `Newtonsoft.Json`, MEF (`System.ComponentModel.Composition`) and `CommunityToolkit.Mvvm`, which are pinned to the exact versions each NINA release depends on. The plugin's [`Export`] attributes must be the very MEF types NINA's container looks for, so the plugin never ships its own copy. Only `Microsoft.Data.Sqlite` (the upload queue) ships with the plugin.

WPF and DPAPI make both targets Windows-only.

## Prerequisites

| Need | Install |
|---|---|
| .NET 10 SDK | https://dotnet.microsoft.com/download/dotnet/10.0 — builds both targets |
| .NET 8 runtime (Windows) | to run the `net8.0-windows` tests |
| NINA 3.2 or 3.3 (Windows) | only for end-to-end testing |

## Build

```bash
dotnet build src/Focale.NINA.Plugin/Focale.NINA.Plugin.csproj -c Release
```

This works on macOS and Linux too (`EnableWindowsTargeting=true`): it compiles both targets, XAML included.

## Test

```bash
dotnet test tests/Focale.NINA.Plugin.Tests/Focale.NINA.Plugin.Tests.csproj -c Release
```

The tests need the Windows Desktop runtime, so they run on Windows only (CI runs them on `windows-latest`, for both targets). The DPAPI fixture is gated with `[Platform("Win")]`.

On macOS the test runner fails with "Framework: 'Microsoft.WindowsDesktop.App' … No frameworks were found": that is expected. Most tests are pure logic; to run those locally, point a scratch `net10.0` NUnit project at the test sources (`<Compile Include="…/tests/Focale.NINA.Plugin.Tests/*.cs" />`) and at the built `Focale.NINA.Plugin.dll`, with `AssemblyName` set to `Focale.NINA.Plugin.Tests` so `InternalsVisibleTo` applies.

## Release

Pushing a `vMAJOR.MINOR.PATCH.BUILD` tag runs [`release.yml`](.github/workflows/release.yml): it publishes the `net8.0-windows` build, zips an allowlist (the plugin, `Microsoft.Data.Sqlite`, `SQLitePCLRaw` and its Windows native libraries), stamps `manifest.json` with the version and the zip's SHA-256 ([`scripts/build-manifest.ps1`](scripts/build-manifest.ps1)), and attaches both to a GitHub Release. Submitting the manifest to the NINA plugin registry is manual.

## Smoke-testing inside NINA (Windows)

1. `dotnet publish src/Focale.NINA.Plugin -c Release -f net8.0-windows -o out/`
2. Copy `Focale.NINA.Plugin.dll`, `Microsoft.Data.Sqlite.dll`, `SQLitePCLRaw.*.dll` and `runtimes\win-x64\` from `out\` into `%LOCALAPPDATA%\NINA\Plugins\<NINA version>\Focale\` (e.g. `3.2.0`).
3. Launch NINA, open **Options → Plugins → Focale**, choose a storage mode and sign in.
4. Connect a simulated camera and mount, capture a frame, and watch the upload queue go from 1 to 0.

Do this on NINA 3.2 and on a NINA 3.3 nightly: the shipped build must load in both.
