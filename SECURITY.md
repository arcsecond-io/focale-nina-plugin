# Security policy

## Supported versions

The latest released version is supported. Pre-release versions are best-effort.

## Reporting a vulnerability

Please report security issues privately to **security@arcsecond.io** rather than opening a public GitHub issue.

Include, where possible:
- the affected version,
- a reproduction or proof of concept,
- the impact you observed,
- any mitigations you've already identified.

We aim to acknowledge within 5 business days and to ship a fix or mitigation within 30 days for confirmed issues.

## What this plugin sends and how

- The plugin uses HTTPS only. It uses the .NET default `SocketsHttpHandler`, which validates server certificates against the operating system trust store. **Certificate validation is not disabled**, and we will not accept patches that disable it.
- The Focale API key is stored at rest using **Windows DPAPI** (`DataProtectionScope.CurrentUser`) with a fixed plugin-scoped entropy. The key is never written to disk in plaintext.
- Before any payload is serialized, fields whose name matches `Password / ApiKey / Token / Secret / Credential / Auth / Bearer` (case-insensitive) are replaced with `***REDACTED***`. URI string values are also stripped of their userinfo (`user:pass@`) component. There is an explicit allowlist for false positives — additions to that list require code review.
- Both metadata sync and FITS upload have independent on/off toggles, both default to enabled but require an API key to actually transmit anything.

## Threat model assumptions

- The plugin runs on a Windows machine the user controls. We trust the local OS trust store.
- The plugin trusts the configured `ServerUrl` to be the user's intended Focale instance. Pinning a specific certificate would prevent legitimate rotation; we rely on the OS PKI.
- We do **not** assume the local NINA profile file (`profile.xml`) is private — that's why the API key is encrypted separately from it.
