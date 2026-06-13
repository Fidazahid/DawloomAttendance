# Dawloom Attendance — Implementation Plan

Custom Attendance Management System with live ZKTeco K70 integration, shift-based calculation, and a real-time dashboard.

Source proposal: `Attendance_System_Proposal.docx` (ATT-2026-001).

---

## Project Snapshot (current state)

- Project: `DawloomAttendance.csproj`
- Type: WPF Desktop Application
- **Current target: .NET Framework 4.7.2** (proposal mentions .NET 8 — decision needed before Phase 1, see "Open Decision" below)
- UI: empty `MainWindow.xaml` shell
- Solution: `DawloomAttendance.sln`

### Open Decision Before Starting

The proposal specifies .NET 8, but the existing `.csproj` targets .NET Framework 4.7.2. The ZKTeco SDK (`zkemkeeper.dll`) is a 32-bit ActiveX COM component, and both runtimes can interop with it as long as the process runs as **x86**. Recommendation:

- **Stay on .NET Framework 4.7.2** if speed-to-MVP matters and the office PC is fixed (lowest interop friction with `zkemkeeper.dll`).
- **Migrate to .NET 8 (WPF)** to match the proposal — still works with the COM SDK via `Interop.zkemkeeper.dll`, but requires a one-time platform retarget and verifying x86 build configuration.

Either way, Phase 1 below works the same — the only change is project file format.

---

## Target Device: ZKTeco K70

| Attribute | Value |
|---|---|
| Fingerprint templates | 1,000 |
| Transactions stored on device | 90,000 |
| RFID cards | 1,000 |
| Communication | TCP/IP, USB Host |
| Algorithm | Finger V10.0 |
| Power | DC 12V 1.5A |
| Default port | 4370 |
| SDK | ZKTeco Standalone SDK (`zkemkeeper.dll`) |

---

## Phase Roadmap

| Phase | Title | Duration | Status |
|---|---|---|---|
| **1** | **Connection — Device Integration & Live Event Capture** | Week 1 | Not started |
| 2 | Employee management, shift assignment, holiday calendar | Week 2 | Not started |
| 3 | Attendance calculation engine (late, absent, overtime, hours) | Week 3 | Not started |
| 4 | Live dashboard with real-time updates | Week 4 | Not started |
| 5 | Reports, Excel/PDF export, leave management, notifications | Week 5 | Not started |
| 6 | Role-based access, payroll export, audit log, backup | Week 6 | Not started |
| 7 | End-to-end testing, deployment, training, handover | Week 7 | Not started |

---

# Phase 1 — Connection (Device Integration & Live Event Capture)

**Goal:** A continuously connected, auto-reconnecting link to the ZKTeco K70 that captures every check-in / check-out the moment it happens, persists the raw event, and surfaces a live connection-status indicator in the UI.

By end of Phase 1 the system can: connect, stay connected, recover from disconnect, receive every punch in real time, and log it to local storage. No business logic yet — just a rock-solid pipe from the device into the database.

---

## 1.1 SDK Acquisition & Registration

### Tasks

1. Download the ZKTeco Standalone SDK from the official ZKTeco SDK page (Communication Protocol SDK, latest).
2. Place the SDK files in a `lib/zkteco/` folder inside the repo (so future devs can rebuild without hunting for installers):
   - `zkemkeeper.dll` (the native COM ActiveX)
   - `commpro.dll`, `comms.dll`, `plcommpro.dll` (dependencies)
   - `tcpcomm.dll`, `rscagent.dll`, `rscomm.dll`
3. Register `zkemkeeper.dll` against the OS so its CLSID can be resolved:
   ```powershell
   # 32-bit registration (always — the SDK is 32-bit ActiveX)
   & "$env:SystemRoot\SysWOW64\regsvr32.exe" "<repo>\lib\zkteco\zkemkeeper.dll"
   ```
4. Add a `Register.bat` next to the DLLs so re-installation on a new PC is a one-click step.
5. Generate the .NET interop assembly:
   - Visual Studio → Add Reference → COM → "ZKEMKeeper 1.0 Type Library" → produces `Interop.zkemkeeper.dll` automatically.
   - Or via CLI: `tlbimp.exe zkemkeeper.dll /out:Interop.zkemkeeper.dll`.
6. Reference `Interop.zkemkeeper.dll` from `DawloomAttendance.csproj`.
7. **Force `PlatformTarget` to `x86`** in both Debug and Release configurations — `zkemkeeper.dll` is 32-bit only, an AnyCPU/x64 process will fail to load it with "not a valid Win32 application".

### Acceptance criteria

- `regsvr32` reports success.
- `new CZKEMClass()` constructs without `COMException` on the dev machine.
- Build configuration shows `x86` for both Debug and Release.

---

