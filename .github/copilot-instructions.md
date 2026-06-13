# Copilot Instructions — Dawloom ZKTeco Attendance Integration

## Current state vs target (read this first)
- **Current (built & verified): .NET Framework 4.7.2, WPF, code-behind** (not MVVM toolkit).
  Phase 1 is working on this stack: connect, live capture, SQLite persistence, backfill,
  auto-reconnect monitor, file + DB logging.
- **Planned target: .NET 8 (WPF) + CommunityToolkit.Mvvm**, to be migrated **after** the
  integration is validated against real K70 hardware. Do **not** retarget the project to
  .NET 8 or rewrite to MVVM until that migration is explicitly scheduled.
- Until migration, follow the 4.7.2 conventions in this file. The **SDK hard rules below
  apply to either runtime** and must never be violated.

## Hard rules for the ZKTeco SDK (do not violate)
1. **Always target x86.** `zkemkeeper` is a 32-bit COM object. Never `AnyCPU`/`x64`.
   Csproj must contain `<PlatformTarget>x86</PlatformTarget>` (verified: output PE = 0x14c).
2. **Only `zkemkeeper.dll` is COM-registered.** The other native DLLs (`zkemsdk.dll`,
   `commpro.dll`, `plcommpro.dll`, `comms.dll`, `plcomms.dll`, `rscomm.dll`, `tcpcomm.dll`,
   `usbcomm.dll`) load at runtime and must sit in the output folder. Don't register them.
3. **Registration uses 32-bit regsvr32** at `C:\Windows\SysWOW64\regsvr32.exe`, elevated.
   Never `System32\regsvr32`. Use `lib\zkteco\Register.bat`.
4. **Instantiate the documented coclass** (`new zkemkeeper.CZKEMClass()` under typed interop).
   In the default build we bind late (reflection over the registered ProgID) so the project
   compiles without the SDK; the typed path compiles only with `/p:ZkSdk=true`.
5. **Every device call can fail silently.** After `Connect_Net`, `ReadGeneralLogData`,
   `SSR_GetGeneralLogData`, etc., check the bool return and call `GetLastError(ref code)` on
   failure. Never assume success.
6. **Freeze during bulk reads.** Call `EnableDevice(machineNumber, false)` before a bulk read
   and re-enable in a `finally` (implemented in `ZkDeviceClient.ReadAllLogs`).

## Build commands
- Default (no SDK needed, builds anywhere): `msbuild DawloomAttendance.csproj /p:Platform=x86`
- Live SDK path (requires SDK registered): add `/p:ZkSdk=true` — pulls the `zkemkeeper`
  COM reference and compiles the `ZK_TYPED` live-capture (`OnAttTransactionEx`) code.

## Reading attendance logs
- `ReadGeneralLogData(machineNumber)` to buffer, then loop `SSR_GetGeneralLogData(...)`.
- Dedup on `(EnrollNumber, Timestamp)` via `AppDb.InsertPunchIfNew` (backfill may overlap live).
- TODO (polish): map verify/in-out codes to enums instead of raw ints in the domain model.

## Architecture preferences
- All COM interaction behind `IZkDevice`. No raw `CZKEM` in views.
- COM calls run off the UI thread (`Task.Run`); results marshal back via the Dispatcher.
- `ZkDeviceClient` serializes all COM access with an internal lock (`_sync`) — never call the
  ActiveX object from two threads at once.
- `ZkConnectionMonitor` owns connect → keep-alive ping → reconnect. Its retry is **intentionally
  uncapped** (backoff + user Stop is the bound) — the "max-attempt cap" rule applies to one-shot
  operations, not this self-healing monitor.
- Dispose/Disconnect deterministically; don't rely on GC for the COM object.

## Persistence
- **On 4.7.2: `System.Data.SQLite`** (the SQLite team's provider). Chosen over
  Microsoft.Data.Sqlite because MDS 6.0 NREs in `ApplicationDataHelper` on a non-packaged
  WPF app. The native `SQLite.Interop.dll` is copied to `bin\...\x86` and `\x64` by referencing
  the `Stub.System.Data.SQLite.Core.NetFramework` package directly.
- **On the .NET 8 migration: switch to `Microsoft.Data.Sqlite`** (works there; simpler native handling).
- Logging: two sinks — rolling file (`%LOCALAPPDATA%\DawloomAttendance\logs\dawloom-YYYYMMDD.log`
  via Serilog) and the SQLite `DeviceLog` table (via `AppDb`).

## Things to avoid suggesting
- LINQ-to-COM lazy enumeration over device records (pull fully, then query).
- Hardcoded IPs/ports — read from `DeviceSettings` (App.config + Settings dialog).
- `AnyCPU`, `x64`, or `System32\regsvr32` (see hard rules).
- Retargeting to .NET 8 / rewriting to MVVM before the migration is scheduled.

## Style
- **Current (4.7.2):** C# 7.3, block-scoped namespaces, nullable not enabled, explicit error surfacing.
- **Target (.NET 8):** C# 12, file-scoped namespaces, nullable enabled, CommunityToolkit.Mvvm
  (`[ObservableProperty]`, `[RelayCommand]`).
