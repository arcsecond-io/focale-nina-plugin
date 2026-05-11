# Focale.NINA.Plugin

A NINA plugin that bridges N.I.N.A. to [Focale](https://focale.space) (the same backend as `api.arcsecond.io`):

- **Metadata sync** — pushes event-driven JSON snapshots of the active equipment profile, the running sequencer tree, target list, device runtime state, and observatory/site info onto the user's `NightLog`, so each Focale session is correlated with what NINA actually had configured at the time.
- **Automatic FITS upload** — uploads every successfully saved FITS frame (with its full `ImageMetaData`) into the user's `Dataset`. Survives network drops and app restarts via a durable on-disk queue.

## Status

Pre-release scaffold. Not yet submitted to the NINA plugin registry. See [BUILD.md](BUILD.md) for build/test instructions and [ENDPOINT_MAPPING.md](ENDPOINT_MAPPING.md) for how the plugin maps NINA concepts onto existing arcsecond-back resources.

## Install

> Once released, the plugin will be installable from the NINA plugin manager (Options → Plugins → Available). For now, manual install:

1. Download the latest release zip from the GitHub Releases page.
2. Extract into `%LOCALAPPDATA%\NINA\Plugins\<NINA-major.minor.patch>\Focale\`.
3. Restart NINA.
4. Open Options → Plugins → Focale and sign in with your Focale credentials.

## Authentication & privacy

- The plugin signs in with your Focale username + password and receives a JWT access/refresh pair. The access token lives in memory only and is auto-refreshed; the refresh token is the long-lived secret and is stored **encrypted at rest** using Windows DPAPI (`DataProtectionScope.CurrentUser`).
- The plugin sends `X-Arcsecond-Client: nina-plugin` on every request. Server-side, that gives the plugin its own session bucket — revoking the plugin from your account settings does **not** affect your web/desktop sessions, and vice-versa.
- All HTTPS calls use the .NET default TLS stack with certificate validation enabled. The plugin never disables TLS verification.
- Before any profile/snapshot is sent, **secrets are redacted** by name (`Password`, `ApiKey`, `Token`, `Secret`, `Credential`, `Auth`, `Bearer`) plus an explicit list of known fields. Any new field NINA adds that matches one of these patterns will fail the unit test suite and force a deliberate review.
- The plugin does not collect or send anything you have not enabled (FITS upload and metadata sync each have their own toggle).
- Transient HTTP errors are retried silently; the plugin does **not** spam UI toasts on network drops.

## License

MPL 2.0 — same as N.I.N.A. itself. See [LICENSE](LICENSE).
