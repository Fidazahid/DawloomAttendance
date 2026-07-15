using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using DawloomAttendance.Data.Entities;

namespace DawloomAttendance.Data
{
    /// <summary>
    /// File-based SQLite store for Phase 1: raw punches and a device-event log.
    ///
    /// Threading: a fresh <see cref="SQLiteConnection"/> is opened per operation, so
    /// the device's COM thread can write a punch while the UI thread reads the feed
    /// without sharing a connection across threads. SQLite serializes the writes.
    /// </summary>
    public sealed partial class AppDb
    {
        // Stored as TEXT so equality-based dedup is exact and DB files are inspectable.
        private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

        private readonly string _connectionString;

        public string DbPath { get; }

        public AppDb(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath)) throw new ArgumentNullException(nameof(dbPath));
            DbPath = dbPath;
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _connectionString = new SQLiteConnectionStringBuilder { DataSource = dbPath }.ToString();
        }

        /// <summary>
        /// Default location: %LOCALAPPDATA%\DawloomAttendance\dawloom.db, overridable
        /// via the "Db.Path" appSettings key. LocalAppData avoids Program Files ACL issues.
        /// </summary>
        public static AppDb CreateDefault()
        {
            var configured = System.Configuration.ConfigurationManager.AppSettings["Db.Path"];
            string path = !string.IsNullOrWhiteSpace(configured)
                ? configured
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DawloomAttendance", "dawloom.db");

            var db = new AppDb(path);
            db.Initialize();
            return db;
        }

        /// <summary>Creates tables and indexes if they do not already exist.</summary>
        public void Initialize()
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS RawPunch (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    EnrollNumber    TEXT NOT NULL,
    Timestamp       TEXT NOT NULL,
    AttState        INTEGER NOT NULL,
    VerifyMethod    INTEGER NOT NULL,
    WorkCode        INTEGER NOT NULL,
    IsValid         INTEGER NOT NULL,
    CapturedAt      TEXT NOT NULL,
    Source          TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_RawPunch_EnrollTimestamp ON RawPunch(EnrollNumber, Timestamp);

CREATE TABLE IF NOT EXISTS DeviceLog (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Timestamp   TEXT NOT NULL,
    Level       TEXT NOT NULL,
    Event       TEXT NOT NULL,
    Detail      TEXT
);

CREATE TABLE IF NOT EXISTS Employee (
    Id            INTEGER PRIMARY KEY AUTOINCREMENT,
    EnrollNumber  TEXT NOT NULL UNIQUE,
    Name          TEXT,
    Cnic          TEXT,
    Department    TEXT,
    Designation   TEXT,
    Contact       TEXT,
    Email         TEXT,
    ShiftId       INTEGER,
    Active        INTEGER NOT NULL DEFAULT 1,
    CreatedAt     TEXT NOT NULL,
    Salary        REAL NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS Shift (
    Id            INTEGER PRIMARY KEY AUTOINCREMENT,
    Name          TEXT NOT NULL,
    StartTime     TEXT NOT NULL,
    EndTime       TEXT NOT NULL,
    GraceMinutes  INTEGER NOT NULL DEFAULT 0,
    WeekendDays   TEXT,
    Active        INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS Holiday (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    Date         TEXT NOT NULL,
    Label        TEXT,
    Recurring    INTEGER NOT NULL DEFAULT 0,
    EnrollNumber TEXT
);

CREATE TABLE IF NOT EXISTS Setting (
    Key   TEXT PRIMARY KEY,
    Value TEXT
);

CREATE TABLE IF NOT EXISTS WeeklyOff (
    Month     INTEGER NOT NULL,   -- 0 = whole year, 1-12 = specific month
    DayOfWeek INTEGER NOT NULL,   -- 0=Sun … 6=Sat
    PRIMARY KEY (Month, DayOfWeek)
);

-- Device-original punches the user deleted or edited, so on-connect backfill must
-- NOT re-import them from the device (which still holds the original record).
CREATE TABLE IF NOT EXISTS SuppressedPunch (
    EnrollNumber  TEXT NOT NULL,
    Timestamp     TEXT NOT NULL,
    Reason        TEXT,
    SuppressedAt  TEXT NOT NULL,
    PRIMARY KEY (EnrollNumber, Timestamp)
);

-- Leave categories (Annual/Sick/Casual/Unpaid). Paid decides whether a day on
-- this leave still counts toward pay; DefaultDays is the yearly entitlement.
CREATE TABLE IF NOT EXISTS LeaveType (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Code        TEXT NOT NULL UNIQUE,
    Name        TEXT NOT NULL,
    Paid        INTEGER NOT NULL DEFAULT 1,
    DefaultDays REAL NOT NULL DEFAULT 0,
    Active      INTEGER NOT NULL DEFAULT 1
);

-- Per-employee yearly entitlement override; absent rows fall back to LeaveType.DefaultDays.
CREATE TABLE IF NOT EXISTS LeaveEntitlement (
    EnrollNumber TEXT NOT NULL,
    LeaveTypeId  INTEGER NOT NULL,
    Year         INTEGER NOT NULL,
    Days         REAL NOT NULL DEFAULT 0,
    PRIMARY KEY (EnrollNumber, LeaveTypeId, Year)
);

-- One row per leave day taken (a multi-day request expands to one row per day).
CREATE TABLE IF NOT EXISTS LeaveEntry (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    EnrollNumber TEXT NOT NULL,
    LeaveTypeId  INTEGER NOT NULL,
    Date         TEXT NOT NULL,
    Reason       TEXT,
    CreatedAt    TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_LeaveEntry_EnrollDate ON LeaveEntry(EnrollNumber, Date);

-- Audit trail of user-initiated data changes: who did what, when.
CREATE TABLE IF NOT EXISTS AuditLog (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    Timestamp TEXT NOT NULL,
    Actor     TEXT NOT NULL,
    Action    TEXT NOT NULL,
    Entity    TEXT,
    EntityId  TEXT,
    Detail    TEXT
);
CREATE INDEX IF NOT EXISTS IX_AuditLog_Timestamp ON AuditLog(Timestamp);

-- Tracks which report emails were sent, so weekly/monthly auto-send never double-sends
-- and can tell on launch whether the previous period already went out.
CREATE TABLE IF NOT EXISTS EmailLog (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    EnrollNumber TEXT NOT NULL,
    Kind         TEXT NOT NULL,   -- 'weekly' / 'monthly' / 'manual'
    PeriodKey    TEXT NOT NULL,   -- e.g. 'W2026-06-06' (Sat-start week) or 'M2026-06'
    SentAt       TEXT NOT NULL,
    Status       TEXT NOT NULL,   -- 'sent' / 'failed'
    Detail       TEXT
);
CREATE INDEX IF NOT EXISTS IX_EmailLog_Lookup ON EmailLog(EnrollNumber, Kind, PeriodKey);";
                cmd.ExecuteNonQuery();

                MigrateWeeklyOff(conn);
                MigrateHolidayEmployee(conn);
                MigrateColumn(conn, "Employee", "Salary", "REAL NOT NULL DEFAULT 0");
                MigrateColumn(conn, "Employee", "Email", "TEXT");
                SeedLeaveTypes(conn);
                InitializeLoansAndSalary(conn);
            }
        }

        /// <summary>
        /// Inserts a punch unless one with the same (EnrollNumber, Timestamp) already
        /// exists. Returns true if a new row was written, false if it was a duplicate.
        /// Dedup matters because backfill (a later step) replays device-stored punches
        /// that may overlap live ones.
        /// </summary>
        public bool InsertPunchIfNew(RawPunch p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));

            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO RawPunch (EnrollNumber, Timestamp, AttState, VerifyMethod, WorkCode, IsValid, CapturedAt, Source)
SELECT $enroll, $ts, $att, $verify, $work, $valid, $captured, $source
WHERE NOT EXISTS (
    SELECT 1 FROM RawPunch WHERE EnrollNumber = $enroll AND Timestamp = $ts
);";
                cmd.Parameters.AddWithValue("$enroll", p.EnrollNumber ?? string.Empty);
                cmd.Parameters.AddWithValue("$ts", p.Timestamp.ToString(TimeFormat));
                cmd.Parameters.AddWithValue("$att", p.AttState);
                cmd.Parameters.AddWithValue("$verify", p.VerifyMethod);
                cmd.Parameters.AddWithValue("$work", p.WorkCode);
                cmd.Parameters.AddWithValue("$valid", p.IsValid ? 1 : 0);
                cmd.Parameters.AddWithValue("$captured",
                    (p.CapturedAt == default ? DateTime.Now : p.CapturedAt).ToString(TimeFormat));
                cmd.Parameters.AddWithValue("$source", p.Source ?? "live");

                return cmd.ExecuteNonQuery() > 0;
            }
        }

        public void InsertDeviceLog(string level, string @event, string detail = null)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO DeviceLog (Timestamp, Level, Event, Detail)
