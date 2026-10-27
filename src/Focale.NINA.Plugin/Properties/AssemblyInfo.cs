using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Resources;

// Plugin identity. The Guid is the immutable plugin Identifier; never change it.
[assembly: Guid("9F4D2F1E-7C3A-4E5B-8B2D-1F7A6E9C0D5A")]

// Manifest fields read by NINA.Plugin.PluginBase.
[assembly: AssemblyTitle("Focale")]
[assembly: AssemblyDescription("Sends every FITS / XISF frame and what happens during the night (autofocus, flips, guiding RMS) from N.I.N.A. 3.2 and 3.3 to Focale.")]
[assembly: AssemblyCompany("Cédric Foellmi / Focale")]
[assembly: AssemblyProduct("Focale.NINA.Plugin")]
[assembly: AssemblyCopyright("Copyright © 2026 Cédric Foellmi / Focale")]

// AssemblyVersion is read at runtime — bump on each release.
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
[assembly: AssemblyInformationalVersion("0.1.0-alpha")]

[assembly: NeutralResourcesLanguage("en")]
[assembly: ComVisible(false)]
// GenerateAssemblyInfo is off, so the SDK does not emit this from the -windows TFM itself.
[assembly: SupportedOSPlatform("windows")]
[assembly: InternalsVisibleTo("Focale.NINA.Plugin.Tests")]

// PluginBase reads these AssemblyMetadata keys via reflection.
[assembly: AssemblyMetadata("License",                 "MPL-2.0")]
[assembly: AssemblyMetadata("LicenseURL",              "https://www.mozilla.org/en-US/MPL/2.0/")]
[assembly: AssemblyMetadata("Homepage",                "https://focale.space")]
[assembly: AssemblyMetadata("Repository",              "https://github.com/arcsecond-io/focale-nina-plugin")]
[assembly: AssemblyMetadata("ChangelogURL",            "https://github.com/arcsecond-io/focale-nina-plugin/releases")]
[assembly: AssemblyMetadata("Tags",                    "Focale,Cloud,Upload,FITS,XISF,Logbook")]
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.0")]
[assembly: AssemblyMetadata("LongDescription",         @"Bridges N.I.N.A. to Focale (https://focale.space). Works with NINA 3.2 and 3.3.

Frames: every FITS or XISF frame NINA saves is sent to Focale, which reads its header and files it under the right night, site, telescope, camera and target, with image metrics computed server-side. You choose what happens to the pixels: index only (the file stays on your computer), Focale storage, or one of your own storages (S3, SFTP, SMB). Nothing is uploaded until you choose.

Night events: the sequence, autofocus runs, meridian flips, guiding RMS per exposure, dithers, park and safety changes are sent to the same night, and shown on its Logbook timeline.

Everything goes through durable on-disk queues: while you are offline, signed out, or over a plan limit, frames and events wait and go up once that clears.

NINA import is part of Focale's Observer plan and above.

Privacy: you sign in with your Focale account. The refresh token is stored encrypted with Windows DPAPI; the access token stays in memory. Only the saved image files and, if enabled, the night's events are sent - no NINA profile, settings or log files.")]
[assembly: AssemblyMetadata("FeaturedImageURL",        "https://focale.space/img/nina-plugin-featured.png")]
[assembly: AssemblyMetadata("ScreenshotURL",           "https://focale.space/img/nina-plugin-screenshot-1.png")]
[assembly: AssemblyMetadata("AltScreenshotURL",        "https://focale.space/img/nina-plugin-screenshot-2.png")]
