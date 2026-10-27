using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Resources;

// Plugin identity. The Guid is the immutable plugin Identifier; never change it.
[assembly: Guid("9F4D2F1E-7C3A-4E5B-8B2D-1F7A6E9C0D5A")]

// Manifest fields read by NINA.Plugin.PluginBase.
[assembly: AssemblyTitle("Focale")]
[assembly: AssemblyDescription("Uploads every FITS / XISF frame NINA saves to Focale, which rebuilds your nights, targets and equipment from the file headers.")]
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
[assembly: AssemblyMetadata("Tags",                    "Focale,Cloud,Upload,FITS,XISF")]
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.3.0.0")]
[assembly: AssemblyMetadata("LongDescription",         @"Bridges N.I.N.A. to Focale (https://focale.space).

Every FITS or XISF frame NINA saves is uploaded to Focale in the background. Focale reads the file header and files the frame under the right night, site, telescope, camera and target, as an observation or a calibration frame, with image metrics computed server-side.

Uploads go through a durable on-disk queue: frames wait while you are offline, signed out, or over a plan limit, and go up once that clears. A frame is only given up when Focale refuses that specific file.

Privacy: you sign in with your Focale account. The refresh token is stored encrypted with Windows DPAPI; the access token stays in memory. Only the saved image files are sent - no NINA profile, settings or logs. Automatic upload can be turned off at any time.")]
[assembly: AssemblyMetadata("FeaturedImageURL",        "https://focale.space/img/nina-plugin-featured.png")]
[assembly: AssemblyMetadata("ScreenshotURL",           "https://focale.space/img/nina-plugin-screenshot-1.png")]
[assembly: AssemblyMetadata("AltScreenshotURL",        "https://focale.space/img/nina-plugin-screenshot-2.png")]