VALUES ($ts, $level, $event, $detail);";
                cmd.Parameters.AddWithValue("$ts", DateTime.Now.ToString(TimeFormat));
                cmd.Parameters.AddWithValue("$level", level ?? "Info");
                cmd.Parameters.AddWithValue("$event", @event ?? string.Empty);
                cmd.Parameters.AddWithValue("$detail", (object)detail ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Most recent punches, newest first — used to preload the live feed.</summary>
        public IReadOnlyList<RawPunch> GetRecentPunches(int limit)
        {
            var list = new List<RawPunch>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, EnrollNumber, Timestamp, AttState, VerifyMethod, WorkCode, IsValid, CapturedAt, Source
FROM RawPunch ORDER BY Id DESC LIMIT $limit;";
                cmd.Parameters.AddWithValue("$limit", limit);

                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new RawPunch
                        {
                            Id = r.GetInt64(0),
                            EnrollNumber = r.GetString(1),
                            Timestamp = ParseTime(r.GetString(2)),
                            AttState = r.GetInt32(3),
                            VerifyMethod = r.GetInt32(4),
                            WorkCode = r.GetInt32(5),
                            IsValid = r.GetInt32(6) != 0,
                            CapturedAt = ParseTime(r.GetString(7)),
                            Source = r.GetString(8)
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>
        /// Writes a consistent copy of the database to destPath using SQLite's
        /// VACUUM INTO (safe even while the app is running; also compacts).
        /// </summary>
        public void BackupTo(string destPath)
        {
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(destPath)) File.Delete(destPath);   // VACUUM INTO requires the target not to exist

            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "VACUUM INTO '" + destPath.Replace("'", "''") + "';";
                cmd.ExecuteNonQuery();
            }
        }

        public long CountPunches()
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM RawPunch;";
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
        }

        // ---- Employees ---------------------------------------------------------------

        public IReadOnlyList<Employee> GetEmployees()
        {
            var list = new List<Employee>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, EnrollNumber, Name, Cnic, Department, Designation, Contact, ShiftId, Active, CreatedAt, Salary, Email
FROM Employee ORDER BY CAST(EnrollNumber AS INTEGER), EnrollNumber;";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new Employee
                        {
                            Id = r.GetInt64(0),
                            EnrollNumber = r.GetString(1),
                            Name = r.IsDBNull(2) ? null : r.GetString(2),
                            Cnic = r.IsDBNull(3) ? null : r.GetString(3),
                            Department = r.IsDBNull(4) ? null : r.GetString(4),
                            Designation = r.IsDBNull(5) ? null : r.GetString(5),
                            Contact = r.IsDBNull(6) ? null : r.GetString(6),
                            ShiftId = r.IsDBNull(7) ? (long?)null : r.GetInt64(7),
                            Active = r.GetInt32(8) != 0,
                            CreatedAt = ParseTime(r.GetString(9)),
                            Salary = r.IsDBNull(10) ? 0 : r.GetDouble(10),
                            Email = r.IsDBNull(11) ? null : r.GetString(11)
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>Inserts a placeholder employee for a device enroll number if absent. Returns true if inserted.</summary>
        public bool InsertEmployeeIfNew(string enrollNumber, string name)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO Employee (EnrollNumber, Name, Active, CreatedAt)
SELECT $enroll, $name, 1, $created
WHERE NOT EXISTS (SELECT 1 FROM Employee WHERE EnrollNumber = $enroll);";
                cmd.Parameters.AddWithValue("$enroll", enrollNumber ?? string.Empty);
                cmd.Parameters.AddWithValue("$name", (object)name ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$created", DateTime.Now.ToString(TimeFormat));
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        public long InsertEmployee(Employee e)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO Employee (EnrollNumber, Name, Cnic, Department, Designation, Contact, Email, ShiftId, Active, CreatedAt, Salary)
VALUES ($enroll, $name, $cnic, $dept, $desig, $contact, $email, $shift, $active, $created, $salary);";
                BindEmployee(cmd, e);
                cmd.Parameters.AddWithValue("$created", (e.CreatedAt == default ? DateTime.Now : e.CreatedAt).ToString(TimeFormat));
                cmd.ExecuteNonQuery();
                long newId = conn.LastInsertRowId;
                Audit(conn, "Employee added", "Employee", newId.ToString(), $"{e.EnrollNumber} {e.Name}".Trim());
                return newId;
            }
        }

        public void UpdateEmployee(Employee e)
        {
            using (var conn = Open())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
UPDATE Employee SET EnrollNumber=$enroll, Name=$name, Cnic=$cnic, Department=$dept,
    Designation=$desig, Contact=$contact, Email=$email, ShiftId=$shift, Active=$active, Salary=$salary
WHERE Id=$id;";
                    BindEmployee(cmd, e);
                    cmd.Parameters.AddWithValue("$id", e.Id);
                    cmd.ExecuteNonQuery();
                }
                Audit(conn, "Employee updated", "Employee", e.Id.ToString(), $"{e.EnrollNumber} {e.Name}".Trim());
            }
        }

        public void DeleteEmployee(long id)
        {
            using (var conn = Open())
            {
                string label = null;
                using (var read = conn.CreateCommand())
                {
                    read.CommandText = "SELECT EnrollNumber, Name FROM Employee WHERE Id=$id;";
                    read.Parameters.AddWithValue("$id", id);
                    using (var r = read.ExecuteReader())
                        if (r.Read()) label = $"{r.GetValue(0)} {(r.IsDBNull(1) ? "" : r.GetString(1))}".Trim();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM Employee WHERE Id=$id;";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
                Audit(conn, "Employee deleted", "Employee", id.ToString(), label);
            }
        }

        // ---- Shifts ------------------------------------------------------------------

        public IReadOnlyList<Shift> GetShifts()
        {
            var list = new List<Shift>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, Name, StartTime, EndTime, GraceMinutes, WeekendDays, Active FROM Shift ORDER BY Name;";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new Shift
                        {
                            Id = r.GetInt64(0),
                            Name = r.IsDBNull(1) ? null : r.GetString(1),
                            StartTime = r.IsDBNull(2) ? null : r.GetString(2),
                            EndTime = r.IsDBNull(3) ? null : r.GetString(3),
                            GraceMinutes = r.GetInt32(4),
                            WeekendDays = r.IsDBNull(5) ? null : r.GetString(5),
                            Active = r.GetInt32(6) != 0
                        });
                    }
                }
            }
            return list;
        }

        public long InsertShift(Shift s)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO Shift (Name, StartTime, EndTime, GraceMinutes, WeekendDays, Active)
