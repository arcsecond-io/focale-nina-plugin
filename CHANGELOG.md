# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Initial scaffold: PluginBase derivative, MEF DI wiring.
- Settings layer with DPAPI-protected refresh token storage.
- Focale API client with JWT Bearer auth (`X-Focale-Client: nina-plugin`), automatic refresh-on-401.
- **Per-night resource model** (local-noon-to-local-noon convention, see `NightDate`):
  one `NightLog` + two `Dataset`s (`NINA - YYYY-MM-DD - Observations` / ` - Calibrations`)
  created lazily on first need, with automatic roll-over.
- Automatic FITS upload pipeline backed by a SQLite durable queue with exponential backoff.
  Uploads to `POST /datasets/<uuid>/datafiles/`. Frames are routed to the Observations or
  Calibrations Dataset based on `ImageMetaData.Image.ImageType` (LIGHT/SNAPSHOT vs DARK/BIAS/FLAT).
- **Sequence push via `observingsequences` app** (no JSON metadata): on
  `SequenceStarting`, the AdvancedSequencer tree is published as `ObservingSequence` +
  `SequenceStep` per target + `SequenceTask` per instruction, then transitioned to
  `completed` on `SequenceFinished`. Target name / RA / Dec / position angle do **not**
  live on the SequenceStep — they live on the per-frame `Observation` rows below.
- **Typed per-frame records** via `TargetResolver` + `FramePublisher`: after each FITS
  upload, the plugin creates a matching `Observation` (lights) or `Calibration` (darks/
  biases/flats) row under the night's NightLog. Observations carry an FK to `XTarget`
  (resolved find-or-create via `/targets/`) and `Observation.position_angle`.
- The plugin does **not** send a duplicate metadata JSON in multipart uploads — arcsecond-back's
  existing FITS extractor picks up the 70+ keywords NINA already writes.
- Secret-redacting JSON converter (`Password|ApiKey|Token|Secret|Credential|Auth|Bearer`)
  plus URI userinfo stripping.
- WPF settings page (`Focale_Options` DataTemplate) wired through MEF `ResourceDictionary`.
- NUnit tests for `NightDate`, `FrameClassifier`, `SecretProtector` (Windows-gated),
  `Redaction`, `UploadQueue`, `UploadWorker.Backoff`.
- GitHub Actions CI (build + test) and release workflow (zip + manifest stamping).
