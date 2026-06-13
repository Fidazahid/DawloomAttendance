using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DawloomAttendance.Data;

namespace DawloomAttendance.Services
{
    /// <summary>
    /// Local database backups: a daily auto-backup on startup plus on-demand backup
    /// and restore. Backups are timestamped DB copies in a configurable folder, with
    /// a retained-count limit.
    /// </summary>
    public static class BackupService
    {
        private const string DirKey = "BackupDir";
        private const int KeepCount = 30;

        public static string GetBackupDir(AppDb db)
        {
            var d = db.GetSetting(DirKey);
            if (string.IsNullOrWhiteSpace(d))
                d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DawloomAttendance", "backups");
            return d;
        }

        public static void SetBackupDir(AppDb db, string dir) => db.SetSetting(DirKey, dir);

        /// <summary>Creates a timestamped backup and prunes old ones. Returns the file path.</summary>
        public static string RunBackup(AppDb db)
        {
            var dir = GetBackupDir(db);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "dawloom-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".db");
            db.BackupTo(path);
            Prune(dir);
            return path;
        }

        /// <summary>Backs up once per day (no-op if a backup already exists for today). Returns true if it ran.</summary>
        public static bool RunDailyIfNeeded(AppDb db)
        {
            var dir = GetBackupDir(db);
            Directory.CreateDirectory(dir);
            string todayPrefix = "dawloom-" + DateTime.Now.ToString("yyyyMMdd");
            if (Directory.GetFiles(dir, todayPrefix + "*.db").Length > 0) return false;
            RunBackup(db);
            return true;
        }

        public static List<FileInfo> ListBackups(AppDb db)
        {
            var dir = GetBackupDir(db);
            if (!Directory.Exists(dir)) return new List<FileInfo>();
            return new DirectoryInfo(dir).GetFiles("dawloom-*.db")
                .OrderByDescending(f => f.LastWriteTime).ToList();
        }

        /// <summary>Replaces the live database with a backup copy. The app should restart afterwards.</summary>
        public static void Restore(string backupPath, string dbPath)
        {
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            File.Copy(backupPath, dbPath, true);
        }

        private static void Prune(string dir)
        {
            foreach (var f in new DirectoryInfo(dir).GetFiles("dawloom-*.db")
                         .OrderByDescending(f => f.LastWriteTime).Skip(KeepCount))
            {
                try { f.Delete(); } catch { /* best effort */ }
            }
        }
    }
}
