# Building Focale.NINA.Plugin

## Targets

Both the plugin and its test project target `net10.0-windows` because the plugin uses WPF (XAML, `PasswordBox`, `ResourceDictionary` merging) and Windows-only APIs (`System.Security.Cryptography.ProtectedData` for DPAPI). NINA itself is Windows-only, so there's no runtime path for a non-Windows build.

That said, you do **not** need a Windows machine to develop. .NET 10 SDK supports `EnableWindowsTargeting=true`, which lets `dotnet build` cross-compile WPF assemblies on macOS / Linux against downloaded reference assemblies. You can write code, run the cross-platform unit tests, and commit. End-to-end testing inside NINA itself does need a Windows host.

---

## Prerequisites

| Need | Install |
|---|---|
| .NET 10 SDK | https://dotnet.microsoft.com/download/dotnet/10.0 |
| (optional) NINA install | https://nighttime-imaging.eu/download/ — only needed to populate `lib/nina/` for the actual plugin build, and for end-to-end testing |

## Quick checks (works on macOS / Linux / Windows)

```bash
# Restore packages.
dotnet restore Focale.NINA.Plugin.sln

# Build & run the cross-platform tests (everything but the DPAPI fixture).
# The DPAPI fixture is gated with [Platform("Win")] and skipped automatically off-Windows.
dotnet test tests/Focale.NINA.Plugin.Tests/Focale.NINA.Plugin.Tests.csproj
```

You will see `SecretProtectorTests` reported as `Skipped` on macOS — that's expected.

---

## Building the plugin DLL itself

The plugin references NINA host assemblies (`NINA.Plugin.dll`, `NINA.Core.dll`, etc.) as `<Reference HintPath="..." Private="false">`. They are **not** copied into the plugin output (NINA provides them at runtime). You need them on disk to compile.

### Option 1 — local NINA install (Windows or macOS via Wine/CrossOver)

Install NINA, then either copy its install folder DLLs into `lib/nina/`, or point `NinaBinPath` at the install location:

```bash
dotnet build src/Focale.NINA.Plugin/Focale.NINA.Plugin.csproj \
    -c Release \
    -p:NinaBinPath="/path/to/N.I.N.A. - Nighttime Imaging 'N' Astronomy/"
```

### Option 2 — extract NINA reference DLLs from the installer

NINA's MSI/EXE installer ships the host assemblies inside. A short script can extract them into `lib/nina/`. There is **no published "reference assemblies" NuGet** for NINA, so this is the canonical path for CI.

Configure a repo variable `NINA_REF_URL` pointing at a hosted zip of the relevant DLLs; the [release workflow](.github/workflows/release.yml) consumes it.

### Option 3 — let GitHub Actions build for you (recommended on macOS)

Push your branch. The CI workflow ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) runs on `windows-latest` with the .NET 10 SDK pre-installed and runs the test suite. The release workflow ([`.github/workflows/release.yml`](.github/workflows/release.yml)) triggers on a `v*` tag, builds the plugin against the staged NINA references, packages a zip, and stamps the manifest with the SHA-256.

This is the path with the least friction for a macOS-based developer. You write and run cross-platform tests locally; CI handles the Windows-targeted full build.

---

## Smoke-testing the plugin inside NINA (Windows host required)

1. Build the plugin: `dotnet publish src/Focale.NINA.Plugin -c Release -o out/`.
2. Copy the output into NINA's plugin folder:
   ```pwsh
   Copy-Item out\* "$env:LOCALAPPDATA\NINA\Plugins\3.3.0\Focale\" -Recurse
   ```
   (Adjust the version segment to match your NINA install; see [`NINA.Plugin/Constants.cs:26`](../nina/NINA.Plugin/Constants.cs).)
3. Launch NINA. Open **Options → Plugins → Focale**.
4. Sign in with your Focale credentials. The plugin authenticates against `api.focale.space` (or whatever you set in the Server URL field).
5. Connect a simulated camera + mount and capture a frame. Watch the upload queue counter go from 1 → 0.

For a developer loop without rebuilding NINA: run NINA as usual; rebuild only the plugin DLL; replace the file in `Plugins\3.3.0\Focale\`; relaunch NINA.

---

## Working on macOS day-to-day

**What works locally:**
- Editing C# / XAML in your IDE (Rider, VS Code, JetBrains Fleet).
- Running `dotnet restore`, `dotnet build` (with `NinaBinPath` set or `lib/nina/` populated, otherwise the plugin csproj will fail with missing references — but the test csproj only depends on the plugin's *own* code, which works).
- Running cross-platform unit tests (Redaction, UploadQueue, UploadWorker.Backoff).

**What needs Windows:**
- Compiling the plugin DLL with WPF (you can do this on macOS via `EnableWindowsTargeting=true`, but you cannot *run* it).
- The DPAPI test fixture (auto-skipped via `[Platform("Win")]`).
- Loading the plugin into NINA.

If you don't have a Windows machine handy:
- A free Windows 11 dev VM image (Microsoft publishes one at https://developer.microsoft.com/windows/downloads/virtual-machines/) runs comfortably under Parallels / UTM / VMware Fusion.
- Or push to a branch and use the CI artifact for hands-off builds. The release workflow produces a downloadable zip you can install into a Windows VM.

The trade-off is intentional: WPF is a Windows-only stack and we're not going to fight that. The plugin contracts (Settings, Api, Sync, Upload) are all kept in non-WPF files so they stay testable cross-platform.