VALUES ($name, $start, $end, $grace, $weekend, $active);";
                BindShift(cmd, s);
                cmd.ExecuteNonQuery();
                return conn.LastInsertRowId;
            }
        }

        public void UpdateShift(Shift s)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
UPDATE Shift SET Name=$name, StartTime=$start, EndTime=$end, GraceMinutes=$grace,
    WeekendDays=$weekend, Active=$active WHERE Id=$id;";
                BindShift(cmd, s);
                cmd.Parameters.AddWithValue("$id", s.Id);
                cmd.ExecuteNonQuery();
            }
        }

        public void DeleteShift(long id)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                // Unassign the shift from any employees, then remove it.
                cmd.CommandText = "UPDATE Employee SET ShiftId=NULL WHERE ShiftId=$id; DELETE FROM Shift WHERE Id=$id;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }

        // ---- Holidays ----------------------------------------------------------------

        private const string DateFormat = "yyyy-MM-dd";

        public IReadOnlyList<Holiday> GetHolidays()
        {
            var list = new List<Holiday>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT Id, Date, Label, Recurring, EnrollNumber FROM Holiday ORDER BY Date;";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new Holiday
                        {
                            Id = r.GetInt64(0),
                            Date = ParseTime(r.GetString(1)),
                            Label = r.IsDBNull(2) ? null : r.GetString(2),
                            Recurring = r.GetInt32(3) != 0,
                            EnrollNumber = r.IsDBNull(4) ? null : r.GetString(4)
                        });
                    }
                }
            }
            return list;
        }

        public long InsertHoliday(Holiday h)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO Holiday (Date, Label, Recurring, EnrollNumber) VALUES ($date, $label, $rec, $enroll);";
                BindHoliday(cmd, h);
                cmd.ExecuteNonQuery();
                return conn.LastInsertRowId;
            }
        }

        public void UpdateHoliday(Holiday h)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE Holiday SET Date=$date, Label=$label, Recurring=$rec, EnrollNumber=$enroll WHERE Id=$id;";
                BindHoliday(cmd, h);
                cmd.Parameters.AddWithValue("$id", h.Id);
                cmd.ExecuteNonQuery();
            }
        }

        public void DeleteHoliday(long id)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM Holiday WHERE Id=$id;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }

        // ---- Leave types / entitlements / entries ------------------------------------

        /// <summary>Inserts the four standard leave types once; existing rows are left untouched.</summary>
        private void SeedLeaveTypes(SQLiteConnection conn)
        {
            // (Code, Name, Paid, DefaultDays) — sensible Pakistan-labour defaults; all editable later.
            var defaults = new[]
            {
                ("annual", "Annual",  1, 14.0),
                ("sick",   "Sick",    1,  8.0),
                ("casual", "Casual",  1, 10.0),
                ("unpaid", "Unpaid",  0,  0.0),
            };
            foreach (var (code, name, paid, days) in defaults)
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO LeaveType (Code, Name, Paid, DefaultDays, Active)
VALUES ($code, $name, $paid, $days, 1);";
                    cmd.Parameters.AddWithValue("$code", code);
                    cmd.Parameters.AddWithValue("$name", name);
                    cmd.Parameters.AddWithValue("$paid", paid);
                    cmd.Parameters.AddWithValue("$days", days);
                    cmd.ExecuteNonQuery();
                }
        }

        public IReadOnlyList<LeaveType> GetLeaveTypes(bool activeOnly = false)
        {
            var list = new List<LeaveType>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT Id, Code, Name, Paid, DefaultDays, Active FROM LeaveType"
                    + (activeOnly ? " WHERE Active = 1" : "") + " ORDER BY Id;";
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        list.Add(new LeaveType
                        {
                            Id = r.GetInt64(0),
                            Code = r.GetString(1),
                            Name = r.GetString(2),
                            Paid = r.GetInt32(3) != 0,
                            DefaultDays = r.GetDouble(4),
                            Active = r.GetInt32(5) != 0
                        });
            }
            return list;
        }

        public long InsertLeaveType(LeaveType t)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO LeaveType (Code, Name, Paid, DefaultDays, Active)
