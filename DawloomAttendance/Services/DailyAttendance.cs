using System;

namespace DawloomAttendance.Services
{
    public enum DayCategory
    {
        FullDay,
        HalfDay,
        Absent,
        Off        // weekend or holiday
    }

    /// <summary>
    /// Computed attendance for one employee on one day. Produced by
    /// <see cref="AttendanceCalculator"/> from raw punches + shift + day type.
    /// </summary>
    public class DailyAttendance
    {
        public string EnrollNumber { get; set; }
        public DateTime Date { get; set; }

        public bool IsWorkingDay { get; set; }   // false on weekend/holiday
        public int PunchCount { get; set; }
        public DateTime? FirstPunch { get; set; }
        public DateTime? LastPunch { get; set; }

        public bool Present { get; set; }         // had at least one punch
        public bool Absent { get; set; }          // working day, no punch
        public bool Late { get; set; }
        public int LateMinutes { get; set; }      // minutes after scheduled start (when late beyond grace)
        public bool EarlyDeparture { get; set; }
        public int EarlyMinutes { get; set; }     // minutes before scheduled end

        public double WorkedHours { get; set; }   // last - first punch (gross)
        public double OvertimeHours { get; set; } // beyond shift end (or all hours on an off day)

        public DayCategory Category { get; set; }
        public string Notes { get; set; }

        /// <summary>Why the day is off (e.g. "Weekend", "Eid", a leave reason).</summary>
        public string OffReason { get; set; }

        public string Status
        {
            get
            {
                if (Category == DayCategory.Off)
                {
                    string label = string.IsNullOrEmpty(OffReason) ? "Off" : "Off — " + OffReason;
                    return Present ? label + " (worked)" : label;
                }
                if (Absent) return "Absent";
                if (Category == DayCategory.HalfDay) return Late ? "Half / Late" : "Half-day";
                return Late ? "Late" : "Present";
            }
        }
    }
}