## 1.2 Project Structure for the Connection Layer

Create a clean folder layout that future phases can extend:

```
DawloomAttendance/
├── Device/
│   ├── IZkDevice.cs              ← abstraction (so we can mock for tests)
│   ├── ZkDeviceClient.cs         ← wraps CZKEMClass
│   ├── ZkConnectionMonitor.cs    ← keep-alive + auto-reconnect loop
│   ├── DeviceSettings.cs         ← IP, port, comm password, machine number
│   └── Events/
│       ├── PunchEvent.cs         ← DTO for a single attendance transaction
│       └── DeviceConnectionState.cs  ← enum: Disconnected / Connecting / Connected / Error
├── Data/
│   ├── AppDbContext.cs           ← SQLite (recommended) or SQL Server LocalDB
│   ├── Entities/
│   │   ├── RawPunch.cs           ← every event captured, untouched
│   │   └── DeviceLog.cs          ← connect/disconnect/error audit
│   └── Migrations/
└── lib/zkteco/                   ← SDK DLLs + Register.bat
```

**Rationale:** isolating `Device/` behind an interface means Phases 3 and 4 can develop against a fake device while the real one is occupied or offline.

---

## 1.3 Configuration Surface

`DeviceSettings.cs` (loaded from `App.config` or `appsettings.json`):

| Setting | Default | Notes |
|---|---|---|
| `Ip` | `192.168.1.201` | Static IP of the K70 on the LAN |
| `Port` | `4370` | Default ZKTeco port |
| `CommKey` | `0` | Communication password set on the device |
| `MachineNumber` | `1` | Logical device ID, used in SDK calls |
| `ReconnectIntervalSeconds` | `10` | Backoff for auto-reconnect |
| `EventMask` | `65535` | Register all real-time events |

