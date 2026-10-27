# Endpoint mapping — NINA plugin → arcsecond-back

How the plugin maps NINA concepts onto **existing** arcsecond-back resources, using **typed FKs** (not free-form JSON) wherever the schema already supports it. URL prefix: arcsecond-back mounts every app at the URL root, so all paths below are absolute under the configured Server URL (default `https://api.focale.space`).

## Night convention

A "night" runs from **local noon (date D) to local noon (date D+1)** and is dated D. The back-end applies it to `DATE-OBS` (in the site's timezone) to pick the `NightLog`; the plugin only uses it for log lines. See [`Common/NightDate.cs`](src/Focale.NINA.Plugin/Common/NightDate.cs).

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

No JSON metadata part — the FITS/XISF header is the canonical source. The plugin's job is to compute the SHA-256 and POST. Everything else lives on the backend. Only `.fits` / `.fit` / `.fts` / `.xisf` files are queued.

Before each upload the plugin refreshes its access token if it is missing or about to expire (it lives in memory only, so this always happens on the first upload after a NINA restart): a streamed multipart body cannot be replayed after a 401.

### Failures

| Response | What happens to the frame |
|---|---|
| 2xx | Removed from the queue. |
| No response (offline, DNS, TLS, timeout) | Stays queued, retried every 60 s; does not count as an attempt. |
| 401 | Stays queued; the plugin forces a token refresh. If the refresh token is rejected too, the queue waits for the user to sign in again. |
| 402 / 403 / 507 | Stays queued; the whole queue pauses for an hour (or until **Resume uploads**), showing the server's `message` — plan gates answer `403 {"error": "subscription_required", "message": …}`. |
| 408 / 425 / 429 / 5xx | Retried with exponential backoff (2 s … 5 min), dead-lettered after 300 attempts (about a day). |
| Any other 4xx | Dead-lettered: the server will never accept that file. |

### Idempotency

The same sha256 from the same owner short-circuits server-side: the existing `DataFile` is returned, no second `Observation` is created, no `NightLog` roll-over noise. Retries on flaky networks are safe by construction.

### Source attribution

`source = observer_console` ensures these rows are distinguishable from Focale Memory drops (which use `data_upload_ingestion`) and from arcsecond-cli / Oort uploads (`data_upload_processing`). The same `NightLog` row can have frames from multiple sources — they're keyed on `(date, telescope, source, owner)`, so live capture and drag-and-drop never collide.

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
