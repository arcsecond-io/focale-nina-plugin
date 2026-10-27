# Focale.NINA.Plugin

A NINA plugin that bridges N.I.N.A. to [Focale](https://focale.space) (the same backend as `api.arcsecond.io`):

- **Automatic frame upload** — every FITS or XISF frame NINA saves is uploaded to Focale in the background. Focale reads the file header and files it under the right night, site, telescope, camera and target, as an observation or a calibration frame, with image metrics computed server-side. NINA's TIFF and RAW saves are skipped.
- **Durable queue** — uploads go through an on-disk SQLite queue that survives network drops and NINA restarts. Frames wait while you are offline, signed out, or over a plan limit, and go up once that clears. A frame is only given up (dead-lettered) when Focale refuses that specific file, or when the file is deleted or modified before it is sent.

The plugin does not (yet) send NINA sequences, profiles or logs, and does not receive anything from Focale. See the [Focale feature table](https://focale.space) for what is planned.

## Status

Pre-release, not yet submitted to the NINA plugin registry. Requires NINA 3.3 or later (the plugin is built for .NET 10, like NINA 3.3); NINA 3.2 and earlier cannot load it. See [BUILD.md](BUILD.md) for build/test instructions and [ENDPOINT_MAPPING.md](ENDPOINT_MAPPING.md) for how the plugin maps NINA concepts onto existing arcsecond-back resources.

## Install

> Once released, the plugin will be installable from the NINA plugin manager (Options → Plugins → Available). For now, manual install:

1. Download the latest release zip from the GitHub Releases page.
2. Extract into `%LOCALAPPDATA%\NINA\Plugins\<NINA-major.minor.patch>\Focale\`.
3. Restart NINA.
4. Open Options → Plugins → Focale and sign in with your Focale credentials.

## Authentication & privacy

- The plugin signs in with your Focale username + password and receives a JWT access/refresh pair. The access token lives in memory only and is auto-refreshed; the refresh token is the long-lived secret and is stored **encrypted at rest** using Windows DPAPI (`DataProtectionScope.CurrentUser`).
- The plugin sends `X-Focale-Client: nina-plugin` on every request. Server-side, that gives the plugin its own session bucket — revoking the plugin from your account settings does **not** affect your web/desktop sessions, and vice-versa.
- All HTTPS calls use the .NET default TLS stack with certificate validation enabled. The plugin never disables TLS verification.
- Only the saved image files are sent — no NINA profile, settings or logs. Nothing leaves your machine until you sign in and leave automatic upload enabled.
- Transient HTTP errors and network drops are retried silently; the plugin does **not** spam UI toasts. A plan or quota refusal (HTTP 402/403/507) pauses the queue for an hour, with the server's reason and a **Resume uploads** button in the plugin's settings page.

## License

MPL 2.0 — same as N.I.N.A. itself. See [LICENSE](LICENSE).