VALUES ($code, $name, $paid, $days, $active);";
                BindLeaveType(cmd, t);
                cmd.ExecuteNonQuery();
                return conn.LastInsertRowId;
            }
        }

        public void UpdateLeaveType(LeaveType t)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
UPDATE LeaveType SET Code=$code, Name=$name, Paid=$paid, DefaultDays=$days, Active=$active WHERE Id=$id;";
                BindLeaveType(cmd, t);
                cmd.Parameters.AddWithValue("$id", t.Id);
                cmd.ExecuteNonQuery();
            }
        }

        private static void BindLeaveType(SQLiteCommand cmd, LeaveType t)
        {
            cmd.Parameters.AddWithValue("$code", (t.Code ?? string.Empty).Trim().ToLowerInvariant());
            cmd.Parameters.AddWithValue("$name", (object)t.Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$paid", t.Paid ? 1 : 0);
            cmd.Parameters.AddWithValue("$days", t.DefaultDays);
            cmd.Parameters.AddWithValue("$active", t.Active ? 1 : 0);
        }

        /// <summary>Sets (or clears) an employee's yearly entitlement override for a leave type.</summary>
        public void SetEntitlement(string enrollNumber, long leaveTypeId, int year, double days)
        {
            using (var conn = Open())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT INTO LeaveEntitlement (EnrollNumber, LeaveTypeId, Year, Days)
VALUES ($e, $t, $y, $d)
ON CONFLICT(EnrollNumber, LeaveTypeId, Year) DO UPDATE SET Days = $d;";
                    cmd.Parameters.AddWithValue("$e", enrollNumber ?? string.Empty);
                    cmd.Parameters.AddWithValue("$t", leaveTypeId);
                    cmd.Parameters.AddWithValue("$y", year);
                    cmd.Parameters.AddWithValue("$d", days);
                    cmd.ExecuteNonQuery();
                }
                Audit(conn, "Leave entitlement set", "LeaveEntitlement", null,
                    $"enroll {enrollNumber} type {leaveTypeId} {year} = {days:0.##}");
            }
        }

        /// <summary>
        /// Computed leave balances for one employee in a year: entitlement (per-employee
        /// override or the type default) minus days actually taken, per active leave type.
        /// </summary>
        public IReadOnlyList<LeaveBalance> GetLeaveBalances(string enrollNumber, int year)
        {
            var list = new List<LeaveBalance>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT t.Id, t.Code, t.Name, t.Paid,
       COALESCE(en.Days, t.DefaultDays) AS Entitled,
       (SELECT COUNT(*) FROM LeaveEntry le
          WHERE le.EnrollNumber = $e AND le.LeaveTypeId = t.Id AND substr(le.Date, 1, 4) = $yr) AS Taken
FROM LeaveType t
LEFT JOIN LeaveEntitlement en
       ON en.LeaveTypeId = t.Id AND en.EnrollNumber = $e AND en.Year = $y
WHERE t.Active = 1
ORDER BY t.Id;";
                cmd.Parameters.AddWithValue("$e", enrollNumber ?? string.Empty);
                cmd.Parameters.AddWithValue("$y", year);
                cmd.Parameters.AddWithValue("$yr", year.ToString());
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        list.Add(new LeaveBalance
                        {
                            LeaveTypeId = r.GetInt64(0),
                            TypeCode = r.GetString(1),
                            TypeName = r.GetString(2),
                            Paid = r.GetInt32(3) != 0,
                            Year = year,
                            Entitled = r.GetDouble(4),
                            Taken = Convert.ToDouble(r.GetValue(5))
                        });
            }
            return list;
        }

        public IReadOnlyList<LeaveEntry> GetLeaveEntries(string enrollNumber, int year)
        {
            var list = new List<LeaveEntry>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, EnrollNumber, LeaveTypeId, Date, Reason, CreatedAt
FROM LeaveEntry WHERE EnrollNumber = $e AND substr(Date, 1, 4) = $yr ORDER BY Date;";
                cmd.Parameters.AddWithValue("$e", enrollNumber ?? string.Empty);
                cmd.Parameters.AddWithValue("$yr", year.ToString());
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        list.Add(new LeaveEntry
                        {
                            Id = r.GetInt64(0),
                            EnrollNumber = r.GetString(1),
                            LeaveTypeId = r.GetInt64(2),
                            Date = ParseTime(r.GetString(3)),
                            Reason = r.IsDBNull(4) ? null : r.GetString(4),
                            CreatedAt = ParseTime(r.GetString(5))
                        });
            }
            return list;
        }

        /// <summary>Records a single leave day; ignored (returns false) if that day already has one.</summary>
        public bool InsertLeaveEntry(LeaveEntry e)
        {
            using (var conn = Open())
            {
                bool inserted;
                using (var cmd = conn.CreateCommand())
                {
                    // One leave day per (employee, date): a second add for the same day is a no-op.
                    cmd.CommandText = @"
INSERT INTO LeaveEntry (EnrollNumber, LeaveTypeId, Date, Reason, CreatedAt)
SELECT $e, $t, $d, $reason, $at
WHERE NOT EXISTS (SELECT 1 FROM LeaveEntry WHERE EnrollNumber = $e AND Date = $d);";
                    cmd.Parameters.AddWithValue("$e", e.EnrollNumber ?? string.Empty);
                    cmd.Parameters.AddWithValue("$t", e.LeaveTypeId);
                    cmd.Parameters.AddWithValue("$d", e.Date.ToString(DateFormat));
                    cmd.Parameters.AddWithValue("$reason", (object)e.Reason ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString(TimeFormat));
                    inserted = cmd.ExecuteNonQuery() > 0;
                }
                if (inserted)
                    Audit(conn, "Leave added", "LeaveEntry", null,
                        $"enroll {e.EnrollNumber} {e.Date.ToString(DateFormat)} type {e.LeaveTypeId}");
                return inserted;
            }
        }

        public void DeleteLeaveEntry(long id)
        {
            using (var conn = Open())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM LeaveEntry WHERE Id=$id;";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
                Audit(conn, "Leave removed", "LeaveEntry", id.ToString(), null);
            }
        }

        /// <summary>Leave taken on a given date — each as (EnrollNumber, TypeName, Paid). Used by the calc engine.</summary>
        public List<(string Enroll, string TypeName, bool Paid)> GetLeaveOn(DateTime date)
        {
            var list = new List<(string, string, bool)>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT le.EnrollNumber, t.Name, t.Paid FROM LeaveEntry le
JOIN LeaveType t ON t.Id = le.LeaveTypeId
WHERE le.Date = $d;";
                cmd.Parameters.AddWithValue("$d", date.ToString(DateFormat));
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        list.Add((r.GetString(0), r.GetString(1), r.GetInt32(2) != 0));
            }
            return list;
        }

        /// <summary>
        /// True if the given date is a non-working day: a weekly off-day (e.g. Sun/Sat),
        /// an exact-dated holiday, or a recurring month/day holiday.
        /// </summary>
        public bool IsHoliday(DateTime date)
            => ResolveWeeklyOffDays(date).Contains((int)date.DayOfWeek) || IsDatedHoliday(date);

        /// <summary>True only for a dated/recurring holiday in the Holiday table (ignores weekly off-days).</summary>
        public bool IsDatedHoliday(DateTime date)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                // substr(Date,6,5) extracts "MM-dd" from a "yyyy-MM-dd" value.
                cmd.CommandText = @"
