# Endpoint mapping — NINA plugin → Focale API (arcsecond-back)

How the plugin maps NINA concepts onto **existing** arcsecond-back resources, using **typed FKs** (not free-form JSON) wherever the schema already supports it. URL prefix: arcsecond-back mounts every app at the URL root, so all paths below are absolute under the configured Server URL (default `https://api.focale.space`).

## Night convention

A "night" runs from **local noon (date D) to local noon (date D+1)** and is dated D. The back-end applies it to `DATE-OBS` (in the site's timezone) to pick the `NightLog`; the plugin only uses it for log lines. See [`Common/NightDate.cs`](src/Focale.NINA.Plugin/Common/NightDate.cs).

## Authentication

| Concept | Plugin | Endpoint |
|---|---|---|
| Sign in | email or username + password | `POST /auth/token/` |
| Refresh | stored refresh token | `POST /auth/token/refresh/` |
| Sign out | clears local + server | `POST /auth/logout/` |
| Plan | the user's Focale tier | `GET /profiles/<username>/` |

Sign-in refusals are put in words: `400` wrong credentials, `403 {"detail": "email_not_verified"}` (verify the email first), `429` too many attempts. The refresh endpoint answers **`400`** `"Invalid or expired refresh token."` for an expired (14 days) or revoked token: the plugin then forgets it and asks the user to sign in again (401/403 are treated the same, for safety).

The account (server URL, username, DPAPI-protected refresh token) is kept in `%LOCALAPPDATA%\NINA\PluginData\Focale\account.json`, shared by every NINA profile; the storage choice and features stay in each profile's plugin settings. An account kept in a profile by an earlier build is moved there on first start.

### Plan

`GET /profiles/<username>/` (the owner's private profile) gives `focale_tier` (`free`, `observer`, `imager`, `pro`; `pro` on a self-hosted Focale) and `focale_storage` (`limit_bytes`, …). It is read after sign-in, at start, on **Check my plan again** / **Resume**, and every 15 minutes while it does not include the sync. On Free, frames and events wait instead of being sent and refused; an unknown tier gates nothing.

Every request carries `X-Focale-Client: nina-plugin` and an `Authorization: Bearer <access>` header. Backend recognises the client type via the `NINA_PLUGIN_JWT_CLIENT_TYPE` constant in `accounts/jwt_tokens.py` (applied).

## NINA → Focale sync ("NINA import" in the pricing table)

### FITS upload — single-shot auto-ingest

For each saved FITS frame the plugin does **one server-side write**:

```
POST /datafiles/
  Content-Type: multipart/form-data
  Idempotency-Key: <sha256>
  ─ file:         <FITS bytes, application/fits>
  ─ sha256:       <hex, lowercase>
  ─ auto_ingest:  true
  ─ persist_file: true | false       (false in "index only" mode)
  ─ destination:  <storage uuid>     ("my own storage" mode only)
  ─ relative_path: 2026-10-27/M31/LIGHT/M31_0001.fits   ("my own storage" mode only: the path below NINA's image folder)
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
| 400 naming `destination` | Stays queued; the queue pauses as above, asking to choose another storage (the chosen one was removed, made read-only, or is a Dropbox/FTP storage). |
| Any other 4xx | Dead-lettered: the server will never accept that file. |

### Idempotency

Before sending a frame, the plugin asks `GET /datafiles/?sha256=<hex>`: a frame Focale already has (an upload cut short after the server filed it) is not sent again, unless only its index is there (`stored: false`) and this upload would keep the pixels. The answer's `ingestion.night_log` is used as an upload's would be. A failed lookup only means the frame is sent.

The same sha256 from the same owner short-circuits server-side: the existing `DataFile` is returned, no second `Observation` is created, no `NightLog` roll-over noise. Retries on flaky networks are safe by construction.

### Source attribution

`source = observer_console` ensures these rows are distinguishable from Focale Memory drops (which use `data_upload_ingestion`) and from arcsecond-cli / Oort uploads (`data_upload_processing`). The same `NightLog` row can have frames from multiple sources — they're keyed on `(date, telescope, source, owner)`, so live capture and drag-and-drop never collide.

### Storage modes

Chosen once in the settings page; until then nothing is uploaded and frames wait in the queue.

| Mode | Fields | What Focale keeps |
|---|---|---|
| Index only | `persist_file=false` | Every row (night, target, observation, metrics) — not the pixels. No storage used. |
| Focale storage | `persist_file=true` | The file too, on Focale's cloud storage (counts against the plan). |
| My own storage | `persist_file=true`, `destination=<uuid>`, `relative_path` | The file too, on the user's storage, as `Focale/<relative_path>` under its root (NINA's folders kept; scans skip `Focale/`). Listed from `GET /datastorages/`, keeping those with `can_receive_uploads` (S3, SFTP, SMB, FTP, and Dropbox connected with "Connect Dropbox" — a pasted Dropbox token expires in four hours). |

The upload response's `ingestion.night_log` (`{uuid, date}`) is remembered per night date for the night events below.

### Plan limits

On the Focale deploy the upload passes the plan gates of `entitlements/focale.py` before anything is filed: NINA import (`source=observer_console`) needs the Observer plan or above, the indexed FITS/XISF files are capped (Free 100, Observer 1,000), and so is Focale storage when the file is kept there (10 / 50 / 250 GB / 1.5 TB). Each refusal is a `403 {"error": "subscription_required", "gate": …, "message": …}`; the plugin pauses and shows the message. A re-upload of a frame Focale already has (same sha256) is never refused.

### Night events

The plugin records what happens during the night from NINA's mediators and files it under the night log its frames went into:

```
POST /nightlogs/<night_log_uuid>/events/
  [ {"client_id": "<guid>", "kind": "autofocus", "start": "2026-10-27T21:04:10.000Z",
     "end": "2026-10-27T21:06:55.000Z", "success": true,
     "data": {"position": 12345, "temperature_c": 8.44, "filter": "L"}}, … ]
```

| Kind | From (NINA 3.2 and 3.3) | Shape |
|---|---|---|
| `sequence` | `ISequenceMediator.SequenceStarting` / `SequenceFinished` | span; sent once it ends (or without an end after 18 h) |
| `autofocus` | `IFocuserConsumer.AutoFocusRunStarting` / `UpdateEndAutoFocusRun` | span, `position`, `temperature_c`, `filter`; NINA only reports successes, so a run still open at the next start or at the end of the sequence is sent with `success=false` and no end |
| `meridian_flip` | `ITelescopeMediator.BeforeMeridianFlip` / `AfterMeridianFlip` | span, `success` |
| `guiding` | `ImageSaved` → `MetaData.Image.RecordedRMS` | one per exposure (start/end = the exposure): `rms_{ra,dec,total}_arcsec`, `peak_{ra,dec}_arcsec`, `data_points`, `file_name`; `rms_*_px` instead when the guider has no pixel scale |
| `dither` | `IGuiderMediator.AfterDither` | instant |
| `park`, `unpark` | `ITelescopeMediator.Parked` / `Unparked` | instant |
| `safety` | `ISafetyMonitorMediator.IsSafeChanged` | instant, `is_safe` |

Events are kept in `%LOCALAPPDATA%\NINA\PluginData\Focale\events.db` and sent in batches of up to 200, idempotently (`client_id` is unique per night log, so a retried batch files nothing twice). An event belongs to the night (local noon to noon) its start falls in, and waits until an upload has reported that night's log; events of nights with no uploaded frame are dropped after 7 days. Writing events is the same `nina_import` plan gate; reading them (the Logbook timeline) is not gated. `image_save_failed` exists on the back-end but is not sent: NINA's `ImageSaveFailed` is 3.3-only.

## Focale → NINA plans (the "Focale → NINA plans" panel; "NINA push" in the pricing table)

```
GET /nightplans/?date=2026-10-28           → the user's night plan of that night (Focale's Tonight; the first)
GET /nightplans/<uuid>/exposures/          → its entries, in order:
  {"night_plan", "date", "entries": [
     {"uuid", "index", "name", "start", "duration_seconds", "status", "position_angle",
      "target": {"id", "name", "target_class", "right_ascension", "declination"},   (J2000, degrees)
      "lines": [{"filter": "Ha", "exposure_seconds": 300, "count": 20, "binning": 1}]}]}
```

The lines are written in Focale's Tonight (PUT `/nightplans/<uuid>/exposures/<target_id>/`, gate `nina_push`: Imager plan and up) and kept as `ExposureLine` rows of the entry's observation (`observingruns/models/exposure_lines.py`). Reading them is not gated; below Imager the panel says each target keeps the template's exposures.

Per entry the plugin plans (`Push/TargetPlanner`): filters are matched by name in the active profile's filter wheel (case and punctuation aside, never guessed; unmatched lines are left out and shown); a target without fixed coordinates (a planet, a comet) cannot be added; an entry without lines keeps the template's exposures. Then it builds (`Push/NinaTargetBuilder`): the chosen template is cloned, its target set (name, J2000 coordinates, position angle), its Smart Exposures replaced by clones of the first one, one per line (filter, exposure time, binning, loop count), and `ISequenceMediator.AddAdvancedTarget` adds it. Gain and offset stay the template's.

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

| `observingruns/models/events.py` + serializer + view + migration `0021` | `NightEvent` and `nightlogs/<uuid>/events/` (list, single or batch POST, idempotent on `client_id`). | Phase 1 |
| `entitlements/focale.py` (+ `constants.py`, `Gate`) | Focale plan gates: `nina_import`, `file_indexing_limit`, `storage_limit`; per-plan storage cap in `UserProfile.data_volume_limit_bytes`. | Phase 1 |
| `datasets/serializers/datafiles.py` | Plan gates on auto-ingest; `destination` must be a storage type the back-end writes to. | Phase 1 |

Everything else uses what was already there.
