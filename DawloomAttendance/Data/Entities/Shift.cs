namespace DawloomAttendance.Data.Entities
{
    /// <summary>
    /// A shift template (e.g. "Day 9-7"). Times are stored as "HH:mm" strings and
    /// parsed by the Phase 3 calculation engine. WeekendDays is a CSV of day numbers
    /// (0=Sunday … 6=Saturday), e.g. "0" = Sundays off, "5,0" = Fri+Sun off.
    /// </summary>
    public class Shift
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string StartTime { get; set; } = "09:00";
        public string EndTime { get; set; } = "19:00";
        public int GraceMinutes { get; set; } = 15;
        public string WeekendDays { get; set; } = "0";
        public bool Active { get; set; } = true;

        /// <summary>Shown in the employee shift dropdown.</summary>
        public string Display => $"{Name}  ({StartTime}-{EndTime})";
    }
}