SELECT COUNT(*) FROM Holiday
WHERE (EnrollNumber IS NULL OR EnrollNumber = '')
  AND ((Recurring = 0 AND Date = $exact) OR (Recurring = 1 AND substr(Date, 6, 5) = $md));";
                cmd.Parameters.AddWithValue("$exact", date.ToString(DateFormat));
                cmd.Parameters.AddWithValue("$md", date.ToString("MM-dd"));
                return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
            }
        }

        /// <summary>Punches for one employee on one date (for review/correction).</summary>
        public IReadOnlyList<RawPunch> GetPunchesForEmployeeDate(string enrollNumber, DateTime date)
        {
            var list = new List<RawPunch>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, EnrollNumber, Timestamp, AttState, VerifyMethod, WorkCode, IsValid, CapturedAt, Source
FROM RawPunch WHERE EnrollNumber = $e AND substr(Timestamp, 1, 10) = $d ORDER BY Timestamp;";
                cmd.Parameters.AddWithValue("$e", enrollNumber ?? string.Empty);
                cmd.Parameters.AddWithValue("$d", date.ToString(DateFormat));
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new RawPunch
                        {
                            Id = r.GetInt64(0),
                            EnrollNumber = r.GetString(1),
                            Timestamp = ParseTime(r.GetString(2)),
                            AttState = r.GetInt32(3),
                            VerifyMethod = r.GetInt32(4),
                            WorkCode = r.GetInt32(5),
                            IsValid = r.GetInt32(6) != 0,
                            CapturedAt = ParseTime(r.GetString(7)),
                            Source = r.GetString(8)
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>Deletes a single raw punch (e.g. an accidental duplicate scan).</summary>
        // ---- Audit trail -------------------------------------------------------------

        /// <summary>
        /// The user attributed to audited changes. Defaults to the Windows account; set
        /// this on login once role-based access exists so the trail names the app user.
        /// </summary>
        public static string CurrentActor { get; set; } = Environment.UserName;

        /// <summary>Records an audit entry on an existing connection (so it shares the caller's work).</summary>
        private void Audit(SQLiteConnection conn, string action, string entity, string entityId, string detail)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO AuditLog (Timestamp, Actor, Action, Entity, EntityId, Detail)
VALUES ($ts, $actor, $action, $entity, $id, $detail);";
                cmd.Parameters.AddWithValue("$ts", DateTime.Now.ToString(TimeFormat));
                cmd.Parameters.AddWithValue("$actor", CurrentActor ?? string.Empty);
                cmd.Parameters.AddWithValue("$action", action ?? string.Empty);
                cmd.Parameters.AddWithValue("$entity", (object)entity ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$id", (object)entityId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$detail", (object)detail ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Records an audit entry on its own connection (for UI-level events like login).</summary>
        public void RecordAudit(string action, string entity = null, string entityId = null, string detail = null)
        {
            using (var conn = Open())
                Audit(conn, action, entity, entityId, detail);
        }

        /// <summary>Audit entries within [from, to] (inclusive dates), newest first, optionally one actor.</summary>
        public IReadOnlyList<AuditEntry> GetAuditLog(DateTime from, DateTime to, string actor = null)
        {
            var list = new List<AuditEntry>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, Timestamp, Actor, Action, Entity, EntityId, Detail FROM AuditLog
WHERE Timestamp >= $from AND Timestamp < $to
  AND ($actor IS NULL OR Actor = $actor)
ORDER BY Timestamp DESC, Id DESC;";
                cmd.Parameters.AddWithValue("$from", from.Date.ToString(TimeFormat));
                cmd.Parameters.AddWithValue("$to", to.Date.AddDays(1).ToString(TimeFormat));
                cmd.Parameters.AddWithValue("$actor", string.IsNullOrEmpty(actor) ? (object)DBNull.Value : actor);
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        list.Add(new AuditEntry
                        {
                            Id = r.GetInt64(0),
                            Timestamp = ParseTime(r.GetString(1)),
                            Actor = r.GetString(2),
                            Action = r.GetString(3),
                            Entity = r.IsDBNull(4) ? null : r.GetString(4),
                            EntityId = r.IsDBNull(5) ? null : r.GetString(5),
                            Detail = r.IsDBNull(6) ? null : r.GetString(6)
                        });
            }
            return list;
        }

        /// <summary>Distinct actors that appear in the audit log (for the viewer's filter).</summary>
        public IReadOnlyList<string> GetAuditActors()
        {
            var list = new List<string>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT Actor FROM AuditLog ORDER BY Actor;";
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        if (!r.IsDBNull(0)) list.Add(r.GetString(0));
            }
            return list;
        }

        // ---- Sent-email log ----------------------------------------------------------

        /// <summary>True if a report of this kind for this period was already sent to the employee.</summary>
        public bool WasEmailSent(string enrollNumber, string kind, string periodKey)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT 1 FROM EmailLog
WHERE EnrollNumber=$e AND Kind=$k AND PeriodKey=$p AND Status='sent' LIMIT 1;";
                cmd.Parameters.AddWithValue("$e", enrollNumber ?? string.Empty);
                cmd.Parameters.AddWithValue("$k", kind ?? string.Empty);
                cmd.Parameters.AddWithValue("$p", periodKey ?? string.Empty);
                return cmd.ExecuteScalar() != null;
            }
        }

        /// <summary>Logs the outcome of a report-email attempt (sent or failed).</summary>
        public void RecordEmailSent(string enrollNumber, string kind, string periodKey, bool ok, string detail)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO EmailLog (EnrollNumber, Kind, PeriodKey, SentAt, Status, Detail)
