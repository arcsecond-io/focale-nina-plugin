using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Resources;

// Plugin identity. The Guid is the immutable plugin Identifier; never change it.
[assembly: Guid("9F4D2F1E-7C3A-4E5B-8B2D-1F7A6E9C0D5A")]

// Manifest fields read by NINA.Plugin.PluginBase.
[assembly: AssemblyTitle("Focale")]
[assembly: AssemblyDescription("Connects N.I.N.A. 3.2 and 3.3 to Focale, both ways: NINA → Focale sync (every frame, session and night log) and Focale → NINA plans (tonight's targets and exposure plans).")]
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
[assembly: AssemblyMetadata("Tags",                    "Focale,Cloud,Sync,Upload,FITS,XISF,Logbook,Planning,Sequencer")]
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.0")]
[assembly: AssemblyMetadata("LongDescription",         @"Connects N.I.N.A. to Focale (https://focale.space), both ways. Works with NINA 3.2 and 3.3.

NINA → Focale sync (Observer plan and above): every FITS or XISF frame NINA saves fills your Focale memory. Focale reads its header and files it under the right night, site, telescope, camera and target, with image metrics computed server-side. You choose what happens to the pixels: index only (Focale reads each frame, keeps the metadata and measurements, and discards the file), Focale storage, or one of your own storages (S3, SFTP, SMB, FTP, Dropbox). Nothing is uploaded until you choose. The night's log - the sequence, autofocus runs, meridian flips, guiding RMS per exposure, dithers, park and safety changes - goes to the same night and makes its Logbook timeline.

Focale → NINA plans (Imager plan and above): the Focale → NINA plans panel lists tonight's plan from Focale - targets, times, filters, exposures - and adds each target to NINA's Advanced Sequencer, built from one of your own target templates.

After you sign in, the settings page shows your Focale plan and what it includes. Everything goes through durable on-disk queues: while you are offline, signed out, or over a plan limit, frames and events wait and go up once that clears.

Privacy: you sign in with your Focale account; the password is never stored. The session token is stored encrypted with Windows DPAPI. Only the saved image files and, if enabled, the night's events are sent - no NINA profile, settings or log files.")]
[assembly: AssemblyMetadata("FeaturedImageURL",        "https://raw.githubusercontent.com/arcsecond-io/focale-nina-plugin/main/assets/nina-plugin-featured.png")]
