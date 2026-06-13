using System;

namespace DawloomAttendance.Data.Entities
{
    /// <summary>
    /// A non-working day. If <see cref="Recurring"/> is true the holiday repeats every
    /// year on the same month/day (the year is ignored when matching).
    /// </summary>
    public class Holiday
    {
        public long Id { get; set; }
        public DateTime Date { get; set; } = DateTime.Today;
        public string Label { get; set; }   // shown as the reason
        public bool Recurring { get; set; }

        /// <summary>Enroll # this applies to; null/empty = all employees (company-wide).</summary>
        public string EnrollNumber { get; set; }
    }
}