VALUES ($e, $k, $p, $at, $status, $detail);";
                cmd.Parameters.AddWithValue("$e", enrollNumber ?? string.Empty);
                cmd.Parameters.AddWithValue("$k", kind ?? string.Empty);
                cmd.Parameters.AddWithValue("$p", periodKey ?? string.Empty);
                cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString(TimeFormat));
                cmd.Parameters.AddWithValue("$status", ok ? "sent" : "failed");
                cmd.Parameters.AddWithValue("$detail", (object)detail ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        public void DeletePunch(long id)
        {
            using (var conn = Open())
            {
                // Tombstone the device-original (enroll, timestamp) first so the next
                // on-connect backfill doesn't silently re-import the punch we're deleting.
                string enroll = null, ts = null;
                using (var read = conn.CreateCommand())
                {
                    read.CommandText = "SELECT EnrollNumber, Timestamp FROM RawPunch WHERE Id=$id;";
                    read.Parameters.AddWithValue("$id", id);
                    using (var r = read.ExecuteReader())
                        if (r.Read()) { enroll = r.GetString(0); ts = r.GetString(1); }
                }
                if (ts != null) SuppressDevicePunch(conn, enroll, ts, "deleted");
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM RawPunch WHERE Id=$id;";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
                Audit(conn, "Punch deleted", "RawPunch", id.ToString(),
                    ts != null ? $"enroll {enroll} @ {ts}" : null);
            }
        }

        /// <summary>
        /// Records that a device-original punch (EnrollNumber + the timestamp string the
        /// device reported) must not be re-imported by backfill — used when the user
        /// deletes a punch or edits its time. Safe to call repeatedly.
        /// </summary>
        public void SuppressDevicePunch(string enrollNumber, DateTime timestamp, string reason)
        {
            using (var conn = Open())
                SuppressDevicePunch(conn, enrollNumber, timestamp.ToString(TimeFormat), reason);
        }

        private void SuppressDevicePunch(SQLiteConnection conn, string enrollNumber, string timestamp, string reason)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT OR IGNORE INTO SuppressedPunch (EnrollNumber, Timestamp, Reason, SuppressedAt)
VALUES ($enroll, $ts, $reason, $at);";
                cmd.Parameters.AddWithValue("$enroll", enrollNumber ?? string.Empty);
                cmd.Parameters.AddWithValue("$ts", timestamp);
                cmd.Parameters.AddWithValue("$reason", reason ?? string.Empty);
                cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString(TimeFormat));
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>True if this device-original (enroll, timestamp) was deleted/edited and must not be re-imported.</summary>
        public bool IsPunchSuppressed(string enrollNumber, DateTime timestamp)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT 1 FROM SuppressedPunch WHERE EnrollNumber=$enroll AND Timestamp=$ts LIMIT 1;";
                cmd.Parameters.AddWithValue("$enroll", enrollNumber ?? string.Empty);
                cmd.Parameters.AddWithValue("$ts", timestamp.ToString(TimeFormat));
                return cmd.ExecuteScalar() != null;
            }
        }

        /// <summary>Corrects a punch's in/out type (e.g. a stray second check-in → check-out).</summary>
        public void UpdatePunchState(long id, int attState)
        {
            using (var conn = Open())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE RawPunch SET AttState=$s WHERE Id=$id;";
                    cmd.Parameters.AddWithValue("$s", attState);
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
                Audit(conn, "Punch type changed", "RawPunch", id.ToString(), $"AttState → {attState}");
            }
        }

        /// <summary>Corrects a punch's timestamp (e.g. a wrong device clock or a manual fix).</summary>
        public void UpdatePunchTime(long id, DateTime timestamp)
        {
            using (var conn = Open())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE RawPunch SET Timestamp=$ts WHERE Id=$id;";
                    cmd.Parameters.AddWithValue("$ts", timestamp.ToString(TimeFormat));
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
                Audit(conn, "Punch time edited", "RawPunch", id.ToString(), $"→ {timestamp.ToString(TimeFormat)}");
            }
        }

        /// <summary>All punches whose timestamp falls on the given calendar date.</summary>
        public IReadOnlyList<RawPunch> GetPunchesForDate(DateTime date)
        {
            var list = new List<RawPunch>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, EnrollNumber, Timestamp, AttState, VerifyMethod, WorkCode, IsValid, CapturedAt, Source
FROM RawPunch WHERE substr(Timestamp, 1, 10) = $d ORDER BY EnrollNumber, Timestamp;";
                cmd.Parameters.AddWithValue("$d", date.ToString(DateFormat));
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new RawPunch
                        {
                            Id = r.GetInt64(0),
                            EnrollNumber = r.GetString(1),
                            Timestamp = ParseTime(r.GetString(2)),
                            AttState = r.GetInt32(3),
                            VerifyMethod = r.GetInt32(4),
                            WorkCode = r.GetInt32(5),
                            IsValid = r.GetInt32(6) != 0,
                            CapturedAt = ParseTime(r.GetString(7)),
                            Source = r.GetString(8)
                        });
                    }
                }
            }
            return list;
        }

        // ---- Settings (key/value) ----------------------------------------------------

        private const string WeeklyOffKey = "WeeklyOffDays";

        public string GetSetting(string key)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT Value FROM Setting WHERE Key=$k;";
                cmd.Parameters.AddWithValue("$k", key);
                return cmd.ExecuteScalar() as string;
            }
        }

        public void SetSetting(string key, string value)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO Setting (Key, Value) VALUES ($k, $v)
