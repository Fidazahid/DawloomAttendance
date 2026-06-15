using System;
using DawloomAttendance.Data;
using DawloomAttendance.Data.Entities;
using DawloomAttendance.Device;

namespace DawloomAttendance.Services
{
    /// <summary>
    /// Reads the transactions stored on the device and merges any that aren't
    /// already in the local database. Relies on <see cref="AppDb.InsertPunchIfNew"/>
    /// for dedup, so it is safe to run repeatedly and safe to overlap with live
    /// capture — duplicates (same enroll + timestamp) are skipped, not re-inserted.
    /// </summary>
    public static class BackfillService
    {
        public sealed class BackfillResult
        {
            public int Read { get; set; }      // records returned by the device
            public int Inserted { get; set; }  // new rows written
            public int Skipped { get; set; }   // already present (deduped)

            public override string ToString()
                => $"read {Read}, inserted {Inserted}, skipped {Skipped} (already stored)";
        }

        public static BackfillResult Run(IZkDevice device, AppDb db, int machineNumber)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            if (db == null) throw new ArgumentNullException(nameof(db));

            var result = new BackfillResult();
            var capturedAt = DateTime.Now;

            foreach (var p in device.ReadAllLogs(machineNumber))
            {
                result.Read++;

                // Don't resurrect a punch the user deliberately deleted or whose time
                // they edited — the device still holds the original, but we suppress it.
                if (db.IsPunchSuppressed(p.EnrollNumber, p.Timestamp)) { result.Skipped++; continue; }

                var raw = new RawPunch
                {
                    EnrollNumber = p.EnrollNumber,
                    Timestamp    = p.Timestamp,
                    AttState     = p.AttState,
                    VerifyMethod = p.VerifyMethod,
                    WorkCode     = p.WorkCode,
                    IsValid      = p.IsValid,
                    CapturedAt   = capturedAt,
                    Source       = "backfill"
                };

                if (db.InsertPunchIfNew(raw)) result.Inserted++;
                else result.Skipped++;
            }

            return result;
        }
    }
}