These must be editable from a Settings screen in the app (don't hardcode), so the customer can re-point at a different K70 without a rebuild.

---

## 1.4 Connect / Disconnect Flow

### 1.4.1 Connect

```csharp
public bool Connect(DeviceSettings s)
{
    _device = new CZKEMClass();
    _device.SetCommPassword(s.CommKey);
    bool ok = _device.Connect_Net(s.Ip, s.Port);
    if (!ok) { LogError("Connect_Net failed", _device); return false; }

    // Register for live events on this machine number, all events
    if (!_device.RegEvent(s.MachineNumber, s.EventMask))
    {
        LogError("RegEvent failed", _device);
        _device.Disconnect();
        return false;
    }

    WireEventHandlers();
    State = DeviceConnectionState.Connected;
    return true;
}
```

### 1.4.2 Disconnect

```csharp
public void Disconnect()
{
    if (_device == null) return;
    UnwireEventHandlers();
    _device.Disconnect();
    _device = null;
    State = DeviceConnectionState.Disconnected;
}
```

### 1.4.3 Live event subscription

Wire at minimum `OnAttTransactionEx` (every successful punch, with verification mode + timestamp). Optionally wire `OnVerify`, `OnFingerFeature`, `OnDoor`, `OnAlarm` for richer telemetry.

```csharp
_device.OnAttTransactionEx += (enrollNumber, isInValid, attState,
                               verifyMethod, year, month, day,
                               hour, minute, second, workCode) =>
{
    var punch = new PunchEvent
    {
        EnrollNumber = enrollNumber,
        IsValid      = isInValid == 0,
        AttState     = attState,        // 0=check-in, 1=check-out, etc.
        VerifyMethod = verifyMethod,    // 1=fingerprint, 3=card, ...
        Timestamp    = new DateTime(year, month, day, hour, minute, second),
        WorkCode     = workCode
    };
    _onPunch?.Invoke(punch);
};
```

The K70 fires this on the device's own thread — every handler must be quick and must marshal to the UI thread before touching XAML (`Application.Current.Dispatcher.InvokeAsync(...)`).

---

## 1.5 Auto-Reconnect Monitor

Network blips, K70 reboots, and switch power-cycles are normal. The connection layer must self-heal without user intervention.

`ZkConnectionMonitor` runs a background `Task` that:

1. On startup, attempts to connect.
2. If `Connect_Net` returns `false` or throws, transitions state to `Error`, waits `ReconnectIntervalSeconds`, retries.
3. While connected, pings the device every 30 s via `_device.GetSerialNumber(...)` (cheapest no-op call). If the call fails or times out, transitions to `Disconnected` and re-enters the retry loop.
4. Exposes `DeviceConnectionState` as an `INotifyPropertyChanged` property so the UI status indicator updates instantly.
5. Logs every state change to `DeviceLog` table for diagnostics.

**Caveat:** when reconnecting, you must call `RegEvent` again — events do not survive a reconnect.

---

## 1.6 Local Persistence (Raw Punches)

Phase 1 only persists **raw** events. Calculation is Phase 3's job. Schema:

```sql
CREATE TABLE RawPunch (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    EnrollNumber    TEXT NOT NULL,
    Timestamp       DATETIME NOT NULL,
    AttState        INTEGER NOT NULL,
    VerifyMethod    INTEGER NOT NULL,
    WorkCode        INTEGER NOT NULL,
    IsValid         INTEGER NOT NULL,
    CapturedAt      DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    Source          TEXT NOT NULL          -- 'live' or 'backfill'
);
CREATE INDEX IX_RawPunch_EnrollTimestamp ON RawPunch(EnrollNumber, Timestamp);

CREATE TABLE DeviceLog (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Timestamp   DATETIME NOT NULL,
    Level       TEXT NOT NULL,    -- Info / Warn / Error
    Event       TEXT NOT NULL,    -- Connected / Disconnected / RegEventFailed / ...
    Detail      TEXT
);
```

Recommendation: **SQLite via `Microsoft.Data.Sqlite`** — file-based, zero install, fits on a single office PC. SQL Server LocalDB is overkill for this load.

---

## 1.7 Backfill of Missed Punches

If the desktop app was off when someone punched in, those events live on the device but never hit the DB. On every successful (re)connect:

1. Call `_device.ReadGeneralLogData(machineNumber)`.
2. Iterate with `SSR_GetGeneralLogData(...)` until it returns false.
3. Insert any rows whose `(EnrollNumber, Timestamp)` is not already in `RawPunch`, with `Source = 'backfill'`.
4. Optionally call `ClearGLog` only after the customer signs off — never automatically, or we lose the audit trail on the device itself.

---

## 1.8 Minimal UI for Phase 1

Phase 1 doesn't ship the full dashboard, but it ships enough UI to prove the pipe works:

- **Status bar:** colored dot — green (Connected) / amber (Connecting / Reconnecting) / red (Disconnected / Error) — bound to `DeviceConnectionState`.
- **Connect / Disconnect buttons.**
- **Settings dialog** to edit IP, port, comm password, machine number; persists to config.
- **Live punch feed:** scrolling list of the last 20 raw events as they arrive, format `HH:mm:ss  ENROLL#1042  IN  fingerprint`.
- **Backfill button** with last-backfill timestamp.

That's it for Phase 1's UI surface — anything more belongs to Phase 4.

---

## 1.9 Logging & Diagnostics

- Use `Serilog` (or `NLog`) with two sinks: rolling file (`logs/dawloom-YYYYMMDD.log`) and the SQLite `DeviceLog` table.
- Log every: `Connect_Net` attempt + result, `RegEvent` result, every `OnAttTransactionEx` (DEBUG level), state transitions, exceptions.
- Include the SDK error code from `_device.GetLastError(ref code)` on every failure — these codes are how the customer's IT will diagnose firewall vs. wrong-password vs. wrong-IP issues.

---

## 1.10 Testing Strategy for Phase 1

Three layers, in order of value:

1. **Manual against a real K70** — there is no substitute. Test plan:
   - Cold start: app launches, connects, status goes green within 5 s.
   - Punch with a registered finger → event appears in live feed within 1 s, row appears in `RawPunch`.
   - Unplug K70 ethernet → status goes red within 30 s; plug back in → reconnects within `ReconnectIntervalSeconds` × 2; missed punches backfilled.
   - Restart app while user is mid-punch → no events lost (they're on the device until backfill).
   - Wrong comm password → meaningful error message, not a crash.
2. **Fake device** — `IZkDevice` implementation that fires synthetic events on a timer. Lets Phases 3+ proceed when the K70 isn't available.
3. **Unit tests** for `RawPunch` deduplication logic in backfill.

---

## 1.11 Phase 1 Deliverables

- ✅ x86 WPF build that successfully connects to a K70 over LAN.
- ✅ Live capture of every check-in / check-out into `RawPunch`.
- ✅ Auto-reconnect within 30 s of any network blip.
- ✅ Backfill of missed events on (re)connect.
- ✅ Visible connection status + live event feed in the UI.
- ✅ Persistent logs in `DeviceLog` and on disk.
- ✅ Settings screen for IP / port / comm password / machine number.
- ✅ `Register.bat` and SDK files committed to `lib/zkteco/`.

---

## 1.12 Phase 1 Risks & Mitigations

| Risk | Likelihood | Mitigation |
|---|---|---|
| `zkemkeeper.dll` registration fails on customer PC (UAC, antivirus) | Medium | Provide a documented Admin install step; check return code of `regsvr32`; fall back to manifest-based registration-free COM if needed. |
| Bitness mismatch — accidental AnyCPU/x64 build | High | Lock `PlatformTarget` to x86 in `.csproj`; CI check that the output exe is 32-bit. |
| K70 has wrong subnet / firewall blocks 4370 | High | Settings dialog includes a "Test Connection" button that returns SDK error code; documented troubleshooting in admin guide. |
| Device clock drift vs. server clock | Medium | On every connect, call `SetDeviceTime` to sync the K70 to the PC clock (configurable; off by default). |
| Lost events when both device and PC are offline simultaneously | Low | The K70 stores 90k transactions locally — backfill recovers them on reconnect. |

---

# Phase 2 — Employee Management & Shifts

- CRUD for employees (name, CNIC, photo, department, designation, contact).
- Map each employee's `EnrollNumber` (from device) to their HR profile.
- Shift template entity: name, start time, end time, grace period, weekend days.
- Assign shift to employee.
- Holiday calendar entity: date, label, recurring (yes/no).
- Bulk import employees from Excel (using ClosedXML).
- Two-way sync with the K70: pushing a new employee from the app enrolls them on the device; deleting in the app removes from device.

**Depends on:** Phase 1 connection layer for device-side employee operations.

---

# Phase 3 — Attendance Calculation Engine

- Pure-function service: `(RawPunches, Shift, Holidays, Leaves) → DailyAttendance`.
- First-punch / last-punch detection per employee per day.
- Late / early / on-time classification using shift start + grace period.
- Working hours, overtime (hours past shift end), half-day vs full-day vs absent.
- Weekend handling (configurable 5-day vs 6-day work week).
- Recalculation job that runs nightly + on-demand.
- All calculation logic unit-tested with synthetic punch sets — no device required.

---

# Phase 4 — Live Dashboard

- Today summary tiles: Total / Present / Absent / Late / On Leave / In Office.
- Recent activity feed (last 10 punches, live).
- Department-wise breakdown.
- Quick search by name / enrollment number / CNIC.
- Late list and absent list.
- Wired to Phase 1's punch event stream — UI updates instantly via WPF data binding.

---

# Phase 5 — Reports, Exports, Leaves, Notifications

- Daily attendance report (printable, one page per day).
- Monthly summary per employee.
- Late arrivals report, absentee report — date-range filterable.
- Excel export (ClosedXML) and PDF export (iText7).
- Leave management: annual / sick / casual / unpaid, balances, history.
- Email notifications: late-arrival alerts, end-of-day summary (SMTP, configurable).
- Optional WhatsApp via third-party API (add-on).

---

# Phase 6 — Roles, Payroll Export, Audit, Backup

- Role-based access control: Admin / HR / Manager.
- Audit log: every change recorded with user + timestamp.
- Payroll-ready Excel export: late deductions, overtime hours, configurable formats.
- Automated daily SQLite backup to a configurable local or network path.
- Restore-from-backup utility.

---

# Phase 7 — Testing, Deployment, Training, Handover

- End-to-end test pass against a real K70 in the customer's office.
- Single-file `.exe` installer (e.g., via Velopack or Inno Setup).
- User Manual (PDF) — daily operations.
- Administrator Guide (PDF) — install, backup, troubleshooting.
- One 2-hour remote training session.
- Source code handover with database schema documentation.
- 30 days post-deployment bug-fix support window starts.

---

## Sources (technical research)

- [ZKTeco Standalone SDK (official GitHub)](https://github.com/ZKTeco/Standalone-SDK)
- [ZKTeco SDK download page](https://www.zkteco.com/en/SDK)
- [C# ZKTeco Biometric Device Getting Started — CodeProject](https://www.codeproject.com/Articles/1104538/Csharp-ZKTeco-Biometric-Device-Getting-Started)
- [ZKTeco-SDK-Test (TCP/IP connection example, GitHub)](https://github.com/RedenLamosa/ZKTeco-SDK-Test)
- [Integrating a ZKTeco Biometric Device with ASP.NET (Medium)](https://medium.com/@jha.aaryan/integrating-a-zkteco-biometric-device-with-asp-net-884d0f4fa141)
- [OnAttTransactionEx event reference (ZKTeco LATAM)](https://desarrollo.zktecolatinoamerica.com/sdk/on_att_transaction_ex)
- [Protocol Description of ZKTeco's Standalone Devices](https://github.com/adrobinoga/zk-protocol/blob/master/protocol.md)
- [ZKTeco K70 product page (Pakistan)](https://zkteco.net.pk/detail/392/k70.html)
- [ZKEMKeeper SDK Event Functions Guide (Scribd)](https://www.scribd.com/doc/299899749/Sdk-Manual-for-zk-devices-VB-C)
- [zkemkeeper.dll registration / interop (Microsoft Q&A)](https://learn.microsoft.com/en-us/answers/questions/493247/zkemkeeper-assembly-adding)