ON CONFLICT(Key) DO UPDATE SET Value=$v;";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$v", (object)value ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        // Weekly recurring days off (0=Sun … 6=Sat), scoped by month:
        // Month 0 = whole year; 1-12 = that month only. A month with its own rule
        // OVERRIDES the whole-year rule for that month.

        /// <summary>Off-days configured for a scope (0 = whole year, 1-12 = month).</summary>
        public HashSet<int> GetWeeklyOffDays(int month)
        {
            var set = new HashSet<int>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DayOfWeek FROM WeeklyOff WHERE Month=$m;";
                cmd.Parameters.AddWithValue("$m", month);
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) set.Add(r.GetInt32(0));
            }
            return set;
        }

        /// <summary>Whole-year off-days (convenience for the common case).</summary>
        public HashSet<int> GetWeeklyOffDays() => GetWeeklyOffDays(0);

        public void SetWeeklyOffDays(int month, System.Collections.Generic.IEnumerable<int> days)
        {
            using (var conn = Open())
            using (var tx = conn.BeginTransaction())
            {
                using (var del = conn.CreateCommand())
                {
                    del.CommandText = "DELETE FROM WeeklyOff WHERE Month=$m;";
                    del.Parameters.AddWithValue("$m", month);
                    del.ExecuteNonQuery();
                }
                foreach (var d in days.Distinct())
                {
                    using (var ins = conn.CreateCommand())
                    {
                        ins.CommandText = "INSERT INTO WeeklyOff (Month, DayOfWeek) VALUES ($m, $d);";
                        ins.Parameters.AddWithValue("$m", month);
                        ins.Parameters.AddWithValue("$d", d);
                        ins.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
        }

        /// <summary>Effective off-days for a date: the month's rule if set, else the whole-year rule.</summary>
        public HashSet<int> ResolveWeeklyOffDays(DateTime date)
        {
            var monthRule = GetWeeklyOffDays(date.Month);
            return monthRule.Count > 0 ? monthRule : GetWeeklyOffDays(0);
        }

        // Adds a column to an existing table if it isn't there yet.
        private void MigrateColumn(SQLiteConnection conn, string table, string column, string definition)
        {
            bool has = false;
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = $"PRAGMA table_info({table});";
                using (var r = pragma.ExecuteReader())
                    while (r.Read())
                        if (string.Equals(r["name"] as string, column, StringComparison.OrdinalIgnoreCase)) has = true;
            }
            if (!has)
                using (var alter = conn.CreateCommand())
                {
                    alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
                    alter.ExecuteNonQuery();
                }
        }

        // Add Holiday.EnrollNumber to pre-existing databases (per-employee leave).
        private void MigrateHolidayEmployee(SQLiteConnection conn)
        {
            bool has = false;
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(Holiday);";
                using (var r = pragma.ExecuteReader())
                    while (r.Read())
                        if (string.Equals(r["name"] as string, "EnrollNumber", StringComparison.OrdinalIgnoreCase)) has = true;
            }
            if (!has)
                using (var alter = conn.CreateCommand())
                {
                    alter.CommandText = "ALTER TABLE Holiday ADD COLUMN EnrollNumber TEXT;";
                    alter.ExecuteNonQuery();
                }
        }

        // One-time migration of the legacy single-CSV setting into the Month=0 scope.
        private void MigrateWeeklyOff(SQLiteConnection conn)
        {
            using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM WeeklyOff;";
                if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;
            }
            string csv;
            using (var get = conn.CreateCommand())
            {
                get.CommandText = "SELECT Value FROM Setting WHERE Key=$k;";
                get.Parameters.AddWithValue("$k", WeeklyOffKey);
                csv = get.ExecuteScalar() as string;
            }
            if (string.IsNullOrWhiteSpace(csv)) return;
            foreach (var tok in csv.Split(','))
            {
                if (int.TryParse(tok.Trim(), out var d) && d >= 0 && d <= 6)
                {
                    using (var ins = conn.CreateCommand())
                    {
                        ins.CommandText = "INSERT OR IGNORE INTO WeeklyOff (Month, DayOfWeek) VALUES (0, $d);";
                        ins.Parameters.AddWithValue("$d", d);
                        ins.ExecuteNonQuery();
                    }
                }
            }
        }

        private static void BindHoliday(SQLiteCommand cmd, Holiday h)
        {
            cmd.Parameters.AddWithValue("$date", h.Date.ToString(DateFormat));
            cmd.Parameters.AddWithValue("$label", (object)h.Label ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rec", h.Recurring ? 1 : 0);
            cmd.Parameters.AddWithValue("$enroll", string.IsNullOrEmpty(h.EnrollNumber) ? (object)DBNull.Value : h.EnrollNumber);
        }

        /// <summary>
        /// Holidays that fall on the given date — each as (EnrollNumber, Reason).
        /// EnrollNumber null/empty = company-wide; otherwise it applies to that employee only.
        /// </summary>
        public List<KeyValuePair<string, string>> GetHolidaysOn(DateTime date)
        {
            var list = new List<KeyValuePair<string, string>>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT EnrollNumber, Label FROM Holiday
WHERE (Recurring = 0 AND Date = $exact) OR (Recurring = 1 AND substr(Date, 6, 5) = $md);";
                cmd.Parameters.AddWithValue("$exact", date.ToString(DateFormat));
                cmd.Parameters.AddWithValue("$md", date.ToString("MM-dd"));
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        list.Add(new KeyValuePair<string, string>(
                            r.IsDBNull(0) ? null : r.GetString(0),
                            r.IsDBNull(1) ? null : r.GetString(1)));
            }
            return list;
        }

        private static void BindShift(SQLiteCommand cmd, Shift s)
        {
            cmd.Parameters.AddWithValue("$name", (object)s.Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$start", (object)s.StartTime ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$end", (object)s.EndTime ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$grace", s.GraceMinutes);
            cmd.Parameters.AddWithValue("$weekend", (object)s.WeekendDays ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$active", s.Active ? 1 : 0);
        }

        private static void BindEmployee(SQLiteCommand cmd, Employee e)
        {
            cmd.Parameters.AddWithValue("$enroll", e.EnrollNumber ?? string.Empty);
            cmd.Parameters.AddWithValue("$name", (object)e.Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cnic", (object)e.Cnic ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$dept", (object)e.Department ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$desig", (object)e.Designation ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$contact", (object)e.Contact ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$email", (object)e.Email ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$shift", (object)e.ShiftId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$active", e.Active ? 1 : 0);
            cmd.Parameters.AddWithValue("$salary", e.Salary);
        }

        private SQLiteConnection Open()
        {
            var conn = new SQLiteConnection(_connectionString);
            conn.Open();
            return conn;
        }

        private static DateTime ParseTime(string s)
            => DateTime.TryParseExact(s, TimeFormat, null,
                System.Globalization.DateTimeStyles.None, out var dt)
                ? dt
                : (DateTime.TryParse(s, out var fallback) ? fallback : default);
    }
}
