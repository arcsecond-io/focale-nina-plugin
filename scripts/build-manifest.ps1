<#
.SYNOPSIS
  Produces a release-ready manifest.json by stamping version + sha256 into manifest.template.json.

.PARAMETER Version
  The 4-part version string (e.g. 0.1.0.0). Must match the assembly's AssemblyFileVersion.

.PARAMETER ZipPath
  Path to the release zip whose SHA-256 checksum will be embedded.

.PARAMETER OutPath
  Where to write the resolved manifest.json (default: ./artifacts/manifest.json).

.NOTES
  This script does not publish the manifest. Submission to the upstream NINA plugin registry
  is done manually (or via your repository's release workflow) once the manifest is reviewed.
#>
param(
  [Parameter(Mandatory)] [string] $Version,
  [Parameter(Mandatory)] [string] $ZipPath,
  [string] $OutPath = "artifacts/manifest.json"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $ZipPath)) { throw "Zip not found: $ZipPath" }

$parts = $Version.Split('.')
if ($parts.Count -ne 4) { throw "Version must be 4-part (Major.Minor.Patch.Build), got '$Version'" }

$sha = (Get-FileHash -Algorithm SHA256 $ZipPath).Hash.ToLowerInvariant()
$tpl = Get-Content -Raw "manifest.template.json" | ConvertFrom-Json

$tpl.Version.Major = $parts[0]
$tpl.Version.Minor = $parts[1]
$tpl.Version.Patch = $parts[2]
$tpl.Version.Build = $parts[3]

$tpl.Installer.URL      = $tpl.Installer.URL.Replace("__VERSION__", $Version)
$tpl.Installer.Checksum = $sha

$outDir = Split-Path -Parent $OutPath
if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$tpl | ConvertTo-Json -Depth 16 | Set-Content -Path $OutPath -Encoding UTF8
Write-Host "Wrote $OutPath (sha256=$sha)"
