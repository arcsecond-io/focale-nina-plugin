# Endpoint mapping — NINA plugin → arcsecond-back

How the plugin maps NINA concepts onto **existing** arcsecond-back resources, using **typed FKs** (not free-form JSON) wherever the schema already supports it. URL prefix: arcsecond-back mounts every app at the URL root, so all paths below are absolute under the configured Server URL (default `https://api.focale.space`).

## Night convention

A "night" runs from **local noon (date D) to local noon (date D+1)** and is dated D. The plugin uses this date for `NightLog.date` and the `ObservingSequence.night` field. See [`Common/NightDate.cs`](src/Focale.NINA.Plugin/Common/NightDate.cs).

## Authentication

| Concept | Plugin | Endpoint |
|---|---|---|
| Sign in | username + password | `POST /auth/token/` |
| Refresh | stored refresh token | `POST /auth/token/refresh/` |
| Sign out | clears local + server | `POST /auth/logout/` |

Every request carries `X-Focale-Client: nina-plugin` and an `Authorization: Bearer <access>` header. Backend recognises the client type via the `NINA_PLUGIN_JWT_CLIENT_TYPE` constant in `accounts/jwt_tokens.py` (applied).

## FITS upload — single-shot auto-ingest

For each saved FITS frame the plugin does **one server-side write**:

```
POST /datafiles/
  Content-Type: multipart/form-data
  Idempotency-Key: <sha256>
  ─ file:         <FITS bytes, application/fits>
  ─ sha256:       <hex>
  ─ idempotency_key: <hex>          (same as sha256, server accepts either)
  ─ auto_ingest:  true
  ─ persist_file: true
  ─ source:       observer_console
```

That single POST creates, in one transaction on the backend:

| What | Where it comes from |
|---|---|
| `DataFile` (+ binary in R2 / Cloudflare) | the multipart `file` part |
| `pixel_metrics` (width, height, min/max/mean/median/std, saturation, star count, FWHM, HFR) | server-side astropy + photutils on the uploaded bytes |
| `ObservingSite` | `OBSERVAT`, or `SITELAT` + `SITELONG` (with `fill_address_and_timezone`) |
| `Telescope` | `TELESCOP`, with `TelescopeModel` linked via the `equipmentmodels` resolver |
| `Camera` | `INSTRUME`, with `CameraModel` linked via the resolver |
| `NightLog` | `DATE-OBS` → local-noon-to-noon rule via the site's timezone; `source = observer_console` |
| `Dataset` | one per (NightLog, classification): `"Observations - YYYY-MM-DD"` or `"Calibrations - YYYY-MM-DD"` |
| `Observation` or `Calibration` | classification from `OBJECT` / `IMAGETYP`; PA from `OBJCTPA` / `POSANG` / `ROTATEDR`; target resolved via SIMBAD + coords |

No JSON metadata part — the FITS header is the canonical source. The plugin's job is to compute the SHA-256 and POST. Everything else lives on the backend.

### Idempotency

The same sha256 from the same owner short-circuits server-side: the existing `DataFile` is returned, no second `Observation` is created, no `NightLog` roll-over noise. Retries on flaky networks are safe by construction.

### Source attribution

`source = observer_console` ensures these rows are distinguishable from Focale Memory drops (which use `data_upload_ingestion`) and from arcsecond-cli / Oort uploads (`data_upload_processing`). The same `NightLog` row can have frames from multiple sources — they're keyed on `(date, telescope, source, owner)`, so live capture and drag-and-drop never collide.

## Sequence push

On `ISequenceMediator.SequenceStarting`, the plugin publishes the running NINA Advanced Sequencer:

| Step | Endpoint | Body |
|---|---|---|
| Create sequence | `POST /observingsequences/` | `name`, `night = NightDate.Format(today)`, `status = "running"` |
| For each target container | `POST /observingsequencesteps/` | `name`, `description`, `index`, `task_mode = "sequential"`, `classification = "observation"` |
| For each child item | `POST /observingsequencetasks/` | `task_name = item.GetType().Name`, `task_kwargs` collects properties like ExposureTime, ExposureCount, Gain, Filter, Binning, Roi via reflection |
| On `SequenceFinished` | `PATCH /observingsequences/<id>/` | `status = "completed"` |

**No target name / RA / Dec / PA on the SequenceStep.** That data lives where it belongs — on the per-frame `Observation` rows via their FK to `XTarget`, plus `Observation.position_angle`. The SequenceStep is just a planning anchor.

## Equipment

Equipment is auto-registered server-side from the FITS header on auto-ingest — `TELESCOP` becomes a `Telescope` row (with `TelescopeModel` linked when the resolver matches), `INSTRUME` becomes a `Camera` row (with `CameraModel`). The plugin itself does not POST to `/equipments/<type>/` endpoints.

## Summary of backend changes (applied)

| File | Change | State |
|---|---|---|
| `accounts/jwt_tokens.py` | Added `NINA_PLUGIN_JWT_CLIENT_TYPE = "nina-plugin"` to `JWT_CLIENT_TYPES` | ✓ applied |
| `observingsequences/models/steps.py` + serializer + migrations `0010` & `0011` | Adds then removes `SequenceStep.metadata` (net no-op). | ✓ net no-op |
| `observingruns/models/observations.py` + serializer + migration `0008` | Added `Observation.position_angle` FloatField (nullable). | ✓ applied |
| `observingruns/models/nightrecords.py` + migration `0009` | Added `NightLog.Source.DATA_UPLOAD_INGESTION` for the Focale Memory drop flow. | ✓ applied |
| `datasets/models/datafiles.py` + migration `0006` | Added `DataFile.sha256` (CharField, indexed) for idempotency lookup. | ✓ applied |
| `datasets/serializers/datafiles.py` + `datasets/tasks/ingestion.py` + `datasets/tasks/_common.py` | The `auto_ingest=true` path: source override, sha256 idempotency, `OBJCTPA` → `Observation.position_angle`, server-side pixel metrics + star metrics. | ✓ applied |

Everything else uses what was already there.
