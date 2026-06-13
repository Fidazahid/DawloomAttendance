using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
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
);";
                cmd.ExecuteNonQuery();
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
