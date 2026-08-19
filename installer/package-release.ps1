# Packages a ready-to-run Release build to hand to the client.
# Produces dist\DawloomAttendance\ (the runnable folder) and a zip on the Desktop.
#
# Bundles: the Release x86 build, the ZKTeco SDK + Register.bat, a pre-configured
# settings file (current device + email), and a README. Build Release first:
#   msbuild DawloomAttendance\DawloomAttendance.csproj /t:Build /p:Configuration=Release /p:Platform=x86 /p:ZkSdk=true

$ErrorActionPreference = "Stop"
$root    = Split-Path $PSScriptRoot -Parent
$version = "1.1.0"
$rel     = Join-Path $root "DawloomAttendance\bin\Release"
$sdk     = Join-Path $root "DawloomAttendance\lib\zkteco"
$cfgSrc  = Join-Path $PSScriptRoot "DawloomAttendance.exe.config"
$stage   = Join-Path $root "dist\DawloomAttendance"

if (-not (Test-Path (Join-Path $rel "DawloomAttendance.exe"))) {
    throw "Release build not found at $rel. Build Release x86 first (see header)."
}

# Fresh staging folder.
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# 1) App build output (exe, dlls, x86/x64 SQLite interop subfolders).
Copy-Item (Join-Path $rel "*") $stage -Recurse -Force

# 2) ZKTeco SDK native DLLs + Register.bat, next to the exe so registration resolves deps.
Copy-Item (Join-Path $sdk "*") $stage -Recurse -Force

# 3) Pre-configured settings (current device + email) — overwrite the built default config.
Copy-Item $cfgSrc (Join-Path $stage "DawloomAttendance.exe.config") -Force

# 4) Trim developer artefacts the client doesn't need.
Get-ChildItem $stage -Recurse -Include *.pdb,*.xml | Remove-Item -Force -ErrorAction SilentlyContinue

# 5) Client README.
$readme = @"
Dawloom Attendance $version
============================

REQUIREMENTS
  - Windows 10/11 (64-bit is fine).
  - Microsoft .NET Framework 4.7.2 or later (already on most Windows installs).
    If the app won't start, install it from:
    https://dotnet.microsoft.com/download/dotnet-framework/net472

FIRST-TIME SETUP (one time, ~1 minute)
  1. Copy this whole folder anywhere on the PC (e.g. C:\Dawloom Attendance).
  2. Right-click "Register.bat"  ->  "Run as administrator".
     (This registers the ZKTeco fingerprint SDK so the app can talk to the device.)
  3. Double-click "DawloomAttendance.exe" to start.

ALREADY CONFIGURED
  - Device: 192.168.18.200, port 4370, machine #1.
  - Email sending is set up and turned on.
  You can change any of this in the app: left sidebar -> Settings.

NOTES
  - Attendance data is stored per-user at:
      %LOCALAPPDATA%\DawloomAttendance\dawloom.db
    with automatic daily backups in the "backups" subfolder there.
  - The device must be reachable on the network (same LAN / correct IP).

Support: contact your installer.
"@
Set-Content -Path (Join-Path $stage "README.txt") -Value $readme -Encoding UTF8

# 6) Zip to the Desktop for easy sharing.
$desktop = [Environment]::GetFolderPath('Desktop')
$zip = Join-Path $desktop "Dawloom_Attendance_v$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip

"Staged folder: $stage"
"Shareable zip: $zip  ($([math]::Round((Get-Item $zip).Length/1MB,1)) MB)"
