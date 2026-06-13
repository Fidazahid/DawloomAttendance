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
    public sealed class AppDb
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
    ShiftId       INTEGER,
    Active        INTEGER NOT NULL DEFAULT 1,
    CreatedAt     TEXT NOT NULL
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
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    Date      TEXT NOT NULL,
    Label     TEXT,
    Recurring INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS Setting (
    Key   TEXT PRIMARY KEY,
    Value TEXT
);

CREATE TABLE IF NOT EXISTS WeeklyOff (
    Month     INTEGER NOT NULL,   -- 0 = whole year, 1-12 = specific month
    DayOfWeek INTEGER NOT NULL,   -- 0=Sun … 6=Sat
    PRIMARY KEY (Month, DayOfWeek)
);";
                cmd.ExecuteNonQuery();

                MigrateWeeklyOff(conn);
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
SELECT Id, EnrollNumber, Name, Cnic, Department, Designation, Contact, ShiftId, Active, CreatedAt
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
                            CreatedAt = ParseTime(r.GetString(9))
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
INSERT INTO Employee (EnrollNumber, Name, Cnic, Department, Designation, Contact, ShiftId, Active, CreatedAt)
VALUES ($enroll, $name, $cnic, $dept, $desig, $contact, $shift, $active, $created);";
                BindEmployee(cmd, e);
                cmd.Parameters.AddWithValue("$created", (e.CreatedAt == default ? DateTime.Now : e.CreatedAt).ToString(TimeFormat));
                cmd.ExecuteNonQuery();
                return conn.LastInsertRowId;
            }
        }

        public void UpdateEmployee(Employee e)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
UPDATE Employee SET EnrollNumber=$enroll, Name=$name, Cnic=$cnic, Department=$dept,
    Designation=$desig, Contact=$contact, ShiftId=$shift, Active=$active
WHERE Id=$id;";
                BindEmployee(cmd, e);
                cmd.Parameters.AddWithValue("$id", e.Id);
                cmd.ExecuteNonQuery();
            }
        }

        public void DeleteEmployee(long id)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM Employee WHERE Id=$id;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
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
                cmd.CommandText = "SELECT Id, Date, Label, Recurring FROM Holiday ORDER BY Date;";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new Holiday
                        {
                            Id = r.GetInt64(0),
                            Date = ParseTime(r.GetString(1)),
                            Label = r.IsDBNull(2) ? null : r.GetString(2),
                            Recurring = r.GetInt32(3) != 0
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
                cmd.CommandText = "INSERT INTO Holiday (Date, Label, Recurring) VALUES ($date, $label, $rec);";
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
                cmd.CommandText = "UPDATE Holiday SET Date=$date, Label=$label, Recurring=$rec WHERE Id=$id;";
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
WHERE (Recurring = 0 AND Date = $exact)
   OR (Recurring = 1 AND substr(Date, 6, 5) = $md);";
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
        public void DeletePunch(long id)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM RawPunch WHERE Id=$id;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Corrects a punch's in/out type (e.g. a stray second check-in → check-out).</summary>
        public void UpdatePunchState(long id, int attState)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE RawPunch SET AttState=$s WHERE Id=$id;";
                cmd.Parameters.AddWithValue("$s", attState);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
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
            cmd.Parameters.AddWithValue("$shift", (object)e.ShiftId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$active", e.Active ? 1 : 0);
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
