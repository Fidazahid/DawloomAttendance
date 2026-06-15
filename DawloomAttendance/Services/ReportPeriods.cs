using System;

namespace DawloomAttendance.Services
{
    /// <summary>
    /// Pure date math for the reporting periods. The work week runs <b>Saturday→Friday</b>.
    /// "Previous" always means the period immediately before the one containing the given day,
    /// so a report stays "due" until that period actually completes (and a missed Monday is
    /// still caught on Tuesday, etc.).
    /// </summary>
    public static class ReportPeriods
    {
        /// <summary>The Saturday that starts the Sat–Fri week containing <paramref name="day"/>.</summary>
        public static DateTime WeekStart(DateTime day)
        {
            // Saturday=6 → 0 offset, Sunday=0 → 1, … Friday=5 → 6.
            int daysSinceSaturday = ((int)day.DayOfWeek + 1) % 7;
            return day.Date.AddDays(-daysSinceSaturday);
        }

        /// <summary>The previous completed Sat–Fri week relative to <paramref name="today"/>.</summary>
        public static (DateTime From, DateTime To) PreviousWeek(DateTime today)
        {
            var currentStart = WeekStart(today);
            return (currentStart.AddDays(-7), currentStart.AddDays(-1));
        }

        /// <summary>Stable key for a week, identified by its Saturday start date.</summary>
        public static string WeekKey(DateTime weekFrom) => "W" + weekFrom.Date.ToString("yyyy-MM-dd");

        /// <summary>The previous calendar month relative to <paramref name="today"/> (first..last day).</summary>
        public static (DateTime From, DateTime To) PreviousMonth(DateTime today)
        {
            var firstOfThisMonth = new DateTime(today.Year, today.Month, 1);
            var to = firstOfThisMonth.AddDays(-1);
            var from = new DateTime(to.Year, to.Month, 1);
            return (from, to);
        }

        /// <summary>Stable key for a month, from any day within it.</summary>
        public static string MonthKey(DateTime monthDay) => "M" + monthDay.ToString("yyyy-MM");
    }
}
