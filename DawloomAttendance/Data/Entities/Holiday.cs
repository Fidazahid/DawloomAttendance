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
        public string Label { get; set; }
        public bool Recurring { get; set; }
    }
}
