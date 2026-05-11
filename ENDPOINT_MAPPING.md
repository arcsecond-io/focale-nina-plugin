# Endpoint mapping — NINA plugin → arcsecond-back

How the plugin maps NINA concepts onto **existing** arcsecond-back resources, using **typed FKs** (not free-form JSON) wherever the schema already supports it. URL prefix: arcsecond-back mounts every app at the URL root, so all paths below are absolute under the configured Server URL (default `https://api.focale.space`).

## Night convention

A "night" runs from **local noon (date D) to local noon (date D+1)** and is dated D. The plugin uses this date for `NightLog.date`, for the `ObservingSequence.night` field, and as a segment in every Dataset name. See [`Common/NightDate.cs`](src/Focale.NINA.Plugin/Common/NightDate.cs).

## Per-night resources

For each night the plugin observes, three resources are created lazily, once, with automatic roll-over at the noon boundary:

| Resource | Endpoint | Name / key |
|---|---|---|
| Session container | `POST /observingruns/nightlogs/` | `name = "NINA — YYYY-MM-DD"`, `date = YYYY-MM-DD`, `source = "observer_console"` |
| Observation Dataset | `POST /datasets/` | `name = "NINA - YYYY-MM-DD - Observations"` |
| Calibration Dataset | `POST /datasets/` | `name = "NINA - YYYY-MM-DD - Calibrations"` |

## Authentication

| Concept | Plugin | Endpoint |
|---|---|---|
| Sign in | username + password | `POST /auth/token/` |
| Refresh | stored refresh token | `POST /auth/token/refresh/` |
| Sign out | clears local + server | `POST /auth/logout/` |

Every request carries `X-Arcsecond-Client: nina-plugin` and an `Authorization: Bearer <access>` header. Backend recognises the client type via the `NINA_PLUGIN_JWT_CLIENT_TYPE` constant in `accounts/jwt_tokens.py` (applied).

## FITS upload + per-frame typed records

For each saved FITS frame the plugin does **two server-side writes**:

### Write 1 — the binary file

| Step | Endpoint |
|---|---|
| Upload the FITS file | `POST /datasets/<dataset_uuid>/datafiles/` (multipart `image/fits` + `sha256` + `idempotency_key`) |

No `metadata` JSON part — NINA writes 70+ FITS header keywords ([`NINA.Image/FileFormat/FITS/CFitsioFITS.cs:98-392`](../nina/NINA.Image/FileFormat/FITS/CFitsioFITS.cs)) and arcsecond-back's existing extractor lands them in `DataFile.pixel_metrics`.

### Write 2 — the typed record (Observation or Calibration)

The plugin classifies the frame from NINA's `ImageMetaData.Image.ImageType` (= FITS `IMAGETYP`):

| `IMAGETYP` | Dataset | Per-frame record |
|---|---|---|
| `LIGHT`, `SNAPSHOT`, anything unknown | Observations Dataset | `POST /observingruns/nightlogs/<uuid>/observations/` |
| `DARK`, `BIAS`, `FLAT`, `DARKFLAT`, `FLATDARK`, `BIASDARK` | Calibrations Dataset | `POST /observingruns/nightlogs/<uuid>/calibrations/` |

**Observation body:**

| Field | Source | Notes |
|---|---|---|
| `name` | File name | |
| `start` | `ImageMetaData.Image.ExposureStart` (UTC) | |
| `duration` | `ImageSavedEventArgs.Duration` | Django DurationField "HH:MM:SS.ffffff" |
| `target` | `TargetResolver.ResolveAsync(name)` → XTarget pk | Find-or-create against `/targets/?name=…` then `POST /targets/` with `target_class=AstronomicalObject`, `mode=auto`. Cached in-process. |
| `position_angle` | `ImageMetaData.Target.PositionAngle` (fallback to `Rotator.Position`) | Newly added field on the `Observation` model (applied). |

**Calibration body:**

| Field | Source | Notes |
|---|---|---|
| `name` | File name | |
| `start` | `ImageMetaData.Image.ExposureStart` (UTC) | |
| `duration` | `ImageSavedEventArgs.Duration` | |
| `type` | NINA `IMAGETYP` → mapped | `DARK` → `Darks`, `BIAS` → `Biases`, `FLAT` → `Flats` |

The DataFile↔Observation OneToOne link is **not** populated by the plugin today — the back-end's `ObservationSerializer.data_file` is a nested writable serializer, not a UUID reference. A future PATCH endpoint (or a server-side reconciliation task matching by `night_log + start + filename`) can join them. Until then, both rows exist on the same NightLog and can be cross-referenced by timestamp.

## Targets

The plugin resolves NINA target names via:

```
GET  /targets/?name=<name>      → if any hit, use first .pk
POST /targets/ { name, target_class: "AstronomicalObject", mode: "auto" }
```

This uses arcsecond-back's `XTargetManager.get_or_create` semantics — for catalogue names (M51, NGC 5194, …), the server auto-resolves an `AstronomicalObject`. The `XTarget.pk` is cached in-process so repeated frames of the same target avoid round-trips. See [`Api/TargetResolver.cs`](src/Focale.NINA.Plugin/Api/TargetResolver.cs).

## Sequence push

On `ISequenceMediator.SequenceStarting`, the plugin publishes the running NINA Advanced Sequencer:

| Step | Endpoint | Body |
|---|---|---|
| Create sequence | `POST /observingsequences/` | `name`, `night = NightDate.Format(today)`, `status = "running"` |
| For each target container | `POST /observingsequencesteps/` | `name`, `description`, `index`, `task_mode = "sequential"`, `classification = "observation"` |
| For each child item | `POST /observingsequencetasks/` | `task_name = item.GetType().Name`, `task_kwargs` collects properties like ExposureTime, ExposureCount, Gain, Filter, Binning, Roi via reflection |
| On `SequenceFinished` | `PATCH /observingsequences/<id>/` | `status = "completed"` |

**No target name / RA / Dec / PA on the SequenceStep.** That data lives where it belongs — on the per-frame `Observation` rows via their FK to `XTarget`, plus `Observation.position_angle`. The SequenceStep is just a planning anchor.

## Equipment (v2)

Equipment data flows into Focale today via FITS header keywords NINA already writes (`INSTRUME`, `TELESCOP`, `FOCNAME`, `FWHEEL`, `BAYERPAT`, …). The plugin does **not** auto-register Camera / Mount / Focuser / FilterWheel instances against `/equipments/<type>/` yet — that needs:

1. Heuristics to match NINA driver model strings to existing `EquipmentModel` rows via the `EquipmentModelAlias` system,
2. Optionally `SensorModel.bayer_pattern` (CharField) added to the catalog (currently only `mode = color|mono`).

Sketch and tradeoffs in the v2 issue tracker.

## Summary of backend changes (applied)

| File | Change | State |
|---|---|---|
| `accounts/jwt_tokens.py` | Added `NINA_PLUGIN_JWT_CLIENT_TYPE = "nina-plugin"` to `JWT_CLIENT_TYPES` | ✓ applied |
| `observingsequences/models/steps.py` + serializer + migrations `0010` & `0011` | Adds then removes `SequenceStep.metadata` (net no-op). The two migrations form an additive trail safe to commit either way; if the migration hasn't been deployed yet, you can also `git restore --staged` and delete the `0010`/`0011` pair for a clean history. | ✓ net no-op |
| `observingruns/models/observations.py` + serializer + migration `0008` | Added `Observation.position_angle` FloatField (nullable). | ✓ applied |

Everything else uses what was already there.
