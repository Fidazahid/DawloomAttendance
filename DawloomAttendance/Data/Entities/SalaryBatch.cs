using System;

namespace DawloomAttendance.Data.Entities
{
    /// <summary>
    /// One generated month of salary slips (the Salary tab list). Identified by its
    /// month key (e.g. "M2026-07"). Slips are snapshotted at generation so re-generating
    /// reproduces the exact same figures and loan deductions. A batch may carry a
    /// scheduled auto-send date: on/after that date the slips email once (missed → caught
    /// on the next launch), skipping interns (employees with no salary).
    /// </summary>
    public class SalaryBatch
    {
        public string PeriodKey { get; set; }        // M2026-07
        public DateTime PeriodFrom { get; set; }
        public DateTime PeriodTo { get; set; }
        public DateTime GeneratedAt { get; set; }
        public DateTime? ScheduleDate { get; set; }
        public bool ScheduleEnabled { get; set; }
        public DateTime? SentAt { get; set; }

        /// <summary>Month/year label for the list, e.g. "July 2026".</summary>
        public string MonthYear => PeriodFrom.ToString("MMMM yyyy");
    }
}
