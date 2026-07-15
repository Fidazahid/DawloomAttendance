using System.Collections.Generic;

namespace DawloomAttendance.Data.Entities
{
    /// <summary>
    /// A shift template (e.g. "Day 9-7"). Times are stored as "HH:mm" strings and
    /// parsed by the Phase 3 calculation engine. WeekendDays is a CSV of day numbers
    /// (0=Sunday … 6=Saturday), e.g. "0" = Sundays off, "5,0" = Fri+Sun off — matching
    /// System.DayOfWeek so the calc can compare directly to (int)date.DayOfWeek.
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

        // ---- Day-name view over WeekendDays --------------------------------------
        // The UI binds seven checkboxes (Sun…Sat) to these so nobody has to remember
        // the 0=Sun…6=Sat numbering. WeekendDays stays the single source of truth.

        private bool HasDay(int day)
        {
            if (string.IsNullOrWhiteSpace(WeekendDays)) return false;
            foreach (var tok in WeekendDays.Split(','))
                if (int.TryParse(tok.Trim(), out var n) && n == day) return true;
            return false;
        }

        private void SetDay(int day, bool on)
        {
            var set = new SortedSet<int>();
            if (!string.IsNullOrWhiteSpace(WeekendDays))
                foreach (var tok in WeekendDays.Split(','))
                    if (int.TryParse(tok.Trim(), out var n) && n >= 0 && n <= 6) set.Add(n);
            if (on) set.Add(day); else set.Remove(day);
            WeekendDays = string.Join(",", set);
        }

        public bool WeekendSun { get => HasDay(0); set => SetDay(0, value); }
        public bool WeekendMon { get => HasDay(1); set => SetDay(1, value); }
        public bool WeekendTue { get => HasDay(2); set => SetDay(2, value); }
        public bool WeekendWed { get => HasDay(3); set => SetDay(3, value); }
        public bool WeekendThu { get => HasDay(4); set => SetDay(4, value); }
        public bool WeekendFri { get => HasDay(5); set => SetDay(5, value); }
        public bool WeekendSat { get => HasDay(6); set => SetDay(6, value); }
    }
}
