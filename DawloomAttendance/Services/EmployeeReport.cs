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

        /// <summary>Summary key/value pairs (working days, present, leave, attendance %, …).</summary>
        public static List<KeyValuePair<string, string>> BuildSummary(IEnumerable<DailyAttendance> days)
        {
            var list = (days ?? Enumerable.Empty<DailyAttendance>()).ToList();

            int workingDays = list.Count(d => d.IsWorkingDay);
            int present = list.Count(d => d.Present);
            int absent = list.Count(d => d.Absent);
            int lateCount = list.Count(d => d.Late);
            int lateMinutes = list.Where(d => d.Late).Sum(d => d.LateMinutes);
            double worked = list.Sum(d => d.WorkedHours);
            double overtime = list.Sum(d => d.OvertimeHours);
            int paidLeave = list.Count(d => d.IsLeave && d.PaidLeave);
            int unpaidLeave = list.Count(d => d.IsLeave && !d.PaidLeave);
            double pct = workingDays > 0 ? Math.Round(100.0 * present / workingDays, 1) : 0;

            return new List<KeyValuePair<string, string>>
            {
                Kv("Working days", workingDays.ToString()),
                Kv("Present", present.ToString()),
                Kv("Absent", absent.ToString()),
                Kv("Paid leave", paidLeave.ToString()),
                Kv("Unpaid leave", unpaidLeave.ToString()),
                Kv("Late count", lateCount.ToString()),
                Kv("Late time", DurationFormat.Minutes(lateMinutes)),
                Kv("Worked", DurationFormat.Hours(worked)),
                Kv("Overtime", DurationFormat.Hours(overtime)),
                Kv("Attendance %", pct.ToString("0.#") + "%"),
            };
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

        /// <summary>Writes the full per-employee report PDF for the given period.</summary>
        public static void WritePdf(string path, Employee emp, DateTime from, DateTime to, IEnumerable<DailyAttendance> days)
        {
            PdfExport.WriteEmployeeReport(
                path,
                emp.Name ?? emp.EnrollNumber,
                emp.EnrollNumber,
                $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}",
                BuildSummary(days),
                DailyHeaders,
                BuildDailyRows(days));
        }

        private static KeyValuePair<string, string> Kv(string k, string v) => new KeyValuePair<string, string>(k, v);
    }
}
