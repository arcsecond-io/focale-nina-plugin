using System.Reflection;
using System.Runtime.InteropServices;
using System.Resources;

// Plugin identity. The Guid is the immutable plugin Identifier; never change it.
[assembly: Guid("9F4D2F1E-7C3A-4E5B-8B2D-1F7A6E9C0D5A")]

// Manifest fields read by NINA.Plugin.PluginBase.
[assembly: AssemblyTitle("Focale")]
[assembly: AssemblyDescription("Pushes NINA equipment & sequence metadata to Focale and uploads captured FITS frames automatically.")]
[assembly: AssemblyCompany("Cédric Foellmi / Focale")]
[assembly: AssemblyProduct("Focale.NINA.Plugin")]
[assembly: AssemblyCopyright("Copyright © 2026 Cédric Foellmi / Focale")]

// AssemblyVersion is read at runtime — bump on each release.
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
[assembly: AssemblyInformationalVersion("0.1.0-alpha")]

[assembly: NeutralResourcesLanguage("en")]
[assembly: ComVisible(false)]

// PluginBase reads these AssemblyMetadata keys via reflection.
[assembly: AssemblyMetadata("License",                 "MPL-2.0")]
[assembly: AssemblyMetadata("LicenseURL",              "https://www.mozilla.org/en-US/MPL/2.0/")]
[assembly: AssemblyMetadata("Homepage",                "https://focale.space")]
[assembly: AssemblyMetadata("Repository",              "https://github.com/arcsecond-io/focale-nina-plugin")]
[assembly: AssemblyMetadata("ChangelogURL",            "https://github.com/arcsecond-io/focale-nina-plugin/releases")]
[assembly: AssemblyMetadata("Tags",                    "Focale,Arcsecond,Cloud,Upload,FITS,Metadata")]
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.3.0.0")]
[assembly: AssemblyMetadata("LongDescription",         @"Bridges N.I.N.A. to Focale (https://focale.space).

Two integrations:

1. Metadata sync — event-driven JSON snapshots of the active equipment profile, the running sequencer tree, target list, device runtime state, and observatory/site info are pushed to Focale on profile changes, device connect/disconnect, sequence start/finish, slews and filter changes, so each Focale session is correlated with what NINA actually had configured at the time.

2. Automatic FITS upload — every successfully saved FITS frame (with its full ImageMetaData) is uploaded to Focale in the background via a durable on-disk queue that survives network drops and app restarts.

Privacy: API key is stored DPAPI-encrypted at rest. Known secret-bearing fields (Astrometry / PinPoint API keys, anything matching Password/ApiKey/Token/Secret/Credential) are redacted before any payload is sent. Both upload and metadata sync have their own toggle and default to the user's explicit opt-in after key entry.")]
[assembly: AssemblyMetadata("FeaturedImageURL",        "https://focale.space/img/nina-plugin-featured.png")]
[assembly: AssemblyMetadata("ScreenshotURL",           "https://focale.space/img/nina-plugin-screenshot-1.png")]
[assembly: AssemblyMetadata("AltScreenshotURL",        "https://focale.space/img/nina-plugin-screenshot-2.png")]
