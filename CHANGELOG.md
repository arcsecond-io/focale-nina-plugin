# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- PluginBase derivative with MEF DI wiring.
- Settings layer with DPAPI-protected refresh token storage.
- Focale API client with JWT Bearer auth (`X-Focale-Client: nina-plugin`). The access
  token is refreshed before a request whenever it is missing or about to expire, and once
  more on a 401 for small JSON bodies.
- Automatic FITS/XISF upload: one `POST /datafiles/` with `auto_ingest=true` per saved
  frame; the back-end derives the NightLog, Dataset, site, telescope, camera, target and
  Observation/Calibration from the header. The SHA-256 is the idempotency key. TIFF and
  RAW saves are skipped.
- SQLite durable upload queue with exponential backoff.
- Upload states in the settings page (uploading, off, signed out, paused) and a
  **Resume uploads** button.
- Secret-redacting JSON converter (`Password|ApiKey|Token|Secret|Credential|Auth|Bearer`)
  plus URI userinfo stripping, for future JSON payloads (not used yet).
- WPF settings page (`Focale_Options` DataTemplate) wired through MEF `ResourceDictionary`.
- NUnit tests for `NightDate`, `FrameClassifier`, `ImageFileTypes`, `SecretProtector`
  (Windows-gated), `Redaction`, `UploadQueue`, `UploadWorker` (backoff and failure
  classification) and API error-body parsing.
- GitHub Actions CI (build + test) and release workflow (zip + manifest stamping).

### Changed
- A frame is no longer dead-lettered for being signed out (401), refused by a plan
  limit or quota (402/403/507) or offline: it stays queued. 401 waits for a sign-in,
  refusals pause the whole queue for an hour, network failures retry every minute
  without spending attempts. Server-side transient errors now get ~a day of retries
  (300 attempts) instead of ~30 minutes.
- Dependencies aligned with NINA 3.3: `Newtonsoft.Json` 13.0.4, `System.ComponentModel.Composition`
  10.0.10, `CommunityToolkit.Mvvm` 8.4.2. `Microsoft.Data.Sqlite` 10.0.14 (pulls SQLitePCLRaw
  2.1.13, out of GHSA-2m69-gcr7-jv3q). The explicit `System.Security.Cryptography.ProtectedData`
  reference is gone: the Windows Desktop framework provides it.

### Removed
- Sequence push to `observingsequences` (and the NightLog pre-creation it relied on):
  the back-end now refuses it — sequences are created `pending` and need a `profile` —
  and that model is Focale's own executable Night Scheduler, not a record of NINA's.
  To be redesigned.
- The "metadata sync" toggle and the unused `MaxParallelUpload` setting; the never
  implemented profile / device / weather snapshots are no longer advertised.

### Fixed
- Build: trailing comma in the upload call (`UploadWorker`); CA1416 on the DPAPI calls
  (`GenerateAssemblyInfo` is off, so `SupportedOSPlatform("windows")` is now declared by hand);
  a `Logger.Warning` overload NINA 3.3 does not have; NuGet audit and pruning errors.
- `UploadQueue.ClaimNextAsync` returned the claimed job still marked `Pending`.
- The first upload after a NINA restart always went out without a token, got a 401, and
  the auth handler tried to replay the already-consumed file stream.
- A 401 on the token refresh call re-entered the refresh gate and deadlocked.
