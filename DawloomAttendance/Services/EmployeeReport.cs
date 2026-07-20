using System;
using System.Collections.Generic;
using System.Linq;
using DawloomAttendance.Data.Entities;

namespace DawloomAttendance.Services
{
    /// <summary>
    /// Turns one employee's computed daily attendance over a period into the summary
    /// key/values + daily-detail rows used by the emailed report PDF.
    /// </summary>
    public static class EmployeeReport
    {
        public static readonly string[] DailyHeaders =
            { "Date", "Status", "Check In", "Check Out", "Late", "Worked" };

        /// <summary>
        /// Summary key/value pairs (working days, present, leave, attendance %, …).
        /// The percentage comes from <see cref="AttendancePercentage"/> — the same call the
        /// Reports and Salary screens make, so the emailed number always matches the screen.
        /// </summary>
        public static List<KeyValuePair<string, string>> BuildSummary(
            IEnumerable<DailyAttendance> days, Shift shift, bool allowAbove100, bool showOvertime = true)
        {
            var list = (days ?? Enumerable.Empty<DailyAttendance>()).ToList();

            int absent = list.Count(d => d.Absent);
            int lateCount = list.Count(d => d.Late);
            int lateMinutes = list.Where(d => d.Late).Sum(d => d.LateMinutes);
            double overtime = list.Sum(d => d.OvertimeHours);
            int paidLeave = list.Count(d => d.IsLeave && d.PaidLeave);
            int unpaidLeave = list.Count(d => d.IsLeave && !d.PaidLeave);
            var pct = AttendancePercentage.Compute(list, shift, allowAbove100);

            var rows = new List<KeyValuePair<string, string>>
            {
                Kv("Working days", pct.WorkingDays.ToString()),
                Kv("Present", list.Count(d => d.Present).ToString()),
                Kv("Absent", absent.ToString()),
                Kv("Paid leave", paidLeave.ToString()),
                Kv("Unpaid leave", unpaidLeave.ToString()),
                Kv("Late count", lateCount.ToString()),
                Kv("Late time", DurationFormat.Minutes(lateMinutes)),
                Kv("Expected hours", DurationFormat.Hours(pct.ExpectedHours)),
                Kv("Worked", DurationFormat.Hours(pct.ActualHours)),
                Kv("Attendance %", pct.Display),
            };

            // Overtime turned off = it is not shown on any report, not just left out of pay.
            if (showOvertime)
                rows.Insert(rows.Count - 1, Kv("Overtime", DurationFormat.Hours(overtime)));

            // Only shown when it actually happened, so it reads as an action item.
            if (pct.MissingCheckoutDays > 0)
                rows.Add(Kv("Days with no check-out", pct.MissingCheckoutDays + " (counted as 0 hours)"));

            return rows;
        }

        /// <summary>Daily-detail rows (one per day, oldest first) matching <see cref="DailyHeaders"/>.</summary>
        public static List<IList<string>> BuildDailyRows(IEnumerable<DailyAttendance> days)
        {
            return (days ?? Enumerable.Empty<DailyAttendance>())
                .OrderBy(d => d.Date)
                .Select(d => (IList<string>)new List<string>
                {
                    d.Date.ToString("yyyy-MM-dd"),
                    d.Status,
                    d.FirstPunch?.ToString("HH:mm:ss") ?? "",
                    d.LastPunch?.ToString("HH:mm:ss") ?? "",
                    d.Late ? DurationFormat.Minutes(d.LateMinutes) : "",
                    DurationFormat.Hours(d.WorkedHours),
                })
                .ToList();
        }

        /// <summary>
        /// Writes the full per-employee report PDF for the given period.
        /// <paramref name="shift"/> is the employee's assigned shift (Employee.ShiftId) —
        /// it sets the expected hours the attendance % is measured against.
        /// </summary>
        public static void WritePdf(string path, Employee emp, DateTime from, DateTime to,
            IEnumerable<DailyAttendance> days, Shift shift, bool allowAbove100, bool showOvertime = true)
        {
            PdfExport.WriteEmployeeReport(
                path,
                emp.Name ?? emp.EnrollNumber,
                emp.EnrollNumber,
                $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}",
                BuildSummary(days, shift, allowAbove100, showOvertime),
                DailyHeaders,
                BuildDailyRows(days));
        }

        private static KeyValuePair<string, string> Kv(string k, string v) => new KeyValuePair<string, string>(k, v);
    }
}
