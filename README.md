# Focale.NINA.Plugin

A NINA plugin that bridges N.I.N.A. to [Focale](https://focale.space) (the same backend as `api.arcsecond.io`). It implements the **NINA import** line of Focale's pricing table (Observer plan and above):

- **Frames** — every FITS or XISF frame NINA saves goes to Focale, which reads the header and files it under the right night, site, telescope, camera and target, as an observation or a calibration frame, with image metrics computed server-side. NINA's TIFF and RAW saves are skipped.
- **Where the pixels go** — chosen in the settings page, once; nothing is uploaded until then:
  - *Index only*: the file stays on your computer; Focale keeps the metadata and measurements (no storage used).
  - *Focale storage*: a copy is kept in Focale (counts against your plan's storage).
  - *My own storage*: a copy goes to one of your Focale-connected storages (S3, SFTP, SMB, FTP, or Dropbox connected with "Connect Dropbox"), as `Focale/<the frame's path below NINA's image folder>` under the storage's root — NINA's own date/target/… folders are kept, and Focale's scans of that storage skip the `Focale/` folder.
- **Night events** — the sequence, autofocus runs (position, temperature, filter; failed runs too), meridian flips, guiding RMS over each exposure, dithers, park/unpark and safety changes are recorded as they happen and filed under the night log Focale put that night's frames in. They make the session timeline of Focale's Logbook. Can be turned off.
- **Durable queues** — frames and events are kept on disk (SQLite) and survive network drops and NINA restarts. While you are offline, signed out, over a plan limit, or your chosen storage is refused, they wait and go up once that clears. A frame is only given up when Focale refuses that specific file, or when it was deleted or modified before it was sent.

The plugin does not send NINA profiles, settings or log files, and does not receive anything from Focale yet (pushing targets and exposure plans to NINA, and smart scheduling, are the next phases).

## Status

Pre-release, not yet submitted to the NINA plugin registry. Works with NINA 3.2 and 3.3: the shipped build targets .NET 8 against NINA 3.2, which NINA 3.3 (.NET 10) loads as well; CI also compiles the code against NINA 3.3 so an API change there breaks the build. See [BUILD.md](BUILD.md) for build/test instructions and [ENDPOINT_MAPPING.md](ENDPOINT_MAPPING.md) for how the plugin maps NINA concepts onto existing arcsecond-back resources.

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
- Only the saved image files and, if enabled, the night's events (times, autofocus positions, guiding RMS, …) are sent — no NINA profile, settings or log files. Nothing leaves your machine until you sign in and choose where frames go.
- Transient HTTP errors and network drops are retried silently; the plugin does **not** spam UI toasts. A plan or quota refusal (HTTP 402/403/507, e.g. NINA import on the Free plan, or the plan's indexed-files or storage limit) or a refused destination storage pauses the queue for an hour, with the reason and a **Resume uploads** button in the plugin's settings page.

## License

MPL 2.0 — same as N.I.N.A. itself. See [LICENSE](LICENSE).
